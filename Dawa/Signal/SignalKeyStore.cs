using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dawa.Auth;
using Dawa.Crypto;

namespace Dawa.Signal;

/// <summary>
/// Complete Signal Protocol session management — X3DH + Double Ratchet.
/// Handles session establishment (outgoing and incoming), message encryption, and decryption.
/// Persists sessions to signals.json in the session directory.
/// </summary>
public sealed class SignalKeyStore
{
    private readonly string _directory;
    private readonly Dictionary<string, SignalSession> _sessions = new();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private string SessionsFilePath => Path.Combine(_directory, "signals.json");

    public SignalKeyStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        LoadSessions();
    }

    // ─── Session management ───────────────────────────────────────────────────

    public SignalSession? GetSession(string jid) =>
        _sessions.TryGetValue(jid, out var s) ? s : null;

    public void PutSession(string jid, SignalSession session)
    {
        _sessions[jid] = session;
        SaveSessions();
    }

    public bool HasSession(string jid) => _sessions.ContainsKey(jid);

    // ─── X3DH: Outgoing (initiator) ──────────────────────────────────────────

    /// <summary>
    /// Initializes an outgoing session using X3DH key agreement.
    /// Call this before sending the first message to a new contact.
    /// </summary>
    public void InitOutgoingSession(string jid, PreKeyBundle bundle, AuthState auth)
    {
        // Generate ephemeral key pair EK1 (used as baseKey in PreKeyWhisperMessage header)
        var (ek1Priv, ek1Pub) = Curve25519Helper.GenerateKeyPair();

        // Strip 0x05 prefix from their keys before DH operations
        var theirIdentityPub     = StripKeyPrefix(bundle.TheirIdentityPub);
        var theirSignedPreKeyPub = StripKeyPrefix(bundle.TheirSignedPreKeyPub);

        // X3DH key agreement (WhatsApp/Signal convention):
        // DH1 = DH(our_identity_priv, their_signed_prekey_pub)
        // DH2 = DH(our_ephemeral_priv, their_identity_pub)
        // DH3 = DH(our_ephemeral_priv, their_signed_prekey_pub)
        // DH4 = DH(our_ephemeral_priv, their_one_time_prekey_pub) [if present]
        var dh1 = Curve25519Helper.DH(auth.SignedIdentityKeyPrivate, theirSignedPreKeyPub);
        var dh2 = Curve25519Helper.DH(ek1Priv, theirIdentityPub);
        var dh3 = Curve25519Helper.DH(ek1Priv, theirSignedPreKeyPub);

        byte[]? dh4 = null;
        if (bundle.TheirOneTimePreKeyPub != null && bundle.TheirOneTimePreKeyPub.Length > 0)
        {
            var theirOtpk = StripKeyPrefix(bundle.TheirOneTimePreKeyPub);
            dh4 = Curve25519Helper.DH(ek1Priv, theirOtpk);
        }

        // F = 32 bytes of 0xFF (Signal version 3 discontinuity padding)
        var f = new byte[32];
        Array.Fill(f, (byte)0xFF);

        // masterSecret = F || DH1 || DH2 || DH3 [|| DH4]
        var masterParts = new List<byte[]> { f, dh1, dh2, dh3 };
        if (dh4 != null) masterParts.Add(dh4);
        var masterSecret = Concat(masterParts.ToArray());

        // (rootKey, sharedChainKey) = HKDF(masterSecret, salt=zero32, info="WhisperText", 64)
        var zeroSalt = new byte[32];
        var info     = Encoding.UTF8.GetBytes("WhisperText");
        var derived  = DawaHKDF.DeriveKey(masterSecret, zeroSalt, info, 64);
        var rootKey0 = derived[..32];
        // sharedChainKey is derived but not directly used — we immediately do the first ratchet

        // Generate separate ratchet key pair EK2
        var (ek2Priv, ek2Pub) = Curve25519Helper.GenerateKeyPair();

        // First DH ratchet step using their signed prekey as the initial ratchet public key
        var (rootKey2, sendChainKey) = HkdfRatchetStep(rootKey0, Curve25519Helper.DH(ek2Priv, theirSignedPreKeyPub));

        var session = new SignalSession
        {
            RemoteJid                = jid,
            RootKey                  = rootKey2,
            SendChainKey             = sendChainKey,
            ReceiveChainKey          = [],
            SendCounter              = 0,
            ReceiveCounter           = 0,
            PrevSendCounter          = 0,
            TheirCurrentRatchetPublic = bundle.TheirSignedPreKeyPub,
            OurRatchetPrivate        = ek2Priv,
            OurRatchetPublic         = ek2Pub,
            BaseKey                  = ek1Pub,
            PreKeyId                 = bundle.TheirOneTimePreKeyId,
            SignedPreKeyId           = bundle.TheirSignedPreKeyId,
            PeerRegistrationId       = bundle.PeerRegistrationId,
            IsEstablished            = false,  // first message will be PreKeyWhisperMessage
        };

        _sessions[jid] = session;
        SaveSessions();
    }

    // ─── X3DH: Incoming (responder) ──────────────────────────────────────────

    /// <summary>
    /// Initializes an incoming session when we receive the first PreKeyWhisperMessage.
    /// </summary>
    public void InitIncomingSession(string jid, PreKeyWhisperMessageProto pkmsg, AuthState auth)
    {
        var theirIdentityPub = StripKeyPrefix(pkmsg.IdentityKey);
        var baseKey          = StripKeyPrefix(pkmsg.BaseKey);

        // X3DH receiver side:
        // DH1 = DH(our_signed_prekey_priv, their_identity_pub)
        // DH2 = DH(our_identity_priv, their_base_key)
        // DH3 = DH(our_signed_prekey_priv, their_base_key)
        // DH4 = DH(our_one_time_prekey_priv[preKeyId], their_base_key) [if preKeyId != 0]
        var dh1 = Curve25519Helper.DH(auth.SignedPreKeyPrivate, theirIdentityPub);
        var dh2 = Curve25519Helper.DH(auth.SignedIdentityKeyPrivate, baseKey);
        var dh3 = Curve25519Helper.DH(auth.SignedPreKeyPrivate, baseKey);

        byte[]? dh4 = null;
        if (pkmsg.PreKeyId != 0)
        {
            var otpk = auth.PreKeys.FirstOrDefault(k => k.Id == pkmsg.PreKeyId);
            if (otpk != null)
            {
                dh4 = Curve25519Helper.DH(otpk.Private, baseKey);
                auth.PreKeys.RemoveAll(k => k.Id == pkmsg.PreKeyId);
            }
        }

        var f = new byte[32];
        Array.Fill(f, (byte)0xFF);

        var masterParts = new List<byte[]> { f, dh1, dh2, dh3 };
        if (dh4 != null) masterParts.Add(dh4);
        var masterSecret = Concat(masterParts.ToArray());

        var zeroSalt = new byte[32];
        var info     = Encoding.UTF8.GetBytes("WhisperText");
        var derived  = DawaHKDF.DeriveKey(masterSecret, zeroSalt, info, 64);
        var rootKey0 = derived[..32];

        // DH ratchet to initialize receive chain using the ratchet key from the WhisperMessage header
        // The inner WhisperMessage is embedded in pkmsg.Message — parse it to get the ratchet key
        // pkmsg.Message = [0x33] + proto_bytes + mac  (the full whisper frame)
        // We only need the ratchet key from the proto to set up the receive chain
        byte[] theirRatchetPub;
        if (pkmsg.Message.Length > 1)
        {
            // Strip version byte, then try to parse the proto (ignore MAC for key extraction)
            try
            {
                var protoBytes = pkmsg.Message[1..Math.Max(1, pkmsg.Message.Length - 8)];
                var innerMsg = WhisperMessageProto.ParseFrom(protoBytes);
                theirRatchetPub = innerMsg.RatchetKey.Length > 0 ? innerMsg.RatchetKey : StripKeyPrefix(pkmsg.BaseKey);
            }
            catch
            {
                theirRatchetPub = StripKeyPrefix(pkmsg.BaseKey);
            }
        }
        else
        {
            theirRatchetPub = StripKeyPrefix(pkmsg.BaseKey);
        }

        var (rootKey2, receiveChainKey) = HkdfRatchetStep(rootKey0, Curve25519Helper.DH(auth.SignedPreKeyPrivate, theirRatchetPub));

        var session = new SignalSession
        {
            RemoteJid                = jid,
            RootKey                  = rootKey2,
            SendChainKey             = [],
            ReceiveChainKey          = receiveChainKey,
            SendCounter              = 0,
            ReceiveCounter           = 0,
            PrevSendCounter          = 0,
            TheirCurrentRatchetPublic = theirRatchetPub,
            OurRatchetPrivate        = auth.SignedPreKeyPrivate,
            OurRatchetPublic         = auth.SignedPreKeyPublic,
            BaseKey                  = pkmsg.BaseKey,
            PreKeyId                 = pkmsg.PreKeyId,
            SignedPreKeyId           = pkmsg.SignedPreKeyId,
            PeerRegistrationId       = 0,
            IsEstablished            = true,
        };

        _sessions[jid] = session;
        SaveSessions();
    }

    // ─── Encrypt ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts a plaintext for the given JID using the Double Ratchet.
    /// Returns (encryptedBytes, isPreKey) where isPreKey=true means the caller
    /// should wrap this in a PreKeyWhisperMessage frame.
    /// </summary>
    public (byte[] encBytes, bool isPreKey) EncryptMessage(string jid, byte[] plaintext, AuthState auth)
    {
        var session = _sessions.TryGetValue(jid, out var s) ? s
            : throw new InvalidOperationException($"No Signal session for {jid}. Call InitOutgoingSession first.");

        // Derive message key from current send chain key
        var (messageKey, nextChainKey) = DeriveMessageKeys(session.SendChainKey);
        session.SendChainKey = nextChainKey;

        // Expand message key: HKDF(messageKey, zero_salt, empty_info, 80)
        var keyMaterial = DawaHKDF.DeriveKey(messageKey, new byte[32], null, 80);
        var encKey = keyMaterial[..32];
        var macKey = keyMaterial[32..64];
        var iv     = keyMaterial[64..80];

        // AES-CBC encrypt
        var ciphertext = MessageCipher.AesCbcEncrypt(encKey, iv, plaintext);

        // Build WhisperMessageProto
        var whisperProto = new WhisperMessageProto
        {
            RatchetKey      = session.OurRatchetPublic,
            Counter         = session.SendCounter,
            PreviousCounter = session.PrevSendCounter,
            Ciphertext      = ciphertext,
        };
        var protoBytes = whisperProto.ToByteArray();

        // MAC = HMAC-SHA256(macKey, [0x33] || proto_bytes)[0:8]
        var macInput = new byte[1 + protoBytes.Length];
        macInput[0] = 0x33;
        protoBytes.CopyTo(macInput, 1);
        var mac = HMACSHA256.HashData(macKey, macInput)[..8];

        // whisperBytes = [0x33] + proto_bytes + mac
        var whisperBytes = new byte[1 + protoBytes.Length + 8];
        whisperBytes[0] = 0x33;
        protoBytes.CopyTo(whisperBytes, 1);
        mac.CopyTo(whisperBytes, 1 + protoBytes.Length);

        session.SendCounter++;

        bool isPreKey = !session.IsEstablished;

        byte[] result;
        if (isPreKey)
        {
            // Wrap in PreKeyWhisperMessageProto
            var pkProto = new PreKeyWhisperMessageProto
            {
                PreKeyId       = session.PreKeyId,
                BaseKey        = session.BaseKey,
                IdentityKey    = auth.SignedIdentityKeyPublic,
                Message        = whisperBytes,
                RegistrationId = auth.RegistrationId,
                SignedPreKeyId = session.SignedPreKeyId,
            };
            var pkProtoBytes = pkProto.ToByteArray();
            result = new byte[1 + pkProtoBytes.Length];
            result[0] = 0x33;
            pkProtoBytes.CopyTo(result, 1);

            // Mark session as established after first message sent
            session.IsEstablished = true;
        }
        else
        {
            result = whisperBytes;
        }

        SaveSessions();
        return (result, isPreKey);
    }

    // ─── Decrypt ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Decrypts a received Signal message. type is "pkmsg" or "msg".
    /// </summary>
    public byte[] DecryptMessage(string jid, string type, byte[] ciphertext, AuthState auth)
    {
        if (type == "pkmsg")
        {
            // Parse PreKeyWhisperMessageProto from ciphertext[1:]
            if (ciphertext.Length < 2)
                throw new CryptographicException("PreKeyWhisperMessage too short.");

            var pkProto = PreKeyWhisperMessageProto.ParseFrom(ciphertext[1..]);
            InitIncomingSession(jid, pkProto, auth);

            // The inner message is in pkProto.Message
            return DecryptWhisperMessage(jid, pkProto.Message, auth);
        }
        else
        {
            // type == "msg": regular WhisperMessage
            return DecryptWhisperMessage(jid, ciphertext, auth);
        }
    }

    // ─── Internal decrypt ────────────────────────────────────────────────────

    private byte[] DecryptWhisperMessage(string jid, byte[] whisperFrame, AuthState auth)
    {
        if (whisperFrame.Length < 10)
            throw new CryptographicException("WhisperMessage too short.");

        // whisperFrame = [0x33] + proto_bytes + mac(8)
        var protoBytes = whisperFrame[1..(whisperFrame.Length - 8)];
        var receivedMac = whisperFrame[(whisperFrame.Length - 8)..];

        var innerMsg = WhisperMessageProto.ParseFrom(protoBytes);

        var session = _sessions.TryGetValue(jid, out var s) ? s
            : throw new InvalidOperationException($"No Signal session for {jid}.");

        // Check if we need to do a DH ratchet step (their ratchet key changed)
        var theirRatchetPub = innerMsg.RatchetKey;
        if (!theirRatchetPub.SequenceEqual(session.TheirCurrentRatchetPublic))
        {
            // DH ratchet: advance receive chain with new ratchet key
            var (newRootKey, newReceiveChainKey) = HkdfRatchetStep(
                session.RootKey,
                Curve25519Helper.DH(session.OurRatchetPrivate, theirRatchetPub));

            session.PrevSendCounter          = session.SendCounter;
            session.TheirCurrentRatchetPublic = theirRatchetPub;
            session.RootKey                  = newRootKey;
            session.ReceiveChainKey          = newReceiveChainKey;
            session.ReceiveCounter           = 0;

            // Generate new ratchet key pair for next send
            var (newRatchPriv, newRatchPub) = Curve25519Helper.GenerateKeyPair();
            var (newRootKey2, newSendChainKey) = HkdfRatchetStep(
                newRootKey,
                Curve25519Helper.DH(newRatchPriv, theirRatchetPub));
            session.OurRatchetPrivate = newRatchPriv;
            session.OurRatchetPublic  = newRatchPub;
            session.RootKey           = newRootKey2;
            session.SendChainKey      = newSendChainKey;
            session.SendCounter       = 0;
        }

        // Derive message key
        var (messageKey, nextChainKey) = DeriveMessageKeys(session.ReceiveChainKey);
        session.ReceiveChainKey = nextChainKey;

        // Expand message key
        var keyMaterial = DawaHKDF.DeriveKey(messageKey, new byte[32], null, 80);
        var encKey = keyMaterial[..32];
        var macKey = keyMaterial[32..64];
        var iv     = keyMaterial[64..80];

        // Verify MAC
        var macInput = new byte[1 + protoBytes.Length];
        macInput[0] = 0x33;
        protoBytes.CopyTo(macInput, 1);
        var expectedMac = HMACSHA256.HashData(macKey, macInput)[..8];
        if (!expectedMac.AsSpan().SequenceEqual(receivedMac))
            throw new CryptographicException("WhisperMessage MAC verification failed.");

        session.ReceiveCounter++;

        var plaintext = MessageCipher.AesCbcDecrypt(encKey, iv, innerMsg.Ciphertext);
        SaveSessions();
        return plaintext;
    }

    // ─── Pre-key access ───────────────────────────────────────────────────────

    public void RemovePreKey(uint id, AuthState auth)
    {
        auth.PreKeys.RemoveAll(k => k.Id == id);
    }

    // ─── Message key derivation (symmetric ratchet) ───────────────────────────

    private static (byte[] messageKey, byte[] nextChainKey) DeriveMessageKeys(byte[] chainKey)
    {
        var messageKey   = HMACSHA256.HashData(chainKey, new byte[] { 0x01 });
        var nextChainKey = HMACSHA256.HashData(chainKey, new byte[] { 0x02 });
        return (messageKey, nextChainKey);
    }

    // ─── DH Ratchet step ──────────────────────────────────────────────────────

    /// <summary>
    /// Performs one DH ratchet step.
    /// Returns (newRootKey, newChainKey).
    /// Uses rootKey as salt, dhOutput as IKM, "WhisperRatchet" as info.
    /// </summary>
    private static (byte[] newRootKey, byte[] chainKey) HkdfRatchetStep(byte[] rootKey, byte[] dhOutput)
    {
        var info   = Encoding.UTF8.GetBytes("WhisperRatchet");
        var output = DawaHKDF.DeriveKey(dhOutput, rootKey, info, 64);
        return (output[..32], output[32..]);
    }

    // ─── Key prefix stripping ─────────────────────────────────────────────────

    /// <summary>
    /// If key is 33 bytes and key[0]==0x05, returns key[1:].
    /// Otherwise returns the key as-is (or trimmed to 32 bytes if needed).
    /// </summary>
    public static byte[] StripKeyPrefix(byte[] key)
    {
        if (key.Length == 33 && key[0] == 0x05)
            return key[1..];
        return key;
    }

    // ─── Persistence ──────────────────────────────────────────────────────────

    private void LoadSessions()
    {
        try
        {
            if (!File.Exists(SessionsFilePath)) return;
            var json = File.ReadAllText(SessionsFilePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, SignalSession>>(json, _jsonOpts);
            if (loaded == null) return;
            foreach (var (k, v) in loaded)
                _sessions[k] = v;
        }
        catch
        {
            // Ignore corrupt sessions file — start fresh
        }
    }

    private void SaveSessions()
    {
        try
        {
            var json = JsonSerializer.Serialize(_sessions, _jsonOpts);
            File.WriteAllText(SessionsFilePath, json);
        }
        catch
        {
            // Best-effort persistence
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(a => a.Length)];
        var offset = 0;
        foreach (var a in arrays)
        {
            a.CopyTo(result, offset);
            offset += a.Length;
        }
        return result;
    }
}

