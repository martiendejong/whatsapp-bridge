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

    // Track the last on-demand history request so we can auto-resend when the phone sends a retry receipt
    private (string ChatJid, string? OldestMsgId, bool OldestMsgFromMe, long OldestMsgTimestampMs, int Count)? _lastPdoRequest;
    private readonly HashSet<string> _sentPdoMsgIds = new();

    // Index into auth.PreKeys for retry receipts — incremented per receipt so each message
    // gets a unique one-time pre-key. Without this all retry receipts would advertise the
    // same pre-key; only the first re-encrypted message would decrypt (the key gets consumed).
    private int _retryPreKeyIndex = 0;

    public event EventHandler<string>? QRCodeGenerated;
    public event EventHandler<AuthState>? Authenticated;
    public event EventHandler<IncomingMessage>? MessageReceived;
    public event EventHandler<HistorySyncBatch>? HistorySyncReceived;

    public NoiseProcessor(FrameSocket socket, AuthState auth, WhatsAppClientOptions options, ILogger logger)
    {
        _socket = socket;
        _auth = auth;
        _options = options;
        _logger = logger;

        (_ephemeralPriv, _ephemeralPub) = Curve25519Helper.GenerateKeyPair();
        _signalStore = new SignalKeyStore(options.SessionDirectory);

        // Load persisted LID/push-name caches from previous sessions
        LoadCacheFromDisk();
    }

    private string CacheFilePath => Path.Combine(_options.SessionDirectory, "contact-cache.json");

    private void LoadCacheFromDisk()
    {
        try
        {
            if (!File.Exists(CacheFilePath)) return;
            var json = File.ReadAllText(CacheFilePath);
            var data = System.Text.Json.JsonSerializer.Deserialize<ContactCacheFile>(json);
            if (data == null) return;
            foreach (var kv in data.LidToPhone) _lidToPhone[kv.Key] = kv.Value;
            foreach (var kv in data.PushNames)  _pushNames[kv.Key]  = kv.Value;
            _logger.LogInformation("Loaded contact cache: {Lids} LID mappings, {Names} push names",
                _lidToPhone.Count, _pushNames.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load contact cache"); }
    }

    private void SaveCacheToDisk()
    {
        try
        {
            var data = new ContactCacheFile
            {
                LidToPhone = new Dictionary<string, string>(_lidToPhone),
                PushNames  = new Dictionary<string, string>(_pushNames),
            };
            var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(CacheFilePath, json);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to save contact cache"); }
    }

    private sealed class ContactCacheFile
    {
        public Dictionary<string, string> LidToPhone { get; set; } = new();
        public Dictionary<string, string> PushNames  { get; set; } = new();
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
        SaveCacheToDisk();
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
            case "presence":
                HandlePresenceNode(node);
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
            case "receipt":
                await HandleReceiptAsync(node, ct);
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
            else if (child.Tag == "offline_preview")
            {
                // WA sends offline_preview to tell us there are queued offline messages.
                // Baileys responds with <ib><offline_batch count="100"/></ib> to trigger delivery.
                // Without this, WA never delivers the offline messages.
                var msgCount = child.GetAttr("message") ?? "0";
                _logger.LogInformation("offline_preview: {Count} queued messages — requesting offline_batch", msgCount);
                var offlineBatch = new BinaryNode("ib")
                {
                    Content = new List<BinaryNode>
                    {
                        new("offline_batch", new Dictionary<string, string> { ["count"] = "100" }),
                    },
                };
                await SendNodeAsync(offlineBatch, ct);
            }
            else if (child.Tag == "offline")
            {
                // WA sends this after delivering all offline messages.
                var offlineCount = child.GetAttr("count") ?? "0";
                _logger.LogInformation("offline delivery complete: {Count} items received", offlineCount);
            }
            else if (child.Tag == "thread_metadata")
            {
                // WhatsApp pushes the chat list on startup as <thread_metadata> items.
                // Each <item from="JID" t="timestamp"/> is an active chat.
                // JIDs may be @lid (privacy-preserving long-lived IDs) or @s.whatsapp.net or @g.us.
                foreach (var item in child.Children)
                {
                    if (item.Tag == "item")
                    {
                        var jid = item.GetAttr("from") ?? "";
                        var ts  = item.GetAttr("t") ?? "0";
                        if (!string.IsNullOrEmpty(jid) && long.TryParse(ts, out var tsVal))
                        {
                            _threadMetadata[jid] = tsVal;
                            _logger.LogDebug("thread_metadata: chat {Jid} t={Ts}", jid, tsVal);
                        }
                    }
                }
                _logger.LogInformation("Cached {Count} chats from thread_metadata", _threadMetadata.Count);
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
        var from           = node.GetAttr("from") ?? "";
        var id             = node.GetAttr("id") ?? "";
        var msgType        = node.GetAttr("type") ?? "";
        var msgCategory    = node.GetAttr("category") ?? "";
        _logger.LogDebug("HandleMessageNode: from={From} id={Id} type={Type} category={Category}", from, id, msgType, msgCategory);
        var participant    = node.GetAttr("participant");
        var participantPn  = node.GetAttr("participant_pn"); // e.g. "31633984381@s.whatsapp.net"
        var senderLid      = node.GetAttr("sender_lid");     // e.g. "70068130029702@lid" (when from= is a phone JID)
        var pushName       = node.GetAttr("notify");

        // Populate LID→phone map from participant_pn attribute
        // participant is the LID JID, participant_pn is the actual phone JID
        var cacheUpdated = false;
        if (!string.IsNullOrEmpty(participant) && !string.IsNullOrEmpty(participantPn)
            && !_lidToPhone.ContainsKey(participant))
        {
            _lidToPhone[participant] = participantPn;
            cacheUpdated = true;
        }

        // Populate LID→phone map from sender_lid attribute (reverse: from=phone JID, sender_lid=LID)
        // This captures messages where the phone JID is in `from` and LID is in `sender_lid`
        if (!string.IsNullOrEmpty(senderLid) && !string.IsNullOrEmpty(from) && from.EndsWith("@s.whatsapp.net")
            && !_lidToPhone.ContainsKey(senderLid))
        {
            _lidToPhone[senderLid] = from;
            cacheUpdated = true;
        }

        // Also store push names keyed by sender JID (may be LID or phone JID)
        if (!string.IsNullOrEmpty(pushName))
        {
            var senderJid = participant ?? from;
            if (!string.IsNullOrEmpty(senderJid) && !_pushNames.ContainsKey(senderJid))
            {
                _pushNames[senderJid] = pushName;
                cacheUpdated = true;
            }
            // If participant is a LID, also map the phone JID if we know it
            if (!string.IsNullOrEmpty(participantPn) && !_pushNames.ContainsKey(participantPn))
            {
                _pushNames[participantPn] = pushName;
                cacheUpdated = true;
            }
            // If from is a phone JID and we have a sender_lid, map both
            if (!string.IsNullOrEmpty(senderLid) && !_pushNames.ContainsKey(senderLid))
            {
                _pushNames[senderLid] = pushName;
                cacheUpdated = true;
            }
        }

        if (cacheUpdated) SaveCacheToDisk();

        // Track this chat in thread metadata
        if (!string.IsNullOrEmpty(from) && long.TryParse(node.GetAttr("t"), out var msgTs))
            _threadMetadata[from] = msgTs;

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

                    if (waMsg.IsHistorySync)
                    {
                        _ = HandleHistorySyncAsync(waMsg.GetHistorySyncNotification()!, id, from, timestamp, CancellationToken.None);
                        continue;
                    }

                    var text      = waMsg.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
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
                    }

                    // ACK all successfully decrypted messages (not just text ones).
                    // Without this, WA re-delivers media/voip/unknown messages on every reconnect.
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
                    // Peer/device messages may not be standard WAMessage format — log at Warning so we can diagnose
                    _logger.LogWarning(parseEx, "Could not parse decrypted message as WAMessage from {Jid} ({Len} bytes, first={First})",
                        from, plaintext.Length, Convert.ToHexString(plaintext[..Math.Min(16, plaintext.Length)]));
                    _ = SendAckAsync(id, from, timestamp);
                    return;
                }

                var text = waMsg.GetText();
                _logger.LogInformation("Parsed WAMessage from {Jid}: text={Text}, hasDeviceSent={DevSent}, hasSKDM={SKDM}, hasHistorySync={HasHS}",
                    from, text ?? "(null)", waMsg.DeviceSentMessage != null, waMsg.SenderKeyDist != null, waMsg.IsHistorySync);

                // ── HistorySync: the phone pushes encrypted chat history blobs ─────────
                if (waMsg.IsHistorySync)
                {
                    _ = HandleHistorySyncAsync(waMsg.GetHistorySyncNotification()!, id, from, timestamp, CancellationToken.None);
                    return; // ACK is sent inside HandleHistorySyncAsync
                }

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
    /// Handles incoming receipt nodes from WhatsApp/phone.
    /// Most importantly handles type="retry" from the phone when it can't decrypt a message we sent.
    /// We ACK the receipt so the server stops re-delivering it, then resend the original message re-encrypted.
    /// </summary>
    private async Task HandleReceiptAsync(BinaryNode node, CancellationToken ct)
    {
        var from      = node.GetAttr("from") ?? "";
        var id        = node.GetAttr("id") ?? "";
        var type      = node.GetAttr("type") ?? "";
        var timestamp = long.TryParse(node.GetAttr("t"), out var ts) ? ts : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _logger.LogInformation("HandleReceipt: from={From} id={Id} type={Type}", from, id, type);

        // ACK the receipt so the server removes it from the delivery queue
        try
        {
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ack = new BinaryNode("ack", new Dictionary<string, string>
            {
                ["id"]    = id,
                ["to"]    = from,
                ["class"] = "receipt",
                ["t"]     = timestamp.ToString(),
            });
            await SendNodeAsync(ack, cts2.Token);
            _logger.LogDebug("Sent ACK for receipt id={Id} from={From}", id, from);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ACK receipt {Id}", id);
        }

        // If the phone tells us it couldn't decrypt one of our PDO messages, resend it
        if (type == "retry" && _sentPdoMsgIds.Contains(id) && _lastPdoRequest.HasValue)
        {
            var req = _lastPdoRequest.Value;
            _logger.LogInformation(
                "HandleReceipt: phone can't decrypt our PDO {Id} — resending fresh PDO for chat {Chat}",
                id, req.ChatJid);
            _ = Task.Run(async () =>
            {
                try
                {
                    await RequestOnDemandHistoryAsync(
                        req.ChatJid, req.OldestMsgId, req.OldestMsgFromMe,
                        req.OldestMsgTimestampMs, req.Count, ct);
                }
                catch (Exception ex2)
                {
                    _logger.LogWarning(ex2, "Failed to resend PDO after retry receipt");
                }
            }, ct);
        }
        else if (type == "retry")
        {
            _logger.LogInformation("HandleReceipt: retry receipt for non-PDO message {Id} from {From} — ACK'd, not resending", id, from);
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
                    // <keys> — our identity + signed pre-key + one-time pre-key so the sender can
                    // re-establish a fresh Signal session. MUST include <key> (one-time pre-key) —
                    // without it the phone ACKs the retry receipt but never re-encrypts the message.
                    // (Baileys messages.ts retryRequestMessage includes preKeys[retryCount+1])
                    BuildRetryKeysNode(_auth),  // uses _retryPreKeyIndex — unique per call
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

    /// Builds a <skey> node from our signed pre-key — included in retry receipts so the
    /// sender can re-encrypt using a fresh Signal session with our current keys.
    private static BinaryNode BuildSignedPreKeyNode(Auth.AuthState auth)
    {
        // id: 3-byte big-endian signed pre-key ID
        var spkId = auth.SignedPreKeyId;
        var idBytes = new byte[] { (byte)(spkId >> 16), (byte)(spkId >> 8), (byte)spkId };
        return new BinaryNode("skey", null, new List<BinaryNode>
        {
            new("id",        null, idBytes),
            new("value",     null, auth.SignedPreKeyPublic),
            new("signature", null, auth.SignedPreKeySignature),
        });
    }

    /// Builds the <keys> node for retry receipts.
    /// Node order matches Baileys: type, identity, key (one-time), skey (signed), device-identity.
    /// The device-identity is REQUIRED for the phone to trust and process the retry receipt.
    /// Uses _retryPreKeyIndex to assign a unique pre-key per message so that if multiple
    /// re-encrypted pkmsg responses arrive they can each be decrypted independently.
    private BinaryNode BuildRetryKeysNode(Auth.AuthState auth)
    {
        static byte[] Be3(uint v) => [(byte)(v >> 16), (byte)(v >> 8), (byte)v];

        var children = new List<BinaryNode>
        {
            new("type",     null, new byte[] { 5 }),   // DJB_TYPE = 0x05
            new("identity", null, auth.SignedIdentityKeyPublic),
        };

        // Pick a unique one-time pre-key for each retry receipt. _retryPreKeyIndex increments
        // per call so that each re-encrypted pkmsg response uses a different X3DH pre-key and
        // can be decrypted independently (keys are consumed by InitIncomingSession on use).
        var keyIndex = _retryPreKeyIndex++;
        var otpk = keyIndex < auth.PreKeys.Count ? auth.PreKeys[keyIndex] : auth.PreKeys.LastOrDefault();
        if (otpk != null)
        {
            // <key> comes BEFORE <skey> — this matches Baileys xmppPreKey / xmppSignedPreKey order
            children.Add(new BinaryNode("key", null, new List<BinaryNode>
            {
                new("id",    null, Be3(otpk.Id)),
                new("value", null, otpk.Public),
            }));
        }

        // <skey> (signed pre-key) AFTER <key>
        children.Add(BuildSignedPreKeyNode(auth));

        // device-identity — REQUIRED! Phone uses this to verify our device and re-establish session.
        // Without it the phone ACKs the retry receipt but never re-encrypts the message.
        if (auth.Account != null)
        {
            children.Add(new BinaryNode("device-identity") { Content = auth.Account });
        }

        return new BinaryNode("keys", null, children);
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

    // Cached app-state collection versions received via server_sync notifications.
    // key = collection name (e.g. "contact", "regular_low"), value = version number
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _serverSyncVersions = new();

    // Thread metadata from <ib><thread_metadata> nodes — key=JID (may be @lid), value=unix timestamp
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _threadMetadata = new();

    // LID → phone JID mapping — populated from participant_pn attributes in incoming messages
    // key = "178430138150925@lid", value = "31633984381@s.whatsapp.net"
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lidToPhone = new();

    // JID → push name — populated from "notify" attribute of incoming messages
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pushNames = new();

    private async Task HandleNotificationAsync(BinaryNode notification, CancellationToken ct)
    {
        var id   = notification.GetAttr("id") ?? "";
        var type = notification.GetAttr("type") ?? "";
        var to   = notification.GetAttr("from") ?? "s.whatsapp.net";

        _logger.LogInformation("Notification: type={Type} id={Id} from={From}", type, id, to);

        // ACK the notification first
        var ack = new BinaryNode("ack", new()
        {
            ["id"]    = id,
            ["to"]    = to,
            ["type"]  = "notification",
            ["class"] = type,
        });
        await SendNodeAsync(ack, ct);

        // --- Handle server_sync notifications ---
        // WhatsApp sends these in response to w:app:state:sync IQs (instead of IQ results).
        // They tell us the current version of each collection.
        if (type == "server_sync")
        {
            var children = notification.Children;
            foreach (var child in children)
            {
                if (child.Tag == "collection")
                {
                    var name    = child.GetAttr("name") ?? "";
                    var verStr  = child.GetAttr("version") ?? "0";
                    if (int.TryParse(verStr, out var ver))
                    {
                        _serverSyncVersions[name] = ver;
                        _logger.LogInformation("server_sync: collection={Name} version={Ver}", name, ver);
                    }
                }
            }

            // If this notification has an ID that matches a pending IQ, resolve it so callers unblock.
            if (!string.IsNullOrEmpty(id) && _pendingIqs.TryGetValue(id, out var tcs))
            {
                _pendingIqs.Remove(id);
                _logger.LogInformation("Resolved pending IQ {Id} via server_sync notification", id);
                tcs.TrySetResult(notification);
            }
        }
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

    /// <summary>
    /// Sends a PeerDataOperationRequestMessage to our primary phone (device 0)
    /// requesting on-demand history sync for <paramref name="chatJid"/>.
    /// The phone responds with one or more HistorySyncNotification events of SyncType=ON_DEMAND (6).
    /// </summary>
    public async Task RequestOnDemandHistoryAsync(
        string chatJid,
        string? oldestMsgId,
        bool oldestMsgFromMe,
        long oldestMsgTimestampMs,
        int count,
        CancellationToken ct)
    {
        var myJid = _auth.Me?.Id;
        if (myJid == null)
        {
            _logger.LogWarning("RequestOnDemandHistory: not authenticated");
            return;
        }

        var myPhone     = myJid.Split('@')[0].Split(':')[0];
        var phoneDevice0 = $"{myPhone}:0@s.whatsapp.net";

        _logger.LogInformation(
            "RequestOnDemandHistory: requesting {Count} msgs for chat {Chat}, oldestMsgId={Id}",
            count, chatJid, oldestMsgId ?? "(none)");

        // Wrap PDO inside ProtocolMessage (field 12), type=16 (PEER_DATA_OPERATION_REQUEST_MESSAGE)
        // This is how Baileys sends it: { protocolMessage: { type: 16, peerDataOperationRequestMessage: pdo } }
        var waMsg = new Dawa.Proto.WAMessage
        {
            ProtocolMsg = new Dawa.Proto.ProtocolMessage
            {
                Type = 16,  // PEER_DATA_OPERATION_REQUEST_MESSAGE (proto field 2 = type enum, value 16)
                PeerDataOperationRequest = new Dawa.Proto.PeerDataOperationRequestMessage
                {
                    RequestType = 3,  // HISTORY_SYNC_ON_DEMAND
                    HistorySyncRequest = new Dawa.Proto.HistorySyncOnDemandRequest
                    {
                        ChatJid                    = chatJid,
                        OldestMsgId               = oldestMsgId,
                        OldestMsgFromMe           = oldestMsgFromMe,
                        OnDemandMsgCount          = count,
                        OldestMsgTimestampSeconds = oldestMsgTimestampMs / 1000,  // proto uses seconds
                    },
                },
            },
        };

        var paddedProto = PadMessage(waMsg.ToByteArray());

        // Get all devices for this phone number (same as how SendTextMessageAsync resolves recipients)
        List<string> deviceJids;
        try { deviceJids = await GetDeviceListAsync(myPhone, ct); }
        catch { deviceJids = [$"{myPhone}:0@s.whatsapp.net"]; }

        // Exclude our own companion device (can't encrypt to ourselves)
        deviceJids = deviceJids.Where(d => d != myJid).ToList();

        if (deviceJids.Count == 0)
        {
            // Fallback: try device 0 directly
            deviceJids = [$"{myPhone}:0@s.whatsapp.net"];
        }

        // PDO messages go to our own phone — always force fresh pre-key exchange so the
        // phone can decrypt our PDO. InitOutgoingSession preserves the existing
        // ReceiveChainKey so we can still decrypt the phone's ON_DEMAND response.
        try
        {
            var bundles = await FetchPreKeyBundlesAsync(deviceJids, ct);
            foreach (var (deviceJid, bundle) in bundles)
                _signalStore.InitOutgoingSession(deviceJid, bundle, _auth);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RequestOnDemandHistory: pre-key bundle fetch failed");
        }

        // Store request params so HandleReceiptAsync can auto-resend on phone retry receipt
        _lastPdoRequest = (chatJid, oldestMsgId, oldestMsgFromMe, oldestMsgTimestampMs, count);

        var msgId    = GenerateMessageId();
        _sentPdoMsgIds.Add(msgId);

        var hasPkMsg = false;
        var toNodes  = new List<BinaryNode>();
        foreach (var d in deviceJids)
        {
            var n = EncryptForDevice(d, paddedProto, ref hasPkMsg);
            if (n != null) toNodes.Add(n);
        }

        if (toNodes.Count == 0)
        {
            _logger.LogError("RequestOnDemandHistory: could not encrypt for any device of {Phone}", myPhone);
            return;
        }

        var participantsNode = new BinaryNode("participants") { Content = toNodes };
        var contentNodes     = new List<BinaryNode> { participantsNode };

        if (hasPkMsg && _auth.Account != null)
        {
            var accountProto = ADVSignedDeviceIdentity.ParseFrom(_auth.Account);
            contentNodes.Add(new BinaryNode("device-identity") { Content = accountProto.ToByteArray() });
        }

        // category="peer" and push_priority="high_force" are required for PDO messages (Baileys pattern)
        var msgNode = new BinaryNode("message", new Dictionary<string, string>
        {
            ["id"]            = msgId,
            ["type"]          = "text",
            ["to"]            = $"{myPhone}@s.whatsapp.net",
            ["category"]      = "peer",
            ["push_priority"] = "high_force",
        }) { Content = contentNodes };

        await SendNodeAsync(msgNode, ct);
        _logger.LogInformation(
            "RequestOnDemandHistory: sent PDO (ProtocolMessage type=16) to {Count} devices of {Phone} for chat {Chat}",
            toNodes.Count, myPhone, chatJid);
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
    private BinaryNode BuildAppStateSyncIQ(string collectionName, int version, bool returnSnapshot)
    {
        var attrs = new Dictionary<string, string>
        {
            ["name"]    = collectionName,
            ["version"] = version.ToString(),
        };
        if (returnSnapshot) attrs["return_snapshot"] = "true";

        return new BinaryNode("iq", new Dictionary<string, string>
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
                    new BinaryNode("collection", attrs),
                }),
            },
        };
    }

    /// <summary>
    /// Returns contacts from multiple sources:
    /// Push names collected from incoming messages (participant_pn, sender_lid, notify attributes).
    /// USync contact queries consistently return empty results for companion devices.
    /// </summary>
    public Task<List<(string Jid, string Name)>> FetchContactsAsync(CancellationToken ct)
    {
        _logger.LogInformation("FetchContactsAsync: starting (pushNames={Names}, lidToPhone={Lids})",
            _pushNames.Count, _lidToPhone.Count);

        var contacts = new List<(string Jid, string Name)>();
        var seenJids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in _pushNames)
        {
            // Resolve LID JIDs to phone JIDs using the lidToPhone mapping
            var jid = kv.Key.EndsWith("@lid") && _lidToPhone.TryGetValue(kv.Key, out var ph) ? ph : kv.Key;
            // Only include contacts with resolved phone JIDs (skip unresolvable LIDs and non-contacts)
            if (jid.EndsWith("@s.whatsapp.net") && seenJids.Add(jid) && !string.IsNullOrEmpty(kv.Value))
                contacts.Add((jid, kv.Value));
        }

        _logger.LogInformation("FetchContactsAsync: returning {Count} contacts", contacts.Count);
        return Task.FromResult(contacts);
    }

    /// <summary>
    /// Uses USync IQ (which reliably returns results for companion devices) to fetch
    /// contact names for a set of JIDs. Handles both @lid and @s.whatsapp.net JIDs.
    /// </summary>
    private async Task<List<(string Jid, string Name)>> FetchContactsViaUsyncAsync(
        IEnumerable<string> jids, CancellationToken ct)
    {
        var jidList = jids.ToList();
        if (jidList.Count == 0) return [];

        // Build user nodes: phone JIDs use jid= attribute, LID JIDs use a <lid> child node
        var userNodes = jidList.Select(j =>
        {
            if (j.EndsWith("@lid"))
            {
                // LID JIDs must be sent as <user><lid>...</lid></user>
                return new BinaryNode("user")
                {
                    Content = new List<BinaryNode>
                    {
                        new BinaryNode("lid") { Content = j },
                    }
                };
            }
            // Phone JIDs: <user jid="31633984381@s.whatsapp.net"/>
            return new BinaryNode("user", new Dictionary<string, string> { ["jid"] = j });
        }).ToList<BinaryNode>();

        var usyncNode = new BinaryNode("usync", new Dictionary<string, string>
        {
            ["context"] = "interactive",
            ["mode"]    = "query",
            ["last"]    = "true",
            ["index"]   = "0",
            ["sid"]     = GenerateMessageId(),
        })
        {
            Content = new List<BinaryNode>
            {
                new BinaryNode("query") { Content = new List<BinaryNode> { new BinaryNode("contact") } },
                new BinaryNode("list")  { Content = userNodes },
            },
        };

        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["to"]    = "@s.whatsapp.net",
            ["type"]  = "get",
            ["xmlns"] = "usync",
            ["id"]    = GenerateMessageId(),
        }) { Content = new List<BinaryNode> { usyncNode } };

        BinaryNode result;
        try { result = await SendIQAsync(iq, ct, timeoutMs: 15000); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchContactsViaUsyncAsync: IQ failed");
            return [];
        }

        // Parse response: <iq><usync><list><user jid="..." lid="..."><contact>name</contact></user>...</list></usync></iq>
        var contacts = new List<(string Jid, string Name)>();
        WalkUsyncForContacts(result, contacts);
        _logger.LogInformation("FetchContactsViaUsyncAsync: parsed {Count} contacts from USync response", contacts.Count);
        return contacts;
    }

    private void WalkUsyncForContacts(BinaryNode node, List<(string Jid, string Name)> result)
    {
        if (node.Tag == "user")
        {
            var jid  = node.GetAttr("jid") ?? "";
            var lid  = node.GetAttr("lid") ?? "";

            // Cache LID → phone JID mapping from USync response
            if (!string.IsNullOrEmpty(lid) && !string.IsNullOrEmpty(jid))
                _lidToPhone[lid] = jid;

            // Look for <contact> child with push name or status
            var contactNode = node.FindChild("contact");
            var name = contactNode?.GetAttr("name")
                    ?? contactNode?.Text
                    ?? node.GetAttr("name");

            if (!string.IsNullOrEmpty(jid))
            {
                // Use phone JID, not LID
                var displayJid = jid.EndsWith("@lid") && _lidToPhone.TryGetValue(jid, out var ph) ? ph : jid;
                var displayName = name ?? (displayJid.Contains('@') ? displayJid.Split('@')[0] : displayJid);
                result.Add((displayJid, displayName));
            }
            return;
        }
        foreach (var child in node.Children)
            WalkUsyncForContacts(child, result);
    }

    // ─── Presence ──────────────────────────────────────────────────────────

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PresenceInfo> _presenceCache = new();

    private void HandlePresenceNode(BinaryNode node)
    {
        var jid    = node.GetAttr("from") ?? "";
        var type   = node.GetAttr("type") ?? "available";
        var status = type == "unavailable" ? "unavailable" : "available";

        var composing = node.FindChild("composing");
        var recording = node.FindChild("recording");
        if (composing != null) status = "composing";
        if (recording != null) status = "recording";

        _presenceCache[jid] = new PresenceInfo(jid, status, DateTime.UtcNow);
        _logger.LogDebug("Presence: {Jid} → {Status}", jid, status);
    }

    public async Task SubscribePresenceAsync(string jid, CancellationToken ct)
    {
        var normalizedJid = jid.Contains('@') ? jid : $"{jid.TrimStart('+')}@s.whatsapp.net";
        var node = new BinaryNode("presence", new Dictionary<string, string>
        {
            ["type"] = "subscribe",
            ["to"]   = normalizedJid,
        });
        await SendNodeAsync(node, ct);
    }

    public PresenceInfo? GetPresence(string jid)
        => _presenceCache.TryGetValue(jid, out var p) ? p : null;

    // ─── Profile picture ────────────────────────────────────────────────────

    public async Task<string?> FetchProfilePictureAsync(string jid, CancellationToken ct)
    {
        var normalizedJid = jid.Contains('@') ? jid : $"{jid.TrimStart('+')}@s.whatsapp.net";
        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["to"]    = normalizedJid,
            ["type"]  = "get",
            ["xmlns"] = "w:profile:pic",
            ["id"]    = GenerateMessageId(),
        })
        {
            Content = new List<BinaryNode>
            {
                // "query=url" tells WhatsApp to return the CDN URL instead of the raw image
                new BinaryNode("picture", new Dictionary<string, string> { ["type"] = "image", ["query"] = "url" })
            }
        };

        try
        {
            var result = await SendIQAsync(iq, ct, timeoutMs: 15000);
            _logger.LogInformation("Profile pic response: tag={Tag} type={Type}", result.Tag, result.GetAttr("type") ?? "?");
            // Result may have a <picture url="..."/> child, or a direct url attr
            var picNode = result.FindChild("picture");
            var url = picNode?.GetAttr("url") ?? result.GetAttr("url");
            _logger.LogInformation("Profile pic url={Url}", url ?? "(none)");
            return url;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch profile picture for {Jid}", jid);
            return null;
        }
    }

    // ─── Read receipts ──────────────────────────────────────────────────────

    /// <summary>
    /// Manually sends a retry receipt to the given sender for a specific message ID.
    /// Use this when a pkmsg was received but could not be decrypted (e.g. pre-key was consumed)
    /// and the server's offline copy has expired. The phone will re-encrypt and resend.
    /// </summary>
    public Task SendManualRetryReceiptAsync(string senderJid, string msgId, long timestamp, CancellationToken ct)
        => SendRetryReceiptAsync(msgId, senderJid, timestamp);

    public async Task SendReadReceiptAsync(string jid, string messageId, long timestamp, CancellationToken ct)
    {
        var normalizedJid = jid.Contains('@') ? jid : $"{jid.TrimStart('+')}@s.whatsapp.net";
        var receipt = new BinaryNode("receipt", new Dictionary<string, string>
        {
            ["id"]   = messageId,
            ["to"]   = normalizedJid,
            ["type"] = "read",
            ["t"]    = timestamp.ToString(),
        });
        await SendNodeAsync(receipt, ct);
        _logger.LogInformation("Sent read receipt for message {MsgId} to {Jid}", messageId, normalizedJid);
    }

    // ─── Chats (from thread_metadata + message history) ─────────────────────

    /// <summary>
    /// Returns the list of active chats. Primary source is thread_metadata from
    /// the ib node that WhatsApp sends immediately after authentication.
    /// Falls back to message history if thread_metadata is empty.
    /// </summary>
    public Task<List<(string Jid, string Name, bool Archived, bool Pinned)>> FetchChatsAsync(CancellationToken ct)
    {
        var chats = new List<(string Jid, string Name, bool Archived, bool Pinned)>();

        // Track seen JIDs to deduplicate: a chat may appear as both LID and phone JID in thread_metadata
        var seenJids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Use thread_metadata as primary source (populated immediately at startup)
        if (_threadMetadata.Count > 0)
        {
            _logger.LogInformation("FetchChatsAsync: returning {Count} chats from thread_metadata", _threadMetadata.Count);

            foreach (var kv in _threadMetadata.OrderByDescending(x => x.Value))
            {
                var rawJid = kv.Key;

                // Resolve LID to phone JID if possible
                string jid;
                if (rawJid.EndsWith("@lid") && _lidToPhone.TryGetValue(rawJid, out var resolvedJid))
                    jid = resolvedJid;
                else
                    jid = rawJid;

                // Deduplicate: skip if we already emitted this resolved JID
                if (!seenJids.Add(jid)) continue;

                // Get display name from push names, then JID
                string name;
                if (_pushNames.TryGetValue(rawJid, out var pn) || _pushNames.TryGetValue(jid, out pn))
                    name = pn;
                else
                    name = jid.Split('@')[0].Split(':')[0];

                chats.Add((jid, name, false, false));
            }
        }
        else
        {
            // Fallback: build from push names cache (populated by received messages)
            _logger.LogInformation("FetchChatsAsync: no thread_metadata, using pushNames cache ({Count} entries)", _pushNames.Count);
            foreach (var kv in _pushNames)
            {
                var jid = kv.Key.EndsWith("@lid") && _lidToPhone.TryGetValue(kv.Key, out var ph) ? ph : kv.Key;
                if (seenJids.Add(jid))
                    chats.Add((jid, kv.Value, false, false));
            }
        }

        _logger.LogInformation("FetchChatsAsync: returning {Count} chats", chats.Count);
        return Task.FromResult(chats);
    }

    /// <summary>Returns internal cache state for debugging.</summary>
    public object GetCacheDebugInfo() => new
    {
        threadMetadataCount = _threadMetadata.Count,
        threadMetadata = _threadMetadata.OrderByDescending(x => x.Value)
            .Take(20)
            .Select(kv => new { jid = kv.Key, t = kv.Value })
            .ToList(),
        lidToPhoneCount = _lidToPhone.Count,
        lidToPhone = _lidToPhone.Take(10)
            .Select(kv => new { lid = kv.Key, phone = kv.Value })
            .ToList(),
        pushNamesCount = _pushNames.Count,
        pushNames = _pushNames.Take(10)
            .Select(kv => new { jid = kv.Key, name = kv.Value })
            .ToList(),
        serverSyncVersions = _serverSyncVersions.ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    // ─── Message history ─────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to fetch message history for a JID using a w:msg sync IQ.
    /// Returns an empty list if the server does not respond (companion devices may not support this).
    /// Note: in-memory message cache is used as primary source; this IQ is a best-effort supplement.
    /// </summary>
    public async Task<List<IncomingMessage>> FetchMessageHistoryAsync(string jid, int count, CancellationToken ct)
    {
        // Normalize JID
        var normalizedJid = jid.Contains('@') ? jid : $"{jid}@s.whatsapp.net";

        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["to"]    = "s.whatsapp.net",
            ["type"]  = "set",
            ["xmlns"] = "w:msg",
            ["id"]    = GenerateMessageId(),
        })
        {
            Content = new List<BinaryNode>
            {
                new BinaryNode("sync")
                {
                    Content = new List<BinaryNode>
                    {
                        new BinaryNode("conversation", new Dictionary<string, string>
                        {
                            ["jid"]   = normalizedJid,
                            ["t"]     = "0",
                            ["count"] = count.ToString(),
                        }),
                    },
                },
            },
        };

        try
        {
            _logger.LogInformation("FetchMessageHistoryAsync: sending w:msg sync IQ for {Jid}", normalizedJid);
            var result = await SendIQAsync(iq, ct, timeoutMs: 20000);
            _logger.LogInformation("FetchMessageHistoryAsync: w:msg result tag={Tag}", result.Tag);

            // Parse any message nodes in the result
            var messages = new List<IncomingMessage>();
            var msgNodes = new List<BinaryNode>();
            CollectNodes(result, "message", msgNodes);
            foreach (var msgNode in msgNodes)
            {
                var text = msgNode.FindChild("body")?.Content as string
                    ?? msgNode.GetAttr("body");
                if (string.IsNullOrEmpty(text)) continue;

                var from = msgNode.GetAttr("from") ?? "";
                long.TryParse(msgNode.GetAttr("t"), out var ts);
                var fromMe = from == _auth.Me?.Id?.Split(':')[0] + "@s.whatsapp.net";
                messages.Add(new IncomingMessage
                {
                    Id        = msgNode.GetAttr("id") ?? "",
                    From      = from,
                    RemoteJid = normalizedJid,
                    Text      = text,
                    FromMe    = fromMe,
                    Timestamp = ts,
                });
            }

            return messages;
        }
        catch (TimeoutException)
        {
            _logger.LogInformation("FetchMessageHistoryAsync: w:msg IQ timed out (companion devices may not support history sync via IQ)");
            return new List<IncomingMessage>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchMessageHistoryAsync: w:msg IQ failed for {Jid}", normalizedJid);
            return new List<IncomingMessage>();
        }
    }

    private static void CollectNodes(BinaryNode node, string tag, List<BinaryNode> result)
    {
        if (node.Tag == tag) result.Add(node);
        foreach (var child in node.Children)
            CollectNodes(child, tag, result);
    }

    // ─── History Sync (HistorySyncNotification → CDN download → proto parse) ─

    private static readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.None, // handle ourselves
    });

    /// <summary>
    /// Handles a HistorySyncNotification message from the phone.
    /// Downloads the encrypted blob from WhatsApp CDN, decrypts it, parses the
    /// HistorySync protobuf, and fires HistorySyncReceived + MessageReceived for each message.
    /// </summary>
    private async Task HandleHistorySyncAsync(
        Dawa.Proto.HistorySyncNotification notification,
        string msgId, string from, long timestamp, CancellationToken ct)
    {
        _logger.LogInformation(
            "HistorySync: type={Type} chunkOrder={Chunk} directPath={Path} fileLen={Len}",
            notification.SyncTypeName, notification.ChunkOrder, notification.DirectPath, notification.FileLength);

        try
        {
            byte[] protoBytes;

            // ── Inline blob path (newer WhatsApp: zlib-compressed HistorySync sent directly) ──
            if (notification.InlineBlob is { Length: > 0 })
            {
                _logger.LogInformation("HistorySync: inline blob {Bytes} bytes — decompressing with zlib", notification.InlineBlob.Length);
                try
                {
                    using var inStream  = new System.IO.MemoryStream(notification.InlineBlob);
                    using var zlib      = new System.IO.Compression.ZLibStream(inStream, System.IO.Compression.CompressionMode.Decompress);
                    using var outStream = new System.IO.MemoryStream();
                    await zlib.CopyToAsync(outStream, ct);
                    protoBytes = outStream.ToArray();
                    _logger.LogInformation("HistorySync: inline decompressed to {Bytes} bytes", protoBytes.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "HistorySync: inline zlib decompress failed");
                    _ = SendAckAsync(msgId, from, timestamp);
                    return;
                }
                goto parseProto;
            }

            // ── CDN path (older WhatsApp: download + decrypt + gunzip) ──────────
            if (string.IsNullOrEmpty(notification.DirectPath))
            {
                _logger.LogWarning("HistorySync: no directPath and no inline blob — skipping");
                _ = SendAckAsync(msgId, from, timestamp);
                return;
            }

            var cdnUrl = "https://mmg.whatsapp.net" + notification.DirectPath;
            byte[] encryptedBlob;
            try
            {
                encryptedBlob = await _httpClient.GetByteArrayAsync(cdnUrl, ct);
                _logger.LogInformation("HistorySync: downloaded {Bytes} bytes from CDN", encryptedBlob.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HistorySync: CDN download failed for {Url}", cdnUrl);
                _ = SendAckAsync(msgId, from, timestamp);
                return;
            }

            // ── 2. Decrypt: HKDF-expand mediaKey → IV(16) + aesKey(32) + macKey(32) ─
            // Info string for history sync is "WhatsApp History Keys"
            // Expand mediaKey using HKDF-SHA256 with WhatsApp's standard media key derivation.
            // Info: "WhatsApp History Keys", salt: 32 zero bytes, output: 112 bytes.
            // Layout (from Baileys): IV=0..15, AES=16..47, MAC=48..79
            var expanded = Dawa.Crypto.DawaHKDF.DeriveKey(
                notification.MediaKey,
                salt: new byte[32], // zero salt
                info: System.Text.Encoding.UTF8.GetBytes("WhatsApp History Keys"),
                outputLength: 80);

            var iv     = expanded[0..16];    // bytes 0..15
            var aesKey = expanded[16..48];   // bytes 16..47
            // macKey is expanded[48..80] — we trust the download, skip MAC verification for now

            // Strip trailing 10-byte HMAC
            var ciphertext = encryptedBlob[..^10];

            byte[] decrypted;
            try
            {
                using var aes = System.Security.Cryptography.Aes.Create();
                aes.Key  = aesKey;
                aes.IV   = iv;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                using var dec = aes.CreateDecryptor();
                decrypted = dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HistorySync: AES decryption failed");
                _ = SendAckAsync(msgId, from, timestamp);
                return;
            }

            // ── 3. Gunzip the decrypted bytes ─────────────────────────────────
            try
            {
                using var inStream  = new System.IO.MemoryStream(decrypted);
                using var gzip      = new System.IO.Compression.GZipStream(inStream, System.IO.Compression.CompressionMode.Decompress);
                using var outStream = new System.IO.MemoryStream();
                await gzip.CopyToAsync(outStream, ct);
                protoBytes = outStream.ToArray();
                _logger.LogInformation("HistorySync: decompressed to {Bytes} bytes", protoBytes.Length);
            }
            catch
            {
                // Not gzip — use raw bytes
                protoBytes = decrypted;
                _logger.LogInformation("HistorySync: not gzip-compressed, using raw {Bytes} bytes", protoBytes.Length);
            }

            parseProto:
            // ── 4. Parse HistorySync protobuf ──────────────────────────────────
            Dawa.Proto.HistorySync historySync;
            try
            {
                historySync = Dawa.Proto.HistorySync.ParseFrom(protoBytes);
                _logger.LogInformation("HistorySync: parsed {ConvCount} conversations, {NameCount} push names",
                    historySync.Conversations.Count, historySync.PushNames.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HistorySync: failed to parse HistorySync proto ({Bytes} bytes)", protoBytes.Length);
                _ = SendAckAsync(msgId, from, timestamp);
                return;
            }

            // ── 5. Store push names from history ─────────────────────────────────
            foreach (var pn in historySync.PushNames)
            {
                if (!string.IsNullOrEmpty(pn.Id) && !string.IsNullOrEmpty(pn.PushName))
                    _pushNames.TryAdd(pn.Id, pn.PushName);
            }

            // ── 6. Fire MessageReceived for each message in each conversation ──
            var totalMessages = 0;
            foreach (var conv in historySync.Conversations)
            {
                var chatJid = conv.Id;
                if (string.IsNullOrEmpty(chatJid)) continue;

                foreach (var wmi in conv.Messages)
                {
                    var key = wmi.Key;
                    if (key == null) continue;

                    var text = wmi.Message?.GetText();
                    if (string.IsNullOrEmpty(text)) continue;

                    var msgFromMe = key.FromMe;
                    var msgFrom   = msgFromMe
                        ? (_auth.Me?.Id ?? chatJid)
                        : (!string.IsNullOrEmpty(key.Participant) ? key.Participant : chatJid);

                    MessageReceived?.Invoke(this, new IncomingMessage
                    {
                        Id          = key.Id,
                        From        = msgFrom,
                        RemoteJid   = key.RemoteJid.Length > 0 ? key.RemoteJid : chatJid,
                        Participant = key.Participant.Length > 0 ? key.Participant : null,
                        Text        = text,
                        FromMe      = msgFromMe,
                        Timestamp   = (long)wmi.MessageTimestamp,
                        PushName    = wmi.PushName,
                    });
                    totalMessages++;
                }

                // Update thread metadata with latest message timestamp
                if (conv.Messages.Count > 0)
                {
                    var latest = conv.Messages.Max(m => (long)m.MessageTimestamp);
                    _threadMetadata.TryAdd(chatJid, latest);
                }
            }

            _logger.LogInformation("HistorySync: fired {Total} MessageReceived events across {Convs} conversations",
                totalMessages, historySync.Conversations.Count);

            // Fire the batch event so callers can persist the full sync
            HistorySyncReceived?.Invoke(this, new HistorySyncBatch(
                SyncType: notification.SyncTypeName,
                ChunkOrder: notification.ChunkOrder,
                ConversationCount: historySync.Conversations.Count,
                MessageCount: totalMessages));

            SaveCacheToDisk();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HistorySync: unexpected error");
        }
        finally
        {
            _ = SendAckAsync(msgId, from, timestamp);
        }
    }

    // ─── Groups ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of group JIDs from thread_metadata (those ending in @g.us).
    /// </summary>
    public List<string> GetGroupJids()
        => _threadMetadata.Keys.Where(j => j.EndsWith("@g.us")).ToList();

    /// <summary>
    /// Fetches group metadata (name, participants) for a specific group JID.
    /// Sends an IQ to the group's server and parses the response.
    /// </summary>
    public async Task<GroupMetadata?> FetchGroupMetadataAsync(string groupJid, CancellationToken ct)
    {
        if (!groupJid.EndsWith("@g.us"))
            throw new ArgumentException("Not a group JID", nameof(groupJid));

        var iq = new BinaryNode("iq", new Dictionary<string, string>
        {
            ["to"]    = groupJid,
            ["type"]  = "get",
            ["xmlns"] = "w:g2",
            ["id"]    = GenerateMessageId(),
        })
        {
            Content = new List<BinaryNode>
            {
                new BinaryNode("query", new Dictionary<string, string> { ["request"] = "interactive" }),
            },
        };

        BinaryNode result;
        try { result = await SendIQAsync(iq, ct, timeoutMs: 15000); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchGroupMetadataAsync: IQ failed for {Group}", groupJid);
            return null;
        }

        // Response: <iq type="result"><group id="..." subject="..." creator="..." creation="...">
        //               <participant type="admin" jid="..."/><participant jid="..."/>...</group></iq>
        var groupNode = FindDeep(result, "group");
        if (groupNode == null)
        {
            _logger.LogWarning("FetchGroupMetadataAsync: no <group> node in response for {Group}", groupJid);
            return null;
        }

        var subject  = groupNode.GetAttr("subject") ?? "";
        var creator  = groupNode.GetAttr("creator") ?? "";
        var creation = groupNode.GetAttr("creation") ?? "0";
        long.TryParse(creation, out var creationTs);

        // Participants — group IQ includes phone_number attribute for LID→phone resolution
        var cacheUpdated = false;
        var participants = groupNode.GetChildren("participant").Select(p =>
        {
            var lidJid   = p.GetAttr("jid") ?? "";
            var phoneJid = p.GetAttr("phone_number");   // e.g. "254708713947@s.whatsapp.net"
            var pType    = p.GetAttr("type") ?? "member";

            // Populate LID→phone cache from the group response
            if (!string.IsNullOrEmpty(lidJid) && !string.IsNullOrEmpty(phoneJid)
                && !_lidToPhone.ContainsKey(lidJid))
            {
                _lidToPhone[lidJid] = phoneJid;
                cacheUpdated = true;
            }

            // Resolve display JID to phone number JID where possible
            var displayJid = (!string.IsNullOrEmpty(phoneJid)) ? phoneJid : lidJid;

            return new GroupParticipant(
                Jid:      displayJid,
                LidJid:   lidJid,
                Type:     pType
            );
        }).Where(p => !string.IsNullOrEmpty(p.Jid)).ToList();

        if (cacheUpdated) SaveCacheToDisk();

        return new GroupMetadata(
            Jid: groupJid,
            Subject: subject,
            Creator: creator,
            CreationTimestamp: creationTs,
            Participants: participants
        );
    }

    private static BinaryNode? FindDeep(BinaryNode node, string tag)
    {
        if (node.Tag == tag) return node;
        foreach (var child in node.Children)
        {
            var found = FindDeep(child, tag);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Resolves a LID JID to a phone JID using a usync IQ query.
    /// If already cached, returns the cached value immediately.
    /// Otherwise sends a usync query to WhatsApp and caches the result.
    /// Returns null if the LID cannot be resolved.
    /// </summary>
    public async Task<string?> ResolveLidAsync(string lidJid, CancellationToken ct)
    {
        if (!lidJid.EndsWith("@lid"))
            return lidJid; // Not a LID, return as-is

        // Check in-memory cache first
        if (_lidToPhone.TryGetValue(lidJid, out var cached))
        {
            _logger.LogInformation("ResolveLidAsync: {Lid} → {Phone} (cached)", lidJid, cached);
            return cached;
        }

        // Send usync IQ to resolve this LID
        _logger.LogInformation("ResolveLidAsync: sending usync IQ to resolve {Lid}", lidJid);
        var contacts = await FetchContactsViaUsyncAsync([lidJid], ct);

        // Check cache again — FetchContactsViaUsyncAsync populates _lidToPhone on success
        if (_lidToPhone.TryGetValue(lidJid, out var resolved))
        {
            _logger.LogInformation("ResolveLidAsync: {Lid} → {Phone} (resolved via usync)", lidJid, resolved);
            SaveCacheToDisk();
            return resolved;
        }

        _logger.LogWarning("ResolveLidAsync: could not resolve {Lid} (usync returned {Count} contacts, none matched)",
            lidJid, contacts.Count);
        return null;
    }

    /// <summary>
    /// Returns all in-memory stored messages for a given JID.
    /// Checks both the given JID and any known LID variant.
    /// </summary>
    public List<IncomingMessage> GetStoredMessages(string jid)
    {
        var result = new List<IncomingMessage>();
        // Note: messages are stored via MessageReceived event in the service layer,
        // not in NoiseProcessor. This returns an empty list — use WhatsAppBridgeService.GetMessagesAsync.
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        await ValueTask.CompletedTask;
    }
}

public record GroupParticipant(string Jid, string LidJid, string Type);
public record GroupMetadata(string Jid, string Subject, string Creator, long CreationTimestamp, List<GroupParticipant> Participants);

/// <summary>Fired when a HistorySync blob has been fully downloaded, decrypted, and processed.</summary>
public record HistorySyncBatch(string SyncType, uint ChunkOrder, int ConversationCount, int MessageCount);

// PresenceInfo record — lives in Dawa.Noise namespace
public record PresenceInfo(string Jid, string Status, DateTime LastSeen);
