using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dawa.Auth;
using Dawa.Binary;
using Dawa.Crypto;
using Dawa.Messages;
using Dawa.Proto;
using Dawa.Transport;
using Microsoft.Extensions.Logging;

namespace Dawa.Noise;

/// <summary>
/// Handles the WhatsApp Noise XX handshake and post-handshake encrypted transport.
/// After the handshake completes, all frames are encrypted/decrypted with AES-GCM
/// using keys derived from the Noise session.
/// </summary>
public sealed class NoiseProcessor : IAsyncDisposable
{
    // WhatsApp prologue bytes: "WA" + protocol version 6 + dict version 3
    // Source: Baileys NOISE_WA_HEADER = Buffer.from([87, 65, 6, DICT_VERSION]) where DICT_VERSION=3
    private static readonly byte[] WA_PROLOGUE = [0x57, 0x41, 0x06, 0x03];

    private readonly FrameSocket _socket;
    private readonly AuthState _auth;
    private readonly WhatsAppClientOptions _options;
    private readonly ILogger _logger;

    // Transport keys (set after handshake)
    private byte[]? _sendKey;
    private byte[]? _recvKey;
    private ulong _sendCounter;
    private ulong _recvCounter;
    private bool _handshakeDone;

    // Ephemeral key pair (generated fresh per connection)
    private readonly byte[] _ephemeralPriv;
    private readonly byte[] _ephemeralPub;

    public event EventHandler<string>? QRCodeGenerated;
    public event EventHandler<AuthState>? Authenticated;
    public event EventHandler<IncomingMessage>? MessageReceived;

    public NoiseProcessor(FrameSocket socket, AuthState auth, WhatsAppClientOptions options, ILogger logger)
    {
        _socket = socket;
        _auth = auth;
        _options = options;
        _logger = logger;

        (_ephemeralPriv, _ephemeralPub) = Curve25519Helper.GenerateKeyPair();
    }

    // ─── Handshake ───────────────────────────────────────────────────────────