// ─── Session model ────────────────────────────────────────────────────────────

/// <summary>A Signal Double Ratchet session for a single remote device JID.</summary>
public sealed class SignalSession
{
    public string RemoteJid { get; set; } = "";
    public byte[] RootKey { get; set; } = [];
    public byte[] SendChainKey { get; set; } = [];
    public byte[] ReceiveChainKey { get; set; } = [];
    public uint SendCounter { get; set; }
    public uint ReceiveCounter { get; set; }
    public uint PrevSendCounter { get; set; }
    public byte[] TheirCurrentRatchetPublic { get; set; } = [];
    public byte[] OurRatchetPrivate { get; set; } = [];
    public byte[] OurRatchetPublic { get; set; } = [];

    /// <summary>X3DH ephemeral public key — sent in the PreKeyWhisperMessage header.</summary>
    public byte[] BaseKey { get; set; } = [];

    /// <summary>Their one-time pre-key ID used during X3DH (0 = none).</summary>
    public uint PreKeyId { get; set; }

    /// <summary>Their signed pre-key ID used during X3DH.</summary>
    public uint SignedPreKeyId { get; set; }

    /// <summary>Their registration ID.</summary>
    public uint PeerRegistrationId { get; set; }

    /// <summary>
    /// False when session was just initialized and the first outgoing message
    /// must be wrapped in a PreKeyWhisperMessage. Set to true after first send.
    /// </summary>
    public bool IsEstablished { get; set; }
}

// ─── PreKeyBundle ─────────────────────────────────────────────────────────────

/// <summary>Pre-key bundle fetched from the server for a remote device.</summary>
public sealed class PreKeyBundle
{
    public byte[] TheirIdentityPub { get; set; } = [];
    public byte[] TheirSignedPreKeyPub { get; set; } = [];
    public uint TheirSignedPreKeyId { get; set; }
    public byte[] TheirSignedPreKeySig { get; set; } = [];
    public byte[]? TheirOneTimePreKeyPub { get; set; }
    public uint TheirOneTimePreKeyId { get; set; }
    public uint PeerRegistrationId { get; set; }
}
