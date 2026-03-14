using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dawa.Auth;
using Dawa.Binary;
using Dawa.Crypto;
using Dawa.Messages;
using Dawa.Proto;
using Dawa.Signal;
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

    // Signal Protocol session store
    private readonly SignalKeyStore _signalStore;

    // Pending IQ tracking for request/response correlation
    private readonly Dictionary<string, TaskCompletionSource<BinaryNode>> _pendingIqs = new();

    // Only fire Authenticated + upload pre-keys once per connection (not on every periodic success token)
    private bool _sessionAuthenticated;
    private CancellationTokenSource? _keepAliveCts;

    // When WhatsApp sends edge_routing, we store the bytes for use on next reconnect.
    // We do NOT break the receive loop — only a stream:error or socket close causes a reconnect.
    public byte[]? PendingRoutingInfo { get; private set; }

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
        _signalStore = new SignalKeyStore(options.SessionDirectory);
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

                var nodeStr = node.ToString();
                _logger.LogInformation("Received node: {Node}", nodeStr);
                // Write to file log (bypasses IIS stdout 8KB cap)
                try
                {
                    var logDir = Path.Combine(_options.SessionDirectory, "..", "nodelog");
                    Directory.CreateDirectory(logDir);
                    var logFile = Path.Combine(logDir, "nodes.log");
                    File.AppendAllText(logFile, $"[{DateTime.UtcNow:HH:mm:ss}] {nodeStr}\n");
                }
                catch { /* non-fatal */ }
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
                if (!_sessionAuthenticated)
                {
                    _sessionAuthenticated = true;
                    _logger.LogInformation("Session authenticated successfully.");
                    Authenticated?.Invoke(this, _auth);
                    // Upload pre-keys so other devices can start Signal sessions with us,
                    // then announce presence so WhatsApp starts delivering messages to this device.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await UploadPreKeysAsync(ct);
                            await SendActiveAsync(ct);       // CRITICAL: switch session from passive → active
                            await SendPresenceAsync(ct);
                            await SendAppStateSyncAsync(ct);
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Post-auth setup failed (non-fatal)"); }
                    });
                    // Start proactive keepalive pings (every 25s, like Baileys)
                    _keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _ = Task.Run(() => KeepAliveLoopAsync(_keepAliveCts.Token));
                }
                else
                {
                    _logger.LogDebug("Received periodic success token (session already authenticated, skipping re-init).");
                }
                break;
            case "failure":
                _logger.LogWarning("Authentication failure: {Reason}", node.GetAttr("reason"));
                break;
            case "stream:error":
                _logger.LogError("Stream error: {Code}", node.GetAttr("code"));
                break;
            case "ib":
                await HandleIbAsync(node, ct);
                break;
            default:
                _logger.LogDebug("Unhandled node tag: {Tag}", node.Tag);
                break;
        }
    }

    /// <summary>
    /// Handles informational broadcast (ib) nodes from WhatsApp.
    /// The most important one is dirty type="account_sync" — WhatsApp withholds
    /// message delivery until the client sends a "clean" IQ to acknowledge the sync.
    /// </summary>
    private async Task HandleIbAsync(BinaryNode ib, CancellationToken ct)
    {
        foreach (var child in ib.Children)
        {
            if (child.Tag == "dirty")
            {
                var dirtyType = child.GetAttr("type") ?? "";
                var timestamp = child.GetAttr("timestamp") ?? "";

                _logger.LogInformation("Received dirty notification: type={Type}, timestamp={Ts} — sending clean IQ", dirtyType, timestamp);

                var cleanIq = new BinaryNode("iq", new Dictionary<string, string>
                {
                    ["to"]    = "@s.whatsapp.net",
                    ["type"]  = "set",
                    ["xmlns"] = "urn:xmpp:whatsapp:dirty",
                    ["id"]    = GenerateMessageId(),
                })
                {
                    Content = new List<BinaryNode>
                    {
                        new("clean", new Dictionary<string, string>
                        {
                            ["type"]      = dirtyType,
                            ["timestamp"] = timestamp,
                        }),
                    },
                };

                try
                {
                    await SendNodeAsync(cleanIq, ct);
                    _logger.LogInformation("Sent clean IQ for dirty type={Type} — message delivery should resume.", dirtyType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send clean IQ for dirty type={Type}", dirtyType);
                }
            }
            else if (child.Tag == "edge_routing")
            {
                // WhatsApp suggests a preferred edge server. Store routing bytes for use on next
                // reconnect (triggered by stream:error or socket close). Do NOT force an immediate
                // disconnect — that causes an infinite reconnect loop.
                var routingInfo = child.FindChild("routing_info");
                if (routingInfo?.Content is byte[] routingBytes && routingBytes.Length > 0)
                {
                    PendingRoutingInfo = routingBytes;
                    _logger.LogInformation("Received edge_routing ({Len} bytes) — stored for next reconnect.", routingBytes.Length);
                }
            }
            else
            {
                _logger.LogDebug("Unhandled ib child: {Tag}", child.Tag);
            }
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
        // Resolve any pending IQ awaiter first
        var iqId = iq.GetAttr("id") ?? "";
        var iqType = iq.GetAttr("type") ?? "";
        _logger.LogDebug("HandleIQ: id={Id} type={Type} pendingKeys=[{Keys}]",
            iqId, iqType, string.Join(",", _pendingIqs.Keys));
        if ((iqType == "result" || iqType == "error") && _pendingIqs.TryGetValue(iqId, out var tcs))
        {
            _pendingIqs.Remove(iqId);
            _logger.LogInformation("IQ resolved: id={Id} type={Type}", iqId, iqType);
            tcs.SetResult(iq);
            return;
        }

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
                    ["to"]   = iq.GetAttr("from") ?? "@s.whatsapp.net",
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
                    ["to"] = iq.GetAttr("from") ?? "@s.whatsapp.net",
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

        // Store account in auth state for device-identity node when sending pkmsg
        _auth.Account = account.ToByteArray();

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
            ["to"]   = "@s.whatsapp.net",
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
        var from        = node.GetAttr("from") ?? "";
        var id          = node.GetAttr("id") ?? "";
        var participant = node.GetAttr("participant");
        var pushName    = node.GetAttr("notify");

        var myJidBase = _auth.Me?.Id?.Split(':')[0]; // e.g. "31633984381"
        var fromMe    = from == myJidBase + "@s.whatsapp.net"
                     || (myJidBase != null && from.StartsWith(myJidBase));

        if (!long.TryParse(node.GetAttr("t"), out var timestamp))
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ── Encrypted path: <participants><to jid="..."><enc .../></to></participants> ──
        var participantsNode = node.FindChild("participants");
        if (participantsNode != null)
        {
            foreach (var toNode in participantsNode.GetChildren("to"))
            {
                var toJid = toNode.GetAttr("jid") ?? "";
                if (myJidBase != null && !toJid.StartsWith(myJidBase)) continue;

                var encNode = toNode.FindChild("enc");
                if (encNode?.Data == null) continue;

                var encType = encNode.GetAttr("type") ?? "msg";
                try
                {
                    var senderJid = participant ?? from;
                    var plaintext = _signalStore.DecryptMessage(senderJid, encType, encNode.Data, _auth);
                    var waMsg     = WAMessage.ParseFrom(plaintext);
                    var text      = waMsg.GetText();
                    if (string.IsNullOrEmpty(text)) continue;

                    MessageReceived?.Invoke(this, new IncomingMessage
                    {
                        Id          = id,
                        From        = senderJid,
                        RemoteJid   = from,
                        Participant = participant,
                        Text        = text,
                        FromMe      = fromMe,
                        Timestamp   = timestamp,
                        PushName    = pushName,
                    });

                    // ACK
                    _ = SendAckAsync(id, from, timestamp);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to decrypt message from {Jid}", from);
                    _ = SendRetryReceiptAsync(id, from, timestamp);
                }
            }
            return;
        }

        // ── Direct <enc> (no <participants> wrapper) ──────────────────────────
        var directEnc = node.FindChild("enc");
        if (directEnc?.Data != null)
        {
            var encType = directEnc.GetAttr("type") ?? "msg";
            try
            {
                var senderJid = participant ?? from;
                byte[] plaintext;
                try
                {
                    plaintext = _signalStore.DecryptMessage(senderJid, encType, directEnc.Data, _auth);
                }
                catch (Exception decEx)
                {
                    _logger.LogWarning(decEx, "Failed to decrypt direct enc message from {Jid}", from);
                    _ = SendRetryReceiptAsync(id, from, timestamp);
                    return;
                }

                _logger.LogInformation("Decrypted {Bytes} bytes from {Jid} (category={Category})",
                    plaintext.Length, from, node.GetAttr("category") ?? "?");

                WAMessage waMsg;
                try
                {
                    waMsg = WAMessage.ParseFrom(plaintext);
                }
                catch (Exception parseEx)
                {
                    // Peer/device messages may not be standard WAMessage format
                    _logger.LogDebug(parseEx, "Could not parse decrypted message as WAMessage from {Jid} ({Len} bytes, first={First})",
                        from, plaintext.Length, Convert.ToHexString(plaintext[..Math.Min(16, plaintext.Length)]));
                    _ = SendAckAsync(id, from, timestamp);
                    return;
                }

                var text = waMsg.GetText();
                _logger.LogInformation("Parsed WAMessage from {Jid}: text={Text}, hasDeviceSent={DevSent}, hasSKDM={SKDM}",
                    from, text ?? "(null)", waMsg.DeviceSentMessage != null, waMsg.SenderKeyDist != null);

                if (!string.IsNullOrEmpty(text))
                {
                    MessageReceived?.Invoke(this, new IncomingMessage
                    {
                        Id          = id,
                        From        = participant ?? from,
                        RemoteJid   = from,
                        Participant = participant,
                        Text        = text,
                        FromMe      = fromMe,
                        Timestamp   = timestamp,
                        PushName    = pushName,
                    });
                }
                _ = SendAckAsync(id, from, timestamp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error handling direct enc message from {Jid}", from);
            }
            return;
        }

        // ── Fallback: plain text body (legacy) ────────────────────────────────
        var body      = node.FindChild("body");
        var plainText = body?.Text;
        if (string.IsNullOrEmpty(plainText)) return;

        MessageReceived?.Invoke(this, new IncomingMessage
        {
            Id          = id,
            From        = participant ?? from,
            RemoteJid   = from,
            Text        = plainText,
            FromMe      = fromMe,
            Timestamp   = timestamp,
            PushName    = pushName,
        });
    }

    /// <summary>
    /// Sends the passive→active switch IQ (xmlns="passive", tag="active").
    /// This is the critical call Baileys makes after auth to tell WhatsApp to start
    /// delivering queued messages to this companion device. Without it, WhatsApp keeps
    /// the session in passive mode and never pushes any message/dirty/notification nodes.
    /// </summary>
    private async Task SendActiveAsync(CancellationToken ct)
    {
        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["to"]    = "@s.whatsapp.net",
            ["type"]  = "set",
            ["xmlns"] = "passive",
            ["id"]    = GenerateMessageId(),
        })
        {
            Content = new List<BinaryNode> { new BinaryNode("active") },
        };
        try
        {
            await SendIQAsync(iq, ct, timeoutMs: 10000);
            _logger.LogInformation("Passive→active switch sent — WhatsApp should now deliver messages.");
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("passive/active IQ timed out — WhatsApp may not deliver messages.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "passive/active IQ failed.");
        }
    }

    /// <summary>
    /// Sends a presence update to tell WhatsApp this device is online and ready to receive messages.
    /// Without this, WhatsApp may not deliver messages to linked devices.
    /// </summary>
    private async Task SendPresenceAsync(CancellationToken ct)
    {
        try
        {
            var presence = new BinaryNode("presence", new Dictionary<string, string>
            {
                ["type"] = "available",
            });
            await SendNodeAsync(presence, ct);
            _logger.LogInformation("Sent presence: available — WhatsApp should now deliver messages to this device.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send presence update");
        }
    }

    /// <summary>
    /// Sends a keepalive ping IQ every 25 seconds, like Baileys does after login.
    /// The server expects this to consider the device fully online and deliver messages.
    /// </summary>
    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Keepalive loop started (25s interval).");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                if (ct.IsCancellationRequested) break;

                try
                {
                    var ping = new BinaryNode("iq", new Dictionary<string, string>
                    {
                        ["id"]    = GenerateMessageId(),
                        ["to"]    = "@s.whatsapp.net",
                        ["type"]  = "get",
                        ["xmlns"] = "w:p",
                    })
                    {
                        Content = new List<BinaryNode> { new("ping") },
                    };
                    await SendNodeAsync(ping, ct);
                    _logger.LogDebug("Sent keepalive ping.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Keepalive ping failed — connection may be lost.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogInformation("Keepalive loop ended.");
    }

    private async Task SendAckAsync(string msgId, string to, long timestamp)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ack = new BinaryNode("ack", new Dictionary<string, string>
            {
                ["id"]   = msgId,
                ["to"]   = to,
                ["type"] = "message",
                ["t"]    = timestamp.ToString(),
            });
            await SendNodeAsync(ack, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send ACK for message {Id}", msgId);
        }
    }

    /// <summary>
    /// Sends a retry receipt to WhatsApp, asking the sender to re-encrypt using
    /// a fresh pre-key bundle. Called when we receive a message we cannot decrypt
    /// (e.g. our session state was lost after a restart).
    /// </summary>
    private async Task SendRetryReceiptAsync(string msgId, string to, long timestamp)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var retry = new BinaryNode("receipt", new Dictionary<string, string>
            {
                ["id"]   = msgId,
                ["type"] = "retry",
                ["to"]   = to,
                ["t"]    = timestamp.ToString(),
            })
            {
                Content = new List<BinaryNode>
                {
                    new("retry", new Dictionary<string, string>
                    {
                        ["count"] = "1",
                        ["id"]    = msgId,
                        ["t"]     = timestamp.ToString(),
                        ["v"]     = "1",
                    }),
                    new("registration", null, new byte[] { (byte)(_auth.RegistrationId >> 24), (byte)(_auth.RegistrationId >> 16), (byte)(_auth.RegistrationId >> 8), (byte)_auth.RegistrationId }),
                },
            };
            await SendNodeAsync(retry, cts.Token);
            _logger.LogInformation("Sent retry receipt for {MsgId} to {To} — session state lost, requesting re-key.", msgId, to);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send retry receipt for {MsgId}", msgId);
        }
    }

    /// <summary>
    /// Sends the initial app state sync request (xmlns="w:app:state:sync").
    /// Baileys calls resyncAppState immediately after auth — WhatsApp may withhold
    /// message delivery until the companion device has "checked in" with a sync request.
    /// We request all 5 standard collections at version=0 (initial sync).
    /// We don't need to process the response content, just sending the request is enough
    /// to signal to WhatsApp that this device is ready to receive messages.
    /// </summary>
    private async Task SendAppStateSyncAsync(CancellationToken ct)
    {
        var collections = new[] { "critical_block", "critical_unblock_to_primary", "regular_high", "regular_low", "regular" };
        var collectionNodes = collections.Select(name => new BinaryNode("collection", new Dictionary<string, string>
        {
            ["name"]            = name,
            ["version"]         = "0",
            ["return_snapshot"] = "true",
        })).ToList<BinaryNode>();

        var syncNode = new BinaryNode("sync") { Content = collectionNodes };
        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["to"]    = "@s.whatsapp.net",
            ["xmlns"] = "w:app:state:sync",
            ["type"]  = "set",
            ["id"]    = GenerateMessageId(),
        }) { Content = new List<BinaryNode> { syncNode } };

        try
        {
            var result = await SendIQAsync(iq, ct, timeoutMs: 60000);
            _logger.LogInformation("App state sync request acknowledged (WhatsApp should now route messages to this device).");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "App state sync IQ timed out — continuing anyway (messages may still arrive)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "App state sync request failed (non-fatal)");
        }
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

    /// <summary>Sends an encrypted text message to a JID using Signal Protocol.</summary>
    public async Task SendTextMessageAsync(string jid, string text, CancellationToken ct)
    {
        // 1. Normalize JID
        var normalizedJid = jid.Contains('@') ? jid : $"{jid.TrimStart('+')}@s.whatsapp.net";
        var phoneNumber   = normalizedJid.Split('@')[0].Split(':')[0];

        // 2. Get recipient device list via USync
        List<string> recipientDeviceJids;
        try { recipientDeviceJids = await GetDeviceListAsync(phoneNumber, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get device list for {Phone}, using fallback", phoneNumber);
            recipientDeviceJids = [$"{phoneNumber}:0@s.whatsapp.net"];
        }

        // 3. Get sender's own devices for multi-device sync
        var senderDeviceJids = new List<string>();
        var myJid = _auth.Me?.Id; // e.g. "31633984381:44@s.whatsapp.net"
        if (myJid != null)
        {
            var myPhone = myJid.Split('@')[0].Split(':')[0];
            try
            {
                var myDevices = await GetDeviceListAsync(myPhone, ct);
                foreach (var d in myDevices)
                {
                    // Skip our own companion device (can't encrypt to ourselves)
                    if (d == myJid) continue;
                    senderDeviceJids.Add(d);
                }
                _logger.LogInformation("Sender has {Count} other devices for sync: {Jids}",
                    senderDeviceJids.Count, string.Join(", ", senderDeviceJids));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get sender device list, skipping multi-device sync");
            }
        }

        // Combine all device JIDs for pre-key bundle fetching
        var allDeviceJids = new List<string>(recipientDeviceJids);
        allDeviceJids.AddRange(senderDeviceJids);

        // 4. For each device without a session, fetch pre-key bundle
        var needBundles = allDeviceJids.Where(d => !_signalStore.HasSession(d)).ToList();
        if (needBundles.Count > 0)
        {
            try
            {
                _logger.LogInformation("Fetching pre-key bundles for {Count} devices: {Jids}",
                    needBundles.Count, string.Join(", ", needBundles));
                var bundles = await FetchPreKeyBundlesAsync(needBundles, ct);
                _logger.LogInformation("Got {Count} bundles back", bundles.Count);
                foreach (var (deviceJid, bundle) in bundles)
                {
                    try
                    {
                        _signalStore.InitOutgoingSession(deviceJid, bundle, _auth);
                        _logger.LogInformation("Initialized outgoing session for {Jid}", deviceJid);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "Failed to init outgoing session for {Jid}", deviceJid);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch pre-key bundles");
            }
        }

        // 5. Proto-encode the message with random PKCS7-style padding (Baileys: writeRandomPadMax16)
        var msgRaw = new WAMessage { Conversation = text }.ToByteArray();

        // 6. Build padded proto for recipient devices (direct message)
        var recipientProto = PadMessage(msgRaw);

        // 7. Build padded proto for sender's own devices (wrapped in DeviceSentMessage)
        byte[]? senderProto = null;
        if (senderDeviceJids.Count > 0)
        {
            var deviceSentMsg = new WAMessage
            {
                DeviceSentMessage = new DeviceSentMessage
                {
                    DestinationJid = normalizedJid,
                    Message = new WAMessage { Conversation = text },
                }
            };
            senderProto = PadMessage(deviceSentMsg.ToByteArray());
        }

        // 8. Encrypt for each device
        var msgId    = GenerateMessageId();
        var hasPkMsg = false;
        var toNodes  = new List<BinaryNode>();

        // Encrypt for recipient devices
        foreach (var deviceJid in recipientDeviceJids)
        {
            var node = EncryptForDevice(deviceJid, recipientProto, ref hasPkMsg);
            if (node != null) toNodes.Add(node);
        }

        // Encrypt for sender's own devices (multi-device sync)
        foreach (var deviceJid in senderDeviceJids)
        {
            var node = EncryptForDevice(deviceJid, senderProto!, ref hasPkMsg);
            if (node != null) toNodes.Add(node);
        }

        if (toNodes.Count == 0)
        {
            _logger.LogError("No devices encrypted successfully for {Jid}", normalizedJid);
            return;
        }

        // 9. Build and send message node
        var participantsNode = new BinaryNode("participants")
        {
            Content = toNodes,
        };
        var contentNodes = new List<BinaryNode> { participantsNode };

        // Include device-identity when sending pkmsg (first message to a device)
        if (hasPkMsg && _auth.Account != null)
        {
            var accountProto = ADVSignedDeviceIdentity.ParseFrom(_auth.Account);
            var deviceIdentityBytes = accountProto.ToByteArray();
            contentNodes.Add(new BinaryNode("device-identity") { Content = deviceIdentityBytes });
            _logger.LogInformation("Including device-identity in message (pkmsg detected, {Len} bytes)", deviceIdentityBytes.Length);
        }
        else if (hasPkMsg)
        {
            _logger.LogWarning("pkmsg detected but Account is NULL — device-identity NOT included. Recipient may reject. Re-pair to fix.");
        }

        var msgAttrs = new Dictionary<string, string>
        {
            ["id"]    = msgId,
            ["type"]  = "text",
            ["to"]    = normalizedJid,
        };

        var msgNode = new BinaryNode("message", msgAttrs) { Content = contentNodes };

        await SendNodeAsync(msgNode, ct);
        _logger.LogInformation("Sent encrypted message to {Jid} via {RecipientCount}+{SenderCount} devices",
            normalizedJid, recipientDeviceJids.Count, senderDeviceJids.Count);
    }

    /// <summary>Pads a proto-encoded message with random PKCS7-style padding (Baileys: writeRandomPadMax16).</summary>
    private static byte[] PadMessage(byte[] msgRaw)
    {
        var padLen = (byte)(System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 16) + 1); // 1-16
        var padded = new byte[msgRaw.Length + padLen];
        msgRaw.CopyTo(padded, 0);
        Array.Fill(padded, padLen, msgRaw.Length, padLen);
        return padded;
    }

    /// <summary>Encrypts a padded proto for a single device, returning a 'to' node or null on failure.</summary>
    private BinaryNode? EncryptForDevice(string deviceJid, byte[] paddedProto, ref bool hasPkMsg)
    {
        if (!_signalStore.HasSession(deviceJid))
        {
            _logger.LogWarning("No session for {Jid} after bundle fetch, skipping", deviceJid);
            return null;
        }
        try
        {
            var (encBytes, isPreKey) = _signalStore.EncryptMessage(deviceJid, paddedProto, _auth);
            if (isPreKey) hasPkMsg = true;
            var encNode = new BinaryNode("enc", new Dictionary<string, string>
            {
                ["v"]    = "2",
                ["type"] = isPreKey ? "pkmsg" : "msg",
            }) { Content = encBytes };

            return new BinaryNode("to", new Dictionary<string, string>
            {
                ["jid"] = deviceJid,
            }) { Content = new List<BinaryNode> { encNode } };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to encrypt for {Jid}: {Error}", deviceJid, ex.Message);
            return null;
        }
    }

    // ─── IQ helper ──────────────────────────────────────────────────────────

    private async Task<BinaryNode> SendIQAsync(BinaryNode iq, CancellationToken ct, int timeoutMs = 15000)
    {
        var id = iq.GetAttr("id") ?? GenerateMessageId();
        if (!iq.Attrs.ContainsKey("id")) iq.Attrs["id"] = id;

        var tcs = new TaskCompletionSource<BinaryNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingIqs[id] = tcs;

        _logger.LogInformation("Sending IQ id={Id} xmlns={Xmlns} type={Type}, pending count={Count}",
            id, iq.GetAttr("xmlns") ?? "?", iq.GetAttr("type") ?? "?", _pendingIqs.Count);

        await SendNodeAsync(iq, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pendingIqs.Remove(id);
            throw new TimeoutException($"IQ {id} timed out after {timeoutMs}ms");
        }
    }

    // ─── Fetch pre-key bundles ───────────────────────────────────────────────

    /// <summary>
    /// Sends an encrypt IQ to fetch pre-key bundles for a list of device JIDs.
    /// Returns a dictionary keyed by device JID.
    /// </summary>
    private async Task<Dictionary<string, PreKeyBundle>> FetchPreKeyBundlesAsync(
        IEnumerable<string> deviceJids, CancellationToken ct)
    {
        var userNodes = deviceJids.Select(d => new BinaryNode("user", new Dictionary<string, string>
        {
            ["jid"] = d,
        })).ToList();

        var keyNode = new BinaryNode("key") { Content = userNodes };
        var iqId    = GenerateMessageId();
        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["xmlns"] = "encrypt",
            ["type"]  = "get",
            ["to"]    = "@s.whatsapp.net",
            ["id"]    = iqId,
        }) { Content = new List<BinaryNode> { keyNode } };

        BinaryNode response;
        try { response = await SendIQAsync(iq, ct, timeoutMs: 30000); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchPreKeyBundles IQ failed");
            return new Dictionary<string, PreKeyBundle>();
        }

        var result  = new Dictionary<string, PreKeyBundle>();
        var listNode = response.FindChild("list") ?? response;

        foreach (var userNode in listNode.GetChildren("user"))
        {
            var userJid = userNode.GetAttr("jid") ?? "";
            if (string.IsNullOrEmpty(userJid)) continue;

            try
            {
                var bundle = ParsePreKeyBundle(userNode);
                result[userJid] = bundle;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse pre-key bundle for {Jid}", userJid);
            }
        }

        return result;
    }

    private static PreKeyBundle ParsePreKeyBundle(BinaryNode userNode)
    {
        var regNode  = userNode.FindChild("registration");
        var typeNode = userNode.FindChild("type");
        var idNode   = userNode.FindChild("identity");
        var skeyNode = userNode.FindChild("skey");
        var otpkNode = userNode.FindChild("key");

        var regBytes = regNode?.Data ?? [];
        uint regId = regBytes.Length >= 4
            ? (uint)((regBytes[0] << 24) | (regBytes[1] << 16) | (regBytes[2] << 8) | regBytes[3])
            : 0;

        var identityKey = idNode?.Data ?? [];

        byte[] spkPub = [];
        uint   spkId  = 0;
        byte[] spkSig = [];
        if (skeyNode != null)
        {
            var skeyIdBytes = skeyNode.FindChild("id")?.Data ?? [];
            spkId = skeyIdBytes.Length >= 3
                ? (uint)((skeyIdBytes[0] << 16) | (skeyIdBytes[1] << 8) | skeyIdBytes[2])
                : 0;
            spkPub = skeyNode.FindChild("value")?.Data ?? [];
            spkSig = skeyNode.FindChild("signature")?.Data ?? [];
        }

        byte[]? otpkPub = null;
        uint    otpkId  = 0;
        if (otpkNode != null)
        {
            var otpkIdBytes = otpkNode.FindChild("id")?.Data ?? [];
            otpkId = otpkIdBytes.Length >= 3
                ? (uint)((otpkIdBytes[0] << 16) | (otpkIdBytes[1] << 8) | otpkIdBytes[2])
                : 0;
            otpkPub = otpkNode.FindChild("value")?.Data;
        }

        return new PreKeyBundle
        {
            TheirIdentityPub      = identityKey,
            TheirSignedPreKeyPub  = spkPub,
            TheirSignedPreKeyId   = spkId,
            TheirSignedPreKeySig  = spkSig,
            TheirOneTimePreKeyPub = otpkPub,
            TheirOneTimePreKeyId  = otpkId,
            PeerRegistrationId    = regId,
        };
    }

    // ─── Get device list (USync) ─────────────────────────────────────────────

    /// <summary>
    /// Sends a USync IQ to get all device JIDs for a phone number.
    /// Returns list of device JIDs like "31633984381:0@s.whatsapp.net".
    /// </summary>
    private async Task<List<string>> GetDeviceListAsync(string phoneNumber, CancellationToken ct)
    {
        var jid  = $"{phoneNumber.TrimStart('+')}@s.whatsapp.net";
        var sid  = GenerateMessageId();
        var iqId = GenerateMessageId();

        var devicesNode = new BinaryNode("devices", new Dictionary<string, string> { ["version"] = "2" });
        var queryNode  = new BinaryNode("query") { Content = new List<BinaryNode> { devicesNode } };

        var userNode = new BinaryNode("user", new Dictionary<string, string> { ["jid"] = jid });
        var listNode = new BinaryNode("list") { Content = new List<BinaryNode> { userNode } };

        var usyncNode = new BinaryNode("usync", new Dictionary<string, string>
        {
            ["context"] = "message",
            ["mode"]    = "query",
            ["last"]    = "true",
            ["index"]   = "0",
            ["sid"]     = sid,
        }) { Content = new List<BinaryNode> { queryNode, listNode } };

        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["to"]    = "@s.whatsapp.net",
            ["type"]  = "get",
            ["xmlns"] = "usync",
            ["id"]    = iqId,
        }) { Content = new List<BinaryNode> { usyncNode } };

        BinaryNode response;
        try { response = await SendIQAsync(iq, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetDeviceList IQ failed for {Phone}", phoneNumber);
            return [$"{phoneNumber}:0@s.whatsapp.net"];
        }

        var deviceJids = new List<string>();
        WalkForDevices(response, phoneNumber, deviceJids);

        if (deviceJids.Count == 0)
            deviceJids.Add($"{phoneNumber}:0@s.whatsapp.net");

        return deviceJids;
    }

    private static void WalkForDevices(BinaryNode node, string phoneNumber, List<string> result)
    {
        if (node.Tag == "device")
        {
            // USync response has two formats:
            // 1. <device jid="31633984381:10@s.whatsapp.net" .../>
            // 2. <device id="10" .../>  (just the device ID, no full JID)
            var jid = node.GetAttr("jid");
            if (!string.IsNullOrEmpty(jid))
            {
                result.Add(jid);
                return;
            }
            var id = node.GetAttr("id");
            if (id != null)
            {
                // Device 0 is the primary phone — Baileys encodes as "user@server" (no :0)
                // Other devices use "user:device@server"
                if (id == "0")
                    result.Add($"{phoneNumber}@s.whatsapp.net");
                else
                    result.Add($"{phoneNumber}:{id}@s.whatsapp.net");
                return;
            }
        }
        foreach (var child in node.Children)
            WalkForDevices(child, phoneNumber, result);
    }

    // ─── Low-level send/receive ─────────────────────────────────────────────

    private async Task SendNodeAsync(BinaryNode node, CancellationToken ct)
    {
        // Log outbound node so we can compare sent vs received in nodelog/nodes.log
        try
        {
            var logDir = Path.Combine(_options.SessionDirectory, "..", "nodelog");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, "nodes.log");
            File.AppendAllText(logFile, $"[{DateTime.UtcNow:HH:mm:ss}] SEND: {node}\n");
        }
        catch { /* non-fatal */ }

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
            // WhatsApp uses zlib compression (with 2-byte header), not raw deflate.
            // Baileys uses Node.js inflateSync() which handles the zlib wrapper.
            // .NET ZLibStream handles the zlib header correctly.
            using var input = new MemoryStream(data);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
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
            // Me.Id format: "31633984381:20@s.whatsapp.net" — extract phone and device number
            var rawId = _auth.Me?.Id.Split('@')[0] ?? "0"; // "31633984381:20"
            var parts = rawId.Split(':');
            ulong.TryParse(parts[0], out var userId);      // "31633984381" → 31633984381
            uint.TryParse(parts.Length > 1 ? parts[1] : "0", out var deviceId); // "20" → 20
            return new ClientPayload
            {
                Username = userId,
                Device = deviceId,   // CRITICAL: server needs device number to route the session
                Passive = true,
                Pull = true,
                ConnectType = 1,
                ConnectReason = 1,
                UserAgent = userAgent,
                WebInfo = new WebInfo { WebSubPlatform = 0 },
            }.ToByteArray();
        }
    }

    /// <summary>
    /// Uploads one-time pre-keys and the signed pre-key to WhatsApp server
    /// so other devices can initiate Signal sessions with us.
    /// Called once after successful authentication.
    /// </summary>
    private async Task UploadPreKeysAsync(CancellationToken ct)
    {
        const int BatchSize = 30; // upload 30 one-time pre-keys at a time

        // Generate fresh pre-keys if we've run out — private keys must be available for decryption
        if (_auth.PreKeys.Count == 0)
        {
            _logger.LogWarning("Pre-key pool exhausted — generating 100 new pre-keys.");
            // Start IDs from a high offset to avoid collisions with any still-live server-side keys
            uint startId = 1001;
            for (uint i = 0; i < 100; i++)
            {
                var (priv, pub) = Crypto.Curve25519Helper.GenerateKeyPair();
                _auth.PreKeys.Add(new Auth.PreKey { Id = startId + i, Private = priv, Public = pub });
            }
        }

        var keysToUpload = _auth.PreKeys.Take(BatchSize).ToList();

        if (keysToUpload.Count == 0)
        {
            _logger.LogWarning("No pre-keys available to upload — others cannot initiate Signal sessions.");
            return;
        }

        // Baileys format: raw 32-byte keys (no 0x05 prefix) — the "type=[0x05]" node indicates key type
        static byte[] Be3(uint v) => [(byte)(v >> 16), (byte)(v >> 8), (byte)v];
        static byte[] Be4(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        // Build key list nodes — value = raw 32-byte public key (no prefix)
        var keyNodes = keysToUpload.Select(k =>
            new BinaryNode("key", null, new List<BinaryNode>
            {
                new("id",    null, Be3(k.Id)),
                new("value", null, k.Public),      // raw 32 bytes
            })
        ).ToList();

        // Signed pre-key node
        var skeyNode = new BinaryNode("skey", null, new List<BinaryNode>
        {
            new("id",        null, Be3(_auth.SignedPreKeyId)),
            new("value",     null, _auth.SignedPreKeyPublic),   // raw 32 bytes
            new("signature", null, _auth.SignedPreKeySignature),
        });

        var content = new List<BinaryNode>
        {
            new("registration", null, Be4(_auth.RegistrationId)),
            new("type",         null, new byte[] { 5 }),  // KEY_BUNDLE_TYPE — indicates Curve25519
            new("identity",     null, _auth.SignedIdentityKeyPublic),   // raw 32 bytes
            new("list",         null, keyNodes),
            skeyNode,
        };

        var iqNode = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["id"]    = GenerateMessageId(),
            ["xmlns"] = "encrypt",
            ["type"]  = "set",
            ["to"]    = "@s.whatsapp.net",
        })
        {
            Content = content,
        };

        try
        {
            var result = await SendIQAsync(iqNode, ct);
            _logger.LogInformation("Pre-keys uploaded successfully ({Count} one-time keys).", keysToUpload.Count);

            // NOTE: Do NOT remove uploaded pre-keys from the local pool.
            // Their private keys must remain in _auth.PreKeys so that InitIncomingSession
            // can look them up by ID when a pkmsg arrives. The keys are removed there,
            // after the session is actually established and the private key consumed.
            // Persist updated auth state in case we generated new pre-keys above.
            Authenticated?.Invoke(this, _auth);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload pre-keys.");
        }
    }

    private static string GenerateMessageId()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        return BitConverter.ToString(bytes).Replace("-", "").ToUpper();
    }

    // ─── App state sync: contact fetching ───────────────────────────────────

    /// <summary>
    /// Fetches a 32-byte app state key from the server by keyId.
    /// </summary>
    private async Task<byte[]> FetchAppStateSyncKeyAsync(byte[] keyId, CancellationToken ct)
    {
        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["id"]    = GenerateMessageId(),
            ["to"]    = "@s.whatsapp.net",
            ["type"]  = "get",
            ["xmlns"] = "w:sync:app:state:k",
        })
        {
            Content = new List<BinaryNode>
            {
                new BinaryNode("key", null, new List<BinaryNode>
                {
                    new BinaryNode("id", null, keyId),
                }),
            },
        };

        var result = await SendIQAsync(iq, ct, timeoutMs: 15000);

        // Navigate result → key → key-data
        var keyNode     = result.FindChild("key") ?? result;
        var keyDataNode = keyNode.FindChild("key-data");
        if (keyDataNode?.Data is { Length: 32 } appKey)
            return appKey;

        // Fallback: walk children for a 32-byte data blob
        foreach (var child in result.Children)
        {
            if (child.Data is { Length: 32 } d) return d;
            foreach (var grandchild in child.Children)
                if (grandchild.Data is { Length: 32 } d2) return d2;
        }

        throw new InvalidOperationException($"App state sync key response did not contain a 32-byte key (tag={result.Tag})");
    }

    /// <summary>
    /// Fetches contacts from WhatsApp via app state sync of the "contact" collection.
    /// Returns (JID, Name) pairs for all contacts found in the snapshot.
    /// </summary>
    public async Task<List<(string Jid, string Name)>> FetchContactsAsync(CancellationToken ct)
    {
        // 1. Send app state sync IQ for "contact" collection
        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["id"]    = GenerateMessageId(),
            ["to"]    = "@s.whatsapp.net",
            ["type"]  = "set",
            ["xmlns"] = "w:app:state:sync",
        })
        {
            Content = new List<BinaryNode>
            {
                new BinaryNode("sync", null, new List<BinaryNode>
                {
                    new BinaryNode("collection", new Dictionary<string, string>
                    {
                        ["name"]            = "contact",
                        ["version"]         = "0",
                        ["return_snapshot"] = "true",
                    }),
                }),
            },
        };

        var result = await SendIQAsync(iq, ct, timeoutMs: 30000);

        // 2. Navigate: result → sync → collection → snapshot
        var syncNode       = result.FindChild("sync") ?? result;
        var collectionNode = syncNode.FindChild("collection") ?? syncNode;
        var snapshotNode   = collectionNode.FindChild("snapshot");

        if (snapshotNode?.Data == null || snapshotNode.Data.Length == 0)
        {
            _logger.LogWarning("FetchContactsAsync: no snapshot data in response");
            return [];
        }

        // 3. Parse SyncdSnapshot from raw protobuf bytes (synchronous — ProtoReader is a ref struct)
        var snapshot = Proto.SyncdSnapshot.ParseFrom(snapshotNode.Data);
        _logger.LogInformation("FetchContactsAsync: snapshot has {Count} records", snapshot.Records.Count);

        // 4. Determine keyId
        var keyId = snapshot.KeyId.Length > 0
            ? snapshot.KeyId
            : snapshot.Records.Count > 0 ? snapshot.Records[0].KeyId : null;

        if (keyId == null || keyId.Length == 0)
        {
            _logger.LogWarning("FetchContactsAsync: no keyId found in snapshot");
            return [];
        }

        // 5. Fetch the app state key (async, after we're done with ref struct parsing)
        var appKey = await FetchAppStateSyncKeyAsync(keyId, ct);

        // 6. Derive enc key using HKDF-SHA256
        var derived = System.Security.Cryptography.HKDF.DeriveKey(
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            appKey,
            outputLength: 64,
            salt: new byte[32],
            info: "WhisperAppStateKeys"u8.ToArray());
        var encKey = derived[..32];

        // 7. Decrypt each record and extract contact info (synchronous decryption)
        var contacts = new List<(string Jid, string Name)>();

        foreach (var record in snapshot.Records)
        {
            if (record.ValueBlob.Length < 17) continue;

            var iv         = record.ValueBlob[..16];
            var ciphertext = record.ValueBlob[16..];

            // AES-CBC requires ciphertext to be a multiple of 16 bytes
            if (ciphertext.Length % 16 != 0) continue;

            byte[] decrypted;
            try
            {
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key     = encKey;
                aes.IV      = iv;
                aes.Mode    = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.None;
                decrypted   = aes.DecryptCbc(ciphertext, iv);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FetchContactsAsync: failed to decrypt record, skipping");
                continue;
            }

            // Strip trailing zero-padding
            var end = decrypted.Length;
            while (end > 0 && decrypted[end - 1] == 0) end--;
            if (end < decrypted.Length) decrypted = decrypted[..end];
            if (decrypted.Length == 0) continue;

            Proto.SyncActionData actionData;
            try { actionData = Proto.SyncActionData.ParseFrom(decrypted); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FetchContactsAsync: failed to parse SyncActionData, skipping");
                continue;
            }

            if (actionData.Index.Length < 2 || actionData.Index[0] != "contact") continue;
            if (actionData.Value?.ContactAction == null) continue;

            var jid           = actionData.Index[1];
            var contactAction = actionData.Value.ContactAction;
            var name          = !string.IsNullOrEmpty(contactAction.FullName)
                ? contactAction.FullName
                : contactAction.FirstName;

            if (string.IsNullOrEmpty(name)) continue;

            contacts.Add((jid, name));
        }

        _logger.LogInformation("FetchContactsAsync: found {Count} contacts", contacts.Count);
        return contacts;
    }

    public async ValueTask DisposeAsync()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        await ValueTask.CompletedTask;
    }
}