    /// <summary>
    /// Performs the complete Noise XX handshake with the WhatsApp server.
    /// </summary>
    public async Task PerformHandshakeAsync(CancellationToken ct)
    {
        var noise = new NoiseState();

        // WhatsApp's modified Noise XX initialization (matches Baileys noise-handler.js):
        // 1. Mix prologue (WA header bytes: "WA" + version 6 + dict 3)
        noise.MixHash(WA_PROLOGUE);
        // 2. Mix OUR ephemeral public key (NOT the static noise key — Baileys does this at init)
        noise.MixHash(_ephemeralPub);

        // ── Phase 1: Send ClientHello with our ephemeral key ──────────────────

        var clientHello = new ClientHello { Ephemeral = _ephemeralPub };
        var handshake1 = new HandshakeMessage { ClientHello = clientHello };
        await SendHandshakeMessageAsync(handshake1, ct);

        _logger.LogDebug("Noise: Sent ClientHello (ephemeral key)");

        // ── Phase 2: Receive ServerHello ──────────────────────────────────────
        var serverFrame = await _socket.ReceiveFrameAsync(ct)
            ?? throw new InvalidOperationException("Server closed connection during handshake.");

        var handshakeResp = HandshakeMessage.ParseFrom(serverFrame);
        var serverHello = handshakeResp.ServerHello
            ?? throw new InvalidOperationException("Expected ServerHello.");

        var serverEphemeral = serverHello.Ephemeral;
        var serverStaticEnc = serverHello.Static;
        var serverPayloadEnc = serverHello.Payload;

        // Mix server ephemeral
        noise.MixHash(serverEphemeral);
        // DH(our_ephemeral, server_ephemeral)
        var dh1 = Curve25519Helper.DH(_ephemeralPriv, serverEphemeral);
        noise.MixKey(dh1);

        // Decrypt server static key
        var serverStaticPub = noise.DecryptWithAssociatedData(serverStaticEnc);
        // DH(our_ephemeral, server_static)
        var dh2 = Curve25519Helper.DH(_ephemeralPriv, serverStaticPub);
        noise.MixKey(dh2);

        // Decrypt server payload (certificate / metadata)
        var serverPayload = noise.DecryptWithAssociatedData(serverPayloadEnc);
        _logger.LogDebug("Noise: Received ServerHello, decrypted server payload ({Length} bytes)", serverPayload.Length);

        // ── Phase 3: Send ClientFinish ─────────────────────────────────────────
        // Encrypt our static (noise) public key
        var encStaticPub = noise.EncryptWithAssociatedData(_auth.NoiseKeyPublic);
        // DH(our_static, server_ephemeral)
        var dh3 = Curve25519Helper.DH(_auth.NoiseKeyPrivate, serverEphemeral);
        noise.MixKey(dh3);

        // Build client payload and encrypt it
        var clientPayload = BuildClientPayload();
        var encPayload = noise.EncryptWithAssociatedData(clientPayload);

        _logger.LogInformation("Noise: ClientFinish encStaticPub({Len})={Hex}",
            encStaticPub.Length, BitConverter.ToString(encStaticPub));
        _logger.LogInformation("Noise: ClientFinish encPayload({Len})={Hex}",
            encPayload.Length, BitConverter.ToString(encPayload));
        _logger.LogInformation("Noise: ClientPayload raw({Len})={Hex}",
            clientPayload.Length, BitConverter.ToString(clientPayload));

        var clientFinish = new ClientFinish
        {
            Static = encStaticPub,
            Payload = encPayload,
        };
        var handshake3 = new HandshakeMessage { ClientFinish = clientFinish };
        await SendHandshakeMessageAsync(handshake3, ct);

        _logger.LogDebug("Noise: Sent ClientFinish");

        // ── Finalize: derive transport keys ───────────────────────────────────
        (_sendKey, _recvKey) = noise.Split();
        _sendCounter = 0;
        _recvCounter = 0;
        _handshakeDone = true;

        _logger.LogInformation("Noise: Handshake complete. Transport keys established. sendKey[0..4]={S} recvKey[0..4]={R}",
            BitConverter.ToString(_sendKey, 0, 4),
            BitConverter.ToString(_recvKey, 0, 4));

        // Now handle the post-handshake authentication (QR or session restore)
        await HandlePostHandshakeAsync(ct);
    }

    // ─── Post-Handshake Auth ────────────────────────────────────────────────

    private Task HandlePostHandshakeAsync(CancellationToken ct)
    {
        // After the Noise handshake + ClientFinish, the server drives the flow.
        // For fresh connections: server processes our devicePairingData from ClientPayload,
        //   then sends an IQ pair-device with QR ref.
        // For existing sessions: server acknowledges the session via "success" or "stream:features".
        // We just start listening — no proactive sends needed here.
        if (_auth.IsFresh)
            _logger.LogInformation("Fresh session — waiting for server QR pair-device IQ.");
        else
            _logger.LogInformation("Restoring session for {Me}", _auth.Me?.Id);

        return Task.CompletedTask;
    }

    // ─── Receive loop ───────────────────────────────────────────────────────

    /// <summary>
    /// Continuously reads and processes incoming frames. Call this on a background task.
    /// </summary>
    public async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket.IsConnected)
        {
            try
            {
                var frame = await _socket.ReceiveFrameAsync(ct);
                if (frame == null) break;

                _logger.LogInformation("Noise: Received raw frame ({Len} bytes), first bytes={Hex}",
                    frame.Length, BitConverter.ToString(frame, 0, Math.Min(32, frame.Length)));

                var decrypted = DecryptFrame(frame);
                // Baileys protocol: first byte is a flags byte.
                // Bit 1 (value 2) = payload is zlib raw-deflate compressed.
                // Always strip this byte before decoding the binary node.
                var nodeData = StripFlagsAndDecompress(decrypted);
                _logger.LogInformation("Noise: Decrypted frame ({Len} bytes), first bytes={Hex}",
                    decrypted.Length, BitConverter.ToString(decrypted, 0, Math.Min(32, decrypted.Length)));

                var node = BinaryNodeDecoder.Decode(nodeData);

                // Server sent StreamEnd — graceful close, stop receive loop.
                if (node.Tag == BinaryNodeDecoder.StreamEndSentinel)
                {
                    _logger.LogInformation("Server sent stream-end, closing.");
                    break;
                }

                _logger.LogInformation("Received node: {Node}", node.ToString());
                await HandleNodeAsync(node, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in receive loop");
                break;
            }
        }
        _logger.LogInformation("Receive loop ended.");
    }

    private async Task HandleNodeAsync(BinaryNode node, CancellationToken ct)
    {
        switch (node.Tag)
        {
            case "chat" when node.GetAttr("add") == "@xmlstreamstart":
                // Multi-device: server embeds pair-device refs directly in the xmlstreamstart frame.
                // Structure: <chat add="@xmlstreamstart"><container><ref>[102 bytes]</ref>×6</container></chat>
                await HandleXmlStreamStartAsync(node, ct);
                break;
            case "iq":
                await HandleIQAsync(node, ct);
                break;
            case "message":
                HandleMessageNode(node);
                break;
            case "notification":
                await HandleNotificationAsync(node, ct);
                break;
            case "success":
                _logger.LogInformation("Session authenticated successfully.");
                Authenticated?.Invoke(this, _auth);
                break;
            case "failure":
                _logger.LogWarning("Authentication failure: {Reason}", node.GetAttr("reason"));
                break;
            case "stream:error":
                _logger.LogError("Stream error: {Code}", node.GetAttr("code"));
                break;
            default:
                _logger.LogDebug("Unhandled node tag: {Tag}", node.Tag);
                break;
        }
    }

    private async Task HandleXmlStreamStartAsync(BinaryNode chat, CancellationToken ct)
    {
        // In the WhatsApp multi-device protocol, the server packs pair-device ref blobs
        // directly into the first post-handshake frame as children of the xmlstreamstart node.
        // Walk the children to find the first binary payload — that is the first QR ref.
        byte[]? refBytes = null;
        foreach (var container in chat.Children)
        {
            foreach (var child in container.Children)
            {
                if (child.Data is { Length: > 0 })
                {
                    refBytes = child.Data;
                    break;
                }
            }
            if (refBytes != null) break;
        }

        if (refBytes == null)
        {
            _logger.LogWarning("xmlstreamstart node contained no ref blobs — cannot generate QR.");
            return;
        }

        // The ref blob is raw bytes; base64-encode it to form the ref string.
        var ref_ = Convert.ToBase64String(refBytes);
        var qrParts = new[]
        {
            ref_,
            Convert.ToBase64String(_auth.NoiseKeyPublic),
            Convert.ToBase64String(_auth.SignedIdentityKeyPublic),
            Convert.ToBase64String(_auth.AdvSecretKey),
        };
        var qrString = string.Join(",", qrParts);

        _logger.LogInformation("QR Code ready (from xmlstreamstart, ref={RefLen} bytes).", refBytes.Length);
        QRCodeGenerated?.Invoke(this, qrString);
        // Connection stays open — server waits for QR scan (up to ~20s per ref).
    }

    private async Task HandleIQAsync(BinaryNode iq, CancellationToken ct)
    {
        var type = iq.GetAttr("type");
        if (type == "get")
        {
            // Respond to keep-alive pings from the server
            if (iq.FindChild("ping") != null)
            {
                var pong = new BinaryNode("iq", new()
                {
                    ["id"]   = iq.GetAttr("id") ?? "",
                    ["type"] = "result",
                    ["to"]   = iq.GetAttr("from") ?? "s.whatsapp.net",
                });
                await SendNodeAsync(pong, ct);
                _logger.LogDebug("Responded to server ping id={Id}", iq.GetAttr("id"));
            }
            return;
        }
        else if (type == "result")
        {
            // Check for pair-device result (QR code ref)
            var pairDevice = iq.FindChild("pair-device");
            if (pairDevice != null)
            {
                await HandlePairDeviceResultAsync(pairDevice, ct);
                return;
            }

            // Check for pair-success (phone scanned QR) — also handle type="result" path
            var pairSuccess = iq.FindChild("pair-success");
            if (pairSuccess != null)
            {
                await HandlePairSuccessAsync(iq, pairSuccess, ct);
                return;
            }
        }
        else if (type == "set")
        {
            // Server is initiating a request (e.g., pair-device from server side)
            var pairDevice = iq.FindChild("pair-device");
            if (pairDevice != null)
            {
                // Respond with an ack
                var ack = new BinaryNode("iq", new()
                {
                    ["id"] = iq.GetAttr("id") ?? "",
                    ["type"] = "result",
                    ["to"] = iq.GetAttr("from") ?? "s.whatsapp.net",
                });
                await SendNodeAsync(ack, ct);
                // Generate QR from the first ref in the pair-device node
                await HandlePairDeviceResultAsync(pairDevice, ct);
            }

            // pair-success arrives as type="set" (server-initiated), not type="result"
            var pairSuccess2 = iq.FindChild("pair-success");
            if (pairSuccess2 != null)
            {
                await HandlePairSuccessAsync(iq, pairSuccess2, ct);
            }
        }
    }

    private async Task HandlePairDeviceResultAsync(BinaryNode pairDevice, CancellationToken ct)
    {
        // Extract ref token from server — the ref is a binary blob encoded as base64 in the QR
        var refNode = pairDevice.FindChild("ref");
        if (refNode == null) return;

        // ref content arrives as raw bytes that are actually a UTF-8 string
        // (the server sends e.g. 102 ASCII chars of a base64-like token as byte[])
        // Do NOT base64-encode it again — decode the bytes as UTF-8.
        string ref_;
        if (refNode.Data != null)
            ref_ = System.Text.Encoding.UTF8.GetString(refNode.Data);
        else if (refNode.Text != null)
            ref_ = refNode.Text;
        else
            return;

        var qrParts = new[]
        {
            ref_,
            Convert.ToBase64String(_auth.NoiseKeyPublic),
            Convert.ToBase64String(_auth.SignedIdentityKeyPublic),
            Convert.ToBase64String(_auth.AdvSecretKey),
        };
        var qrString = string.Join(",", qrParts);

        _logger.LogInformation("QR Code ready for scanning.");
        QRCodeGenerated?.Invoke(this, qrString);
    }

    private async Task HandlePairSuccessAsync(BinaryNode iq, BinaryNode pairSuccess, CancellationToken ct)
    {
        var msgId = iq.GetAttr("id") ?? "";
        _logger.LogInformation("=== PAIR-SUCCESS RECEIVED === id={Id}", msgId);

        var platform = pairSuccess.GetAttr("platform") ?? "UNKNOWN";

        // Extract JID
        var deviceNode = pairSuccess.FindChild("device");
        var jid = deviceNode?.GetAttr("jid") ?? "";
        _logger.LogInformation("Paired as {Jid} on platform {Platform}", jid, platform);

        // ── ADV device-identity verification & signing ─────────────────────
        // Baileys: configureSuccessfulPairing() in validate-connection.js
        var devIdentityNode = pairSuccess.FindChild("device-identity");
        if (devIdentityNode?.Data == null)
        {
            _logger.LogError("pair-success missing device-identity content — cannot complete pairing.");
            return;
        }

        // 1. Decode ADVSignedDeviceIdentityHMAC
        var hmacMsg = ADVSignedDeviceIdentityHMAC.ParseFrom(devIdentityNode.Data);

        // 2. Verify HMAC-SHA256(details, advSecretKey)
        //    isHostedAccount = (hmacMsg.AccountType == 1) => prefix [6,5], else empty
        var isHosted = hmacMsg.AccountType == 1;
        var hmacInput = isHosted
            ? (new byte[] { 6, 5 }).Concat(hmacMsg.Details).ToArray()
            : hmacMsg.Details;
        var expectedHmac = HMACSHA256.HashData(_auth.AdvSecretKey, hmacInput);
        if (!expectedHmac.AsSpan().SequenceEqual(hmacMsg.Hmac))
        {
            _logger.LogError("ADV HMAC verification failed — pairing rejected.");
            return;
        }
        _logger.LogInformation("ADV HMAC verified OK.");

        // 3. Decode ADVSignedDeviceIdentity
        var account = ADVSignedDeviceIdentity.ParseFrom(hmacMsg.Details);

        // 4. Verify account signature: XEdDSA.Verify(accountSignatureKey, [6,0] + deviceDetails + identityPub, accountSignature)
        var accountMsg = new byte[] { 6, 0 }
            .Concat(account.Details)
            .Concat(_auth.SignedIdentityKeyPublic)
            .ToArray();
        if (!XEdDSA.Verify(account.AccountSignatureKey, accountMsg, account.AccountSignature))
        {
            _logger.LogError("Account signature verification failed — pairing rejected.");
            return;
        }
        _logger.LogInformation("Account signature verified OK.");

        // 5. Sign device identity: XEdDSA.Sign(identityPrivate, prefix + deviceDetails + identityPub + accountSigKey)
        var devicePrefix = isHosted ? new byte[] { 6, 6 } : new byte[] { 6, 1 };
        var deviceMsg = devicePrefix
            .Concat(account.Details)
            .Concat(_auth.SignedIdentityKeyPublic)
            .Concat(account.AccountSignatureKey)
            .ToArray();
        account.DeviceSignature = XEdDSA.Sign(_auth.SignedIdentityKeyPrivate, deviceMsg);
        _logger.LogInformation("Device signature created.");

        // 6. Decode ADVDeviceIdentity to get keyIndex
        var deviceIdentity = ADVDeviceIdentity.ParseFrom(account.Details);

        // 7. Re-encode ADVSignedDeviceIdentity (WITHOUT accountSignatureKey per Baileys protocol)
        var accountEnc = account.ToByteArrayForReply();

        // 8. Send pair-device-sign IQ as the result (this IS the ack — same msgId)
        var deviceIdentityNode2 = new BinaryNode("device-identity", new()
        {
            ["key-index"] = deviceIdentity.KeyIndex.ToString(),
        })
        {
            Content = accountEnc,
        };
        var pairDeviceSign = new BinaryNode("pair-device-sign")
        {
            Content = new List<BinaryNode> { deviceIdentityNode2 },
        };
        var reply = new BinaryNode("iq", new()
        {
            ["to"]   = "s.whatsapp.net",
            ["type"] = "result",
            ["id"]   = msgId,
        })
        {
            Content = new List<BinaryNode> { pairDeviceSign },
        };
        await SendNodeAsync(reply, ct);
        _logger.LogInformation("pair-device-sign sent (keyIndex={KeyIndex}).", deviceIdentity.KeyIndex);

        // 9. Update auth state
        _auth.Platform = platform;
        _auth.Me = new MeInfo { Id = jid };

        _logger.LogInformation("Firing Authenticated event — session established.");
        Authenticated?.Invoke(this, _auth);
    }

    private void HandleMessageNode(BinaryNode node)
    {
        var from = node.GetAttr("from") ?? "";
        var id = node.GetAttr("id") ?? "";
        var participant = node.GetAttr("participant");
        var pushName = node.GetAttr("notify");
        var fromMe = node.GetAttr("fromMe") == "true" || node.GetAttr("from") == _auth.Me?.Id;

        if (!long.TryParse(node.GetAttr("t"), out var timestamp))
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Extract text content — walk the message body
        string? text = null;
        var body = node.FindChild("body");
        if (body?.Text != null)
            text = body.Text;

        if (string.IsNullOrEmpty(text))
            return; // Skip non-text messages for now

        var msg = new IncomingMessage
        {
            Id = id,
            From = participant ?? from,
            RemoteJid = from,
            Participant = participant,
            Text = text,
            FromMe = fromMe,
            Timestamp = timestamp,
        };

        MessageReceived?.Invoke(this, msg);
    }

    private async Task HandleNotificationAsync(BinaryNode notification, CancellationToken ct)
    {
        // ACK notifications
        var id = notification.GetAttr("id");
        var to = notification.GetAttr("from") ?? "s.whatsapp.net";
        var ack = new BinaryNode("ack", new()
        {
            ["id"] = id ?? "",
            ["to"] = to,
            ["type"] = "notification",
            ["class"] = notification.GetAttr("type") ?? "",
        });
        await SendNodeAsync(ack, ct);
    }

    // ─── Send message ───────────────────────────────────────────────────────

    /// <summary>Sends a text message to a JID.</summary>
    public async Task SendTextMessageAsync(string jid, string text, CancellationToken ct)
    {
        var msgId = GenerateMessageId();
        var msgContent = Encoding.UTF8.GetBytes(text);

        var msgNode = new BinaryNode("message", new()
        {
            ["id"] = msgId,
            ["type"] = "text",
            ["to"] = jid,
        }, new List<BinaryNode>
        {
            new("body", content: text),
        });

        await SendNodeAsync(msgNode, ct);
        _logger.LogInformation("Sent message to {Jid}: {Text}", jid, text.Length > 50 ? text[..50] + "…" : text);
    }

    // ─── Low-level send/receive ─────────────────────────────────────────────

    private async Task SendNodeAsync(BinaryNode node, CancellationToken ct)
    {
        var encoded = BinaryNodeEncoder.Encode(node);
        // Prepend flags byte (0x00 = uncompressed) — server strips this on receive
        // just as we strip it from server frames in StripFlagsAndDecompress().
        var frameData = new byte[1 + encoded.Length];
        frameData[0] = 0;
        encoded.CopyTo(frameData, 1);
        var encrypted = EncryptFrame(frameData);
        await _socket.SendFrameAsync(encrypted, ct);
    }

    private bool _firstHandshakeFrame = true;

    private async Task SendHandshakeMessageAsync(HandshakeMessage msg, CancellationToken ct)
    {
        var msgBytes = msg.ToByteArray();

        if (_firstHandshakeFrame)
        {
            // Baileys wire format for the FIRST frame:
            //   [WA_PROLOGUE: 4 bytes] [length: 3 bytes BE] [proto payload: N bytes]
            // The WA prologue is OUTSIDE the 3-byte length framing (unlike subsequent frames).
            var raw = new byte[WA_PROLOGUE.Length + 3 + msgBytes.Length];
            WA_PROLOGUE.CopyTo(raw, 0);
            raw[4] = (byte)(msgBytes.Length >> 16);
            raw[5] = (byte)(msgBytes.Length >> 8);
            raw[6] = (byte)(msgBytes.Length);
            msgBytes.CopyTo(raw, 7);
            await _socket.SendRawAsync(raw, ct);
            _firstHandshakeFrame = false;
        }
        else
        {
            // Subsequent frames: standard [3-byte len][payload]
            await _socket.SendFrameAsync(msgBytes, ct);
        }
    }

    private byte[] EncryptFrame(byte[] data)
    {
        if (!_handshakeDone || _sendKey == null)
            throw new InvalidOperationException("Handshake not complete.");
        return AesGcmHelper.EncryptWithCounter(_sendKey, _sendCounter++, data);
    }

    private byte[] DecryptFrame(byte[] data)
    {
        if (!_handshakeDone || _recvKey == null)
            throw new InvalidOperationException("Handshake not complete.");
        return AesGcmHelper.DecryptWithCounter(_recvKey, _recvCounter++, data);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static byte[] StripFlagsAndDecompress(byte[] decrypted)
    {
        if (decrypted.Length == 0) return decrypted;
        var flags = decrypted[0];
        var data = decrypted[1..];

        if ((flags & 2) != 0)
        {
            // zlib raw deflate compressed (DeflateStream = raw deflate, no zlib header)
            using var input = new MemoryStream(data);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }

        return data;
    }

    // Baileys default version: [2, 3000, 1033846690]
    // buildHash = MD5("2.3000.1033846690")
    private const string WA_VERSION = "2.3000.1033846690";

    private byte[] BuildClientPayload()
    {
        var userAgent = new UserAgent
        {
            Platform = 14, // WEB
            AppVersion = new AppVersion { Primary = 2, Secondary = 3000, Tertiary = 1033846690 },
            Mcc = "000",
            Mnc = "000",
            OsVersion = "0.1",
            Device = "Desktop",   // Baileys getUserAgent always uses "Desktop"
            OsBuildNumber = "0.1",
            LocaleLanguageIso6391 = "en",
            LocaleCountryIso31661Alpha2 = "US",
        };

        if (_auth.IsFresh)
        {
            // Fresh registration: include device pairing data so server knows our keys
            var buildHash = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(WA_VERSION));

            // Registration ID as 4-byte big-endian
            var eRegid = new byte[4];
            eRegid[0] = (byte)(_auth.RegistrationId >> 24);
            eRegid[1] = (byte)(_auth.RegistrationId >> 16);
            eRegid[2] = (byte)(_auth.RegistrationId >> 8);
            eRegid[3] = (byte)(_auth.RegistrationId);

            // Signed pre-key ID as 3-byte big-endian
            var eSkeyId = new byte[3];
            eSkeyId[0] = (byte)(_auth.SignedPreKeyId >> 16);
            eSkeyId[1] = (byte)(_auth.SignedPreKeyId >> 8);
            eSkeyId[2] = (byte)(_auth.SignedPreKeyId);

            var deviceProps = new DevicePropsMessage
            {
                // Os="Ubuntu", Version={10,15,7}, HistorySyncConfig — all set by default
                PlatformType = 1, // CHROME
            }.ToByteArray();

            return new ClientPayload
            {
                Passive = false,
                Pull = false,
                ConnectType = 1,   // WIFI_UNKNOWN
                ConnectReason = 1, // USER_ACTIVATED
                UserAgent = userAgent,
                WebInfo = new WebInfo { WebSubPlatform = 0 },
                DevicePairingData = new DevicePairingRegistrationData
                {
                    ERegid   = eRegid,
                    EKeytype = [5], // KEY_BUNDLE_TYPE
                    EIdent   = _auth.SignedIdentityKeyPublic,
                    ESkeyId  = eSkeyId,
                    ESkeyVal = _auth.SignedPreKeyPublic,
                    ESkeySig = _auth.SignedPreKeySignature,
                    BuildHash   = buildHash,
                    DeviceProps = deviceProps,
                },
            }.ToByteArray();
        }
        else
        {
            // Session restore (login)
            ulong.TryParse(_auth.Me?.Id.Split('@')[0] ?? "0", out var userId);
            return new ClientPayload
            {
                Username = userId,
                Passive = true,
                Pull = true,
                ConnectType = 1,
                ConnectReason = 1,
                UserAgent = userAgent,
                WebInfo = new WebInfo { WebSubPlatform = 0 },
            }.ToByteArray();
        }
    }

    private static string GenerateMessageId()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        return BitConverter.ToString(bytes).Replace("-", "").ToUpper();
    }

    public async ValueTask DisposeAsync()
    {
        // Nothing to dispose here — socket is owned by the caller
        await ValueTask.CompletedTask;
    }
}
