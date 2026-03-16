namespace Dawa.Proto;

// ─────────────────────────────────────────────────────────────
// Hand-crafted proto3 for WhatsApp message types.
// Field numbers verified against WAProto.proto from Baileys.
// ─────────────────────────────────────────────────────────────

public sealed class ClientPayload
{
    public ulong  Username      { get; set; }
    public bool   Passive       { get; set; }
    public UserAgent? UserAgent { get; set; }
    public WebInfo?   WebInfo   { get; set; }
    // ConnectType enum: WIFI_UNKNOWN=1
    public int    ConnectType   { get; set; } = 1;
    // ConnectReason enum: USER_ACTIVATED=1
    public int    ConnectReason { get; set; } = 1;
    public uint   Device        { get; set; }
    public DevicePairingRegistrationData? DevicePairingData { get; set; }
    public bool   Pull          { get; set; }

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        if (Username != 0)     ProtoEncoder.WriteUInt64(buf, 1, Username);
                               ProtoEncoder.WriteBoolAlways(buf, 3, Passive);       // always emit (Baileys emits false)
        if (UserAgent != null) ProtoEncoder.WriteMessage(buf, 5, UserAgent.ToByteArray());
        if (WebInfo   != null) ProtoEncoder.WriteMessageAlways(buf, 6, WebInfo.ToByteArray()); // always emit, even when 0-value
                               ProtoEncoder.WriteInt32(buf, 12, ConnectType);
                               ProtoEncoder.WriteInt32(buf, 13, ConnectReason);
        if (Device != 0)       ProtoEncoder.WriteUInt32(buf, 18, Device);
        if (DevicePairingData != null) ProtoEncoder.WriteMessage(buf, 19, DevicePairingData.ToByteArray());
                               ProtoEncoder.WriteBoolAlways(buf, 33, Pull);         // always emit (Baileys emits false)
        return [.. buf];
    }
}

public sealed class DevicePairingRegistrationData
{
    public byte[] ERegid   { get; set; } = [];
    public byte[] EKeytype { get; set; } = [];
    public byte[] EIdent   { get; set; } = [];
    public byte[] ESkeyId  { get; set; } = [];
    public byte[] ESkeyVal { get; set; } = [];
    public byte[] ESkeySig { get; set; } = [];
    public byte[] BuildHash   { get; set; } = [];
    public byte[] DeviceProps { get; set; } = [];

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteBytes(buf, 1, ERegid);
        ProtoEncoder.WriteBytes(buf, 2, EKeytype);
        ProtoEncoder.WriteBytes(buf, 3, EIdent);
        ProtoEncoder.WriteBytes(buf, 4, ESkeyId);
        ProtoEncoder.WriteBytes(buf, 5, ESkeyVal);
        ProtoEncoder.WriteBytes(buf, 6, ESkeySig);
        ProtoEncoder.WriteBytes(buf, 7, BuildHash);
        ProtoEncoder.WriteBytes(buf, 8, DeviceProps);
        return [.. buf];
    }
}

/// <summary>DeviceProps (companion registration info), field 8 of DevicePairingRegistrationData.</summary>
public sealed class DevicePropsMessage
{
    public string Os             { get; set; } = "Ubuntu";
    // Version = WA app version (Baileys sends [2, 3000, tertiary]), NOT the OS/browser version.
    public AppVersion? Version   { get; set; } = new AppVersion { Primary = 2, Secondary = 3000, Tertiary = 1033846690 };
    // PlatformType: CHROME=1
    public int PlatformType      { get; set; } = 1;
    // requireFullSync: true triggers the server to include history in initial sync (Baileys default: true)
    public bool RequireFullSync  { get; set; } = true;
    public HistorySyncConfigMessage? HistorySyncConfig { get; set; } = new();

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, Os);
        if (Version != null) ProtoEncoder.WriteMessage(buf, 2, Version.ToByteArray());
        ProtoEncoder.WriteInt32(buf, 3, PlatformType);
        ProtoEncoder.WriteBool(buf, 4, RequireFullSync);  // always emit — server expects it
        if (HistorySyncConfig != null) ProtoEncoder.WriteMessage(buf, 5, HistorySyncConfig.ToByteArray());
        return [.. buf];
    }
}

/// <summary>HistorySyncConfig nested in DeviceProps (field 5).</summary>
public sealed class HistorySyncConfigMessage
{
    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteUInt32(buf, 1, 3);        // fullSyncDaysLimit = 3  (Baileys: fullCount)
        ProtoEncoder.WriteUInt32(buf, 3, 2048);     // storageQuotaMb = 2048  (Baileys default)
        ProtoEncoder.WriteBool(buf, 4, true);        // inlineInitialPayloadInE2EeMsg
        ProtoEncoder.WriteUInt32(buf, 5, 25);        // recentSyncChunkSize = 25  (Baileys: count)
        // field 6: supportCallLogHistory = false — omit
        ProtoEncoder.WriteBool(buf, 7, true);        // supportBotUserAgentChatHistory
        ProtoEncoder.WriteBool(buf, 8, true);        // supportCagReactionsAndPolls
        ProtoEncoder.WriteBool(buf, 9, true);        // supportBizHostedMsg
        ProtoEncoder.WriteBool(buf, 10, true);       // supportRecentSyncChunkMessageCountTuning
        ProtoEncoder.WriteBool(buf, 11, true);       // supportHostedGroupMsg
        ProtoEncoder.WriteBool(buf, 12, true);       // supportFbidBotChatHistory
        ProtoEncoder.WriteBool(buf, 14, true);       // supportMessageAssociation
        // field 15: supportGroupHistory = false — omit
        return [.. buf];
    }
}

public sealed class UserAgent
{
    // Platform: WEB=14
    public int        Platform                  { get; set; } = 14;
    public AppVersion? AppVersion               { get; set; }
    public string Mcc                           { get; set; } = "000";
    public string Mnc                           { get; set; } = "000";
    public string OsVersion                     { get; set; } = "0.1";
    public string Manufacturer                  { get; set; } = "";
    public string Device                        { get; set; } = "Desktop";
    public string OsBuildNumber                 { get; set; } = "0.1";
    // field 9 = phoneId (skip)
    // field 10 = releaseChannel (RELEASE=0, default, skip)
    public string LocaleLanguageIso6391         { get; set; } = "en";  // field 11
    public string LocaleCountryIso31661Alpha2   { get; set; } = "US";  // field 12

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteInt32(buf, 1, Platform);
        if (AppVersion != null) ProtoEncoder.WriteMessage(buf, 2, AppVersion.ToByteArray());
        ProtoEncoder.WriteString(buf, 3, Mcc);
        ProtoEncoder.WriteString(buf, 4, Mnc);
        ProtoEncoder.WriteString(buf, 5, OsVersion);
        if (!string.IsNullOrEmpty(Manufacturer)) ProtoEncoder.WriteString(buf, 6, Manufacturer);
        ProtoEncoder.WriteString(buf, 7, Device);
        ProtoEncoder.WriteString(buf, 8, OsBuildNumber);
        // field 9 = phoneId (skip)
        ProtoEncoder.WriteInt32Always(buf, 10, 0); // releaseChannel = RELEASE(0), always emit (Baileys does)
        ProtoEncoder.WriteString(buf, 11, LocaleLanguageIso6391);
        ProtoEncoder.WriteString(buf, 12, LocaleCountryIso31661Alpha2);
        return [.. buf];
    }
}

public sealed class AppVersion
{
    public uint Primary   { get; set; } = 2;
    public uint Secondary { get; set; } = 3000;
    public uint Tertiary  { get; set; } = 1027934701;

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteUInt32(buf, 1, Primary);
        ProtoEncoder.WriteUInt32(buf, 2, Secondary);
        ProtoEncoder.WriteUInt32(buf, 3, Tertiary);
        return [.. buf];
    }
}

public sealed class WebInfo
{
    // WebSubPlatform: WEB_BROWSER=0
    public int WebSubPlatform { get; set; } = 0;

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteInt32Always(buf, 4, WebSubPlatform); // always emit 0 (Baileys does this)
        return [.. buf];
    }
}

// ─────────────────────────────────────────────────────────────
// ADV (Advanced Device Verification) protos used in QR pairing.
// Field numbers verified against WAProto.proto from Baileys.
// ─────────────────────────────────────────────────────────────

/// <summary>Server sends this in device-identity during pair-success. Contains HMAC-protected device identity.</summary>
public sealed class ADVSignedDeviceIdentityHMAC
{
    public byte[] Details     { get; set; } = [];  // field 1: encoded ADVSignedDeviceIdentity
    public byte[] Hmac        { get; set; } = [];  // field 2: HMAC-SHA256 over details
    public int    AccountType { get; set; } = 0;   // field 3: 0=E2EE, 1=HOSTED

    public static ADVSignedDeviceIdentityHMAC ParseFrom(byte[] data)
    {
        var msg = new ADVSignedDeviceIdentityHMAC();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.Details     = r.ReadBytes(); break;
                case 2: msg.Hmac        = r.ReadBytes(); break;
                case 3: msg.AccountType = r.ReadInt32(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>Decoded from ADVSignedDeviceIdentityHMAC.Details. Client adds deviceSignature and re-encodes.</summary>
public sealed class ADVSignedDeviceIdentity
{
    public byte[] Details             { get; set; } = [];  // field 1: encoded ADVDeviceIdentity
    public byte[] AccountSignatureKey { get; set; } = [];  // field 2: phone's Curve25519 public key
    public byte[] AccountSignature    { get; set; } = [];  // field 3: phone's XEdDSA signature
    public byte[] DeviceSignature     { get; set; } = [];  // field 4: client fills this in

    public static ADVSignedDeviceIdentity ParseFrom(byte[] data)
    {
        var msg = new ADVSignedDeviceIdentity();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.Details             = r.ReadBytes(); break;
                case 2: msg.AccountSignatureKey = r.ReadBytes(); break;
                case 3: msg.AccountSignature    = r.ReadBytes(); break;
                case 4: msg.DeviceSignature     = r.ReadBytes(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }

    /// <summary>Encode all fields including accountSignatureKey (for device-identity in outgoing pkmsg).</summary>
    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteBytes(buf, 1, Details);
        if (AccountSignatureKey.Length > 0) ProtoEncoder.WriteBytes(buf, 2, AccountSignatureKey);
        ProtoEncoder.WriteBytes(buf, 3, AccountSignature);
        ProtoEncoder.WriteBytes(buf, 4, DeviceSignature);
        return [.. buf];
    }

    /// <summary>Encode WITHOUT accountSignatureKey (field 2 omitted per Baileys protocol).</summary>
    public byte[] ToByteArrayForReply()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteBytes(buf, 1, Details);
        // field 2 (accountSignatureKey) intentionally omitted in reply
        ProtoEncoder.WriteBytes(buf, 3, AccountSignature);
        ProtoEncoder.WriteBytes(buf, 4, DeviceSignature);
        return [.. buf];
    }
}

/// <summary>Decoded from ADVSignedDeviceIdentity.Details. Used to get keyIndex for the reply.</summary>
public sealed class ADVDeviceIdentity
{
    public uint   RawId       { get; set; }  // field 1
    public ulong  Timestamp   { get; set; }  // field 2
    public uint   KeyIndex    { get; set; }  // field 3
    public int    AccountType { get; set; }  // field 4
    public int    DeviceType  { get; set; }  // field 5

    public static ADVDeviceIdentity ParseFrom(byte[] data)
    {
        var msg = new ADVDeviceIdentity();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.RawId       = r.ReadUInt32(); break;
                case 2: msg.Timestamp   = r.ReadUInt64(); break;
                case 3: msg.KeyIndex    = r.ReadUInt32(); break;
                case 4: msg.AccountType = r.ReadInt32();  break;
                case 5: msg.DeviceType  = r.ReadInt32();  break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>
/// WhatsApp Message proto (field numbers from Baileys WAProto.proto).
/// Only the fields we need for text message extraction are implemented;
/// all others are safely skipped.
/// </summary>
public sealed class WAMessage
{
    public string? Conversation { get; set; }                        // field 1
    public SenderKeyDistributionMessage? SenderKeyDist { get; set; } // field 2
    public ExtendedTextMessage? ExtendedTextMessage { get; set; }    // field 6
    public ProtocolMessage? ProtocolMsg { get; set; }                // field 12
    public DeviceSentMessage? DeviceSentMessage { get; set; }           // field 31
    public MessageContextInfo? MessageContextInfo { get; set; }         // field 35
    public HistorySyncNotification? HistorySyncNotification { get; set; } // field 46

    /// <summary>Extracts the text from whatever message type this is.</summary>
    public string? GetText()
    {
        if (!string.IsNullOrEmpty(Conversation))
            return Conversation;
        if (ExtendedTextMessage != null && !string.IsNullOrEmpty(ExtendedTextMessage.Text))
            return ExtendedTextMessage.Text;
        if (DeviceSentMessage?.Message != null)
            return DeviceSentMessage.Message.GetText();
        return null;
    }

    /// <summary>True if this is a HistorySync notification message (field 46 or via ProtocolMessage field 6).</summary>
    public bool IsHistorySync => HistorySyncNotification != null || ProtocolMsg?.HistorySyncNotification != null;

    /// <summary>Returns HistorySyncNotification from either WAMessage field 46 or ProtocolMessage field 6.</summary>
    public HistorySyncNotification? GetHistorySyncNotification() => HistorySyncNotification ?? ProtocolMsg?.HistorySyncNotification;

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, Conversation);
        if (ExtendedTextMessage != null)
            ProtoEncoder.WriteMessage(buf, 6, ExtendedTextMessage.ToByteArray());
        if (ProtocolMsg != null)
            ProtoEncoder.WriteMessage(buf, 12, ProtocolMsg.ToByteArray());
        if (DeviceSentMessage != null)
            ProtoEncoder.WriteMessage(buf, 31, DeviceSentMessage.ToByteArray());
        return [.. buf];
    }

    public static WAMessage ParseFrom(byte[] data)
    {
        var msg = new WAMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            try
            {
                var (field, wire) = r.ReadTag();
                switch (field)
                {
                    case 1:  msg.Conversation = r.ReadString(); break;
                    case 2:  msg.SenderKeyDist = SenderKeyDistributionMessage.ParseFrom(r.ReadBytes()); break;
                    case 6:  msg.ExtendedTextMessage = ExtendedTextMessage.ParseFrom(r.ReadBytes()); break;
                    case 12: msg.ProtocolMsg = ProtocolMessage.ParseFrom(r.ReadBytes()); break;
                    case 31: msg.DeviceSentMessage = DeviceSentMessage.ParseFrom(r.ReadBytes()); break;
                    case 35: msg.MessageContextInfo = MessageContextInfo.ParseFrom(r.ReadBytes()); break;
                    case 46: msg.HistorySyncNotification = HistorySyncNotification.ParseFrom(r.ReadBytes()); break;
                    default: r.Skip(wire); break;
                }
            }
            catch (Exception)
            {
                // Unknown/truncated field — return what we have
                break;
            }
        }
        return msg;
    }
}

/// <summary>Field 6 of Message. Contains text with optional link preview etc.</summary>
public sealed class ExtendedTextMessage
{
    public string Text { get; set; } = "";  // field 1

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, Text);
        return [.. buf];
    }

    public static ExtendedTextMessage ParseFrom(byte[] data)
    {
        var msg = new ExtendedTextMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            if (field == 1) msg.Text = r.ReadString();
            else r.Skip(wire);
        }
        return msg;
    }
}

/// <summary>Field 31 of Message. Wraps messages sent from this account on another device.</summary>
public sealed class DeviceSentMessage
{
    public string? DestinationJid { get; set; }  // field 1
    public WAMessage? Message { get; set; }       // field 2

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        if (!string.IsNullOrEmpty(DestinationJid))
            ProtoEncoder.WriteString(buf, 1, DestinationJid);
        if (Message != null)
            ProtoEncoder.WriteMessage(buf, 2, Message.ToByteArray());
        return [.. buf];
    }

    public static DeviceSentMessage ParseFrom(byte[] data)
    {
        var msg = new DeviceSentMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.DestinationJid = r.ReadString(); break;
                case 2: msg.Message = WAMessage.ParseFrom(r.ReadBytes()); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>Field 2 of Message. Distributes group encryption keys.</summary>
public sealed class SenderKeyDistributionMessage
{
    public string? GroupId { get; set; }          // field 1
    public byte[] AxolotlSenderKeyData { get; set; } = []; // field 2

    public static SenderKeyDistributionMessage ParseFrom(byte[] data)
    {
        var msg = new SenderKeyDistributionMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.GroupId = r.ReadString(); break;
                case 2: msg.AxolotlSenderKeyData = r.ReadBytes(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>Field 12 of Message. Protocol-level messages (message revocation, history sync, etc.)</summary>
public sealed class ProtocolMessage
{
    // field 2: type enum (NOT field 5 — 5 is ephemeralSettingTimestamp)
    // Enum values: 5=HISTORY_SYNC_NOTIFICATION, 16=PEER_DATA_OPERATION_REQUEST_MESSAGE
    public int Type { get; set; }
    public HistorySyncNotification? HistorySyncNotification { get; set; }         // field 6
    public PeerDataOperationRequestMessage? PeerDataOperationRequest { get; set; } // field 16

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        if (Type != 0) ProtoEncoder.WriteInt32(buf, 2, Type);  // field 2, not 5
        if (PeerDataOperationRequest != null)
            ProtoEncoder.WriteMessage(buf, 16, PeerDataOperationRequest.ToByteArray());
        return [.. buf];
    }

    public static ProtocolMessage ParseFrom(byte[] data)
    {
        var msg = new ProtocolMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 2: msg.Type = r.ReadInt32(); break;  // field 2 is the type
                case 6: msg.HistorySyncNotification = HistorySyncNotification.ParseFrom(r.ReadBytes()); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>
/// Nested sub-message inside HistorySyncNotification (field 11) in newer WhatsApp versions.
/// Contains CDN blob credentials when the history data is referenced externally.
/// </summary>
public sealed class ExternalBlobReference
{
    public byte[] MediaKey      { get; set; } = [];
    public byte[] FileSha256    { get; set; } = [];
    public byte[] FileEncSha256 { get; set; } = [];
    public string DirectPath    { get; set; } = "";
    public ulong  FileLength    { get; set; }

    public static ExternalBlobReference ParseFrom(byte[] data)
    {
        var msg = new ExternalBlobReference();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            try
            {
                var (field, wire) = r.ReadTag();
                switch (field)
                {
                    case 1: msg.MediaKey      = r.ReadBytes(); break;
                    case 2: msg.FileSha256    = r.ReadBytes(); break;
                    case 3: msg.FileEncSha256 = r.ReadBytes(); break;
                    case 4: msg.DirectPath    = r.ReadString(); break;
                    case 5: msg.FileLength    = r.ReadUInt64(); break;
                    default: r.Skip(wire); break;
                }
            }
            catch (Exception) { break; }
        }
        return msg;
    }
}

/// <summary>
/// Field 46 of Message. Sent by the phone to companion devices when history needs to be synced.
/// Contains CDN credentials to download an encrypted HistorySync protobuf blob.
/// Field numbers verified against Baileys WAProto.d.ts.
/// </summary>
public sealed class HistorySyncNotification
{
    public byte[] FileSha256    { get; set; } = [];  // field 1
    public ulong  FileLength    { get; set; }         // field 2
    public byte[] MediaKey      { get; set; } = [];  // field 3
    public byte[] FileEncSha256 { get; set; } = [];  // field 4
    public string DirectPath    { get; set; } = "";  // field 6  (CDN path)
    public int    SyncType      { get; set; }         // field 7  (enum: 0=INITIAL_BOOTSTRAP, 2=FULL, 3=RECENT, 6=ON_DEMAND)
    public uint   ChunkOrder    { get; set; }         // field 8
    public string? OriginalMessageId { get; set; }   // field 9
    public byte[]? InlineBlob        { get; set; }   // field 11: zlib-compressed HistorySync proto (newer WA format)

    public static HistorySyncNotification ParseFrom(byte[] data)
    {
        var msg = new HistorySyncNotification();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            try
            {
                var (field, wire) = r.ReadTag();
                switch (field)
                {
                    case 1: msg.FileSha256        = r.ReadBytes(); break;
                    case 2: msg.FileLength        = r.ReadUInt64(); break;
                    case 3: msg.MediaKey          = r.ReadBytes(); break;
                    case 4: msg.FileEncSha256     = r.ReadBytes(); break;
                    // field 5 = SyncType in some versions (varint)
                    case 5 when wire == 0: msg.SyncType = r.ReadInt32(); break;
                    // field 6 = DirectPath (string, wire 2) OR SyncType (varint, wire 0) in newer versions
                    case 6 when wire == 2: msg.DirectPath = r.ReadString(); break;
                    case 6 when wire == 0: msg.SyncType   = r.ReadInt32(); break;
                    // field 7 = SyncType (wire 0) OR DirectPath (wire 2) depending on version
                    case 7 when wire == 0: msg.SyncType   = r.ReadInt32(); break;
                    case 7 when wire == 2: msg.DirectPath = r.ReadString(); break;
                    case 8: msg.ChunkOrder        = r.ReadUInt32(); break;
                    case 9: msg.OriginalMessageId = r.ReadString(); break;
                    // field 11 = inline zlib-compressed HistorySync proto (newer WA format, no CDN)
                    case 11: msg.InlineBlob = r.ReadBytes(); break;
                    default: r.Skip(wire); break;
                }
            }
            catch (InvalidDataException)
            {
                // Truncated or unknown trailing field — return whatever we parsed so far
                break;
            }
        }
        return msg;
    }

    public string SyncTypeName => SyncType switch
    {
        0 => "INITIAL_BOOTSTRAP",
        1 => "INITIAL_STATUS_V3",
        2 => "FULL",
        3 => "RECENT",
        4 => "PUSH",
        5 => "NON_BLOCKING_DATA",
        6 => "ON_DEMAND",
        _ => $"UNKNOWN({SyncType})",
    };
}

/// <summary>
/// Top-level HistorySync protobuf. The encrypted blob downloaded via HistorySyncNotification
/// decodes to this message. Contains one or more conversations with their messages.
/// </summary>
public sealed class HistorySync
{
    public int SyncType { get; set; }                              // field 1
    public List<HistorySyncConversation> Conversations { get; set; } = new(); // field 2 (repeated)
    public List<HistorySyncPushName> PushNames { get; set; } = new(); // field 5 (repeated)

    public static HistorySync ParseFrom(byte[] data)
    {
        var msg = new HistorySync();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.SyncType = r.ReadInt32(); break;
                case 2: msg.Conversations.Add(HistorySyncConversation.ParseFrom(r.ReadBytes())); break;
                case 5: msg.PushNames.Add(HistorySyncPushName.ParseFrom(r.ReadBytes())); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>Push name mapping from HistorySync field 5.</summary>
public sealed class HistorySyncPushName
{
    public string Id       { get; set; } = "";  // field 1 - JID
    public string PushName { get; set; } = "";  // field 3

    public static HistorySyncPushName ParseFrom(byte[] data)
    {
        var msg = new HistorySyncPushName();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.Id = r.ReadString(); break;
                case 3: msg.PushName = r.ReadString(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>A single chat thread inside HistorySync.</summary>
public sealed class HistorySyncConversation
{
    public string Id { get; set; } = "";                          // field 1 - chat JID
    public List<WebMessageInfo> Messages { get; set; } = new();  // field 2 (repeated)
    public string Name { get; set; } = "";                        // field 11

    public static HistorySyncConversation ParseFrom(byte[] data)
    {
        var msg = new HistorySyncConversation();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1:  msg.Id = r.ReadString(); break;
                case 2:
                    var syncMsg = HistorySyncMsg.ParseFrom(r.ReadBytes());
                    if (syncMsg.Message != null) msg.Messages.Add(syncMsg.Message);
                    break;
                case 11: msg.Name = r.ReadString(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>Wrapper around WebMessageInfo inside HistorySyncConversation (field 2).</summary>
public sealed class HistorySyncMsg
{
    public WebMessageInfo? Message { get; set; }  // field 1

    public static HistorySyncMsg ParseFrom(byte[] data)
    {
        var msg = new HistorySyncMsg();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.Message = WebMessageInfo.ParseFrom(r.ReadBytes()); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>A single message inside a HistorySyncConversation.</summary>
public sealed class WebMessageInfo
{
    public WebMessageKey? Key { get; set; }       // field 1
    public WAMessage?     Message { get; set; }   // field 2
    public ulong          MessageTimestamp { get; set; } // field 3
    public int            Status { get; set; }    // field 4 (delivery status)
    public string         PushName { get; set; } = ""; // field 5

    public static WebMessageInfo ParseFrom(byte[] data)
    {
        var msg = new WebMessageInfo();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.Key               = WebMessageKey.ParseFrom(r.ReadBytes()); break;
                case 2: msg.Message           = WAMessage.ParseFrom(r.ReadBytes()); break;
                case 3: msg.MessageTimestamp  = r.ReadUInt64(); break;
                case 4: msg.Status            = r.ReadInt32(); break;
                case 5: msg.PushName          = r.ReadString(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>Message key inside WebMessageInfo.</summary>
public sealed class WebMessageKey
{
    public string RemoteJid   { get; set; } = "";  // field 1
    public bool   FromMe      { get; set; }         // field 2
    public string Id          { get; set; } = "";  // field 3
    public string Participant { get; set; } = "";  // field 4 (group sender)

    public static WebMessageKey ParseFrom(byte[] data)
    {
        var msg = new WebMessageKey();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.RemoteJid   = r.ReadString(); break;
                case 2: msg.FromMe      = r.ReadBool(); break;
                case 3: msg.Id          = r.ReadString(); break;
                case 4: msg.Participant = r.ReadString(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>
/// WAMessage field 149. Sent from companion device to the primary phone to request
/// on-demand history sync. The phone responds with HistorySyncNotification(SyncType=ON_DEMAND).
/// PeerDataOperationRequestType: 3=HISTORY_SYNC_ON_DEMAND, 6=FULL_HISTORY_SYNC_ON_DEMAND
/// </summary>
public sealed class PeerDataOperationRequestMessage
{
    public int RequestType { get; set; }                          // field 1: PeerDataOperationRequestType enum
    public HistorySyncOnDemandRequest? HistorySyncRequest { get; set; } // field 4 (single, not repeated)

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        if (RequestType != 0) ProtoEncoder.WriteInt32(buf, 1, RequestType);
        if (HistorySyncRequest != null)
            ProtoEncoder.WriteMessage(buf, 4, HistorySyncRequest.ToByteArray());
        return [.. buf];
    }
}

/// <summary>Embedded in PeerDataOperationRequestMessage field 3.</summary>
public sealed class HistorySyncOnDemandRequest
{
    public string  ChatJid               { get; set; } = "";  // field 1: target chat JID
    public string? OldestMsgId          { get; set; }         // field 2: start from this message
    public bool    OldestMsgFromMe      { get; set; }         // field 3: was oldest msg sent by us?
    public int     OnDemandMsgCount     { get; set; } = 50;   // field 4: how many messages to request
    public long    OldestMsgTimestampMs { get; set; }         // field 5: timestamp of oldest msg (ms)

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, ChatJid);
        if (!string.IsNullOrEmpty(OldestMsgId))
            ProtoEncoder.WriteString(buf, 2, OldestMsgId);
        if (OldestMsgFromMe)
            ProtoEncoder.WriteBool(buf, 3, true);
        if (OnDemandMsgCount != 0)
            ProtoEncoder.WriteInt32(buf, 4, OnDemandMsgCount);
        if (OldestMsgTimestampMs != 0)
        {
            ProtoEncoder.WriteTag(buf, 5, 0);  // wire type 0 = varint (int64)
            ProtoEncoder.WriteVarint(buf, (ulong)OldestMsgTimestampMs);
        }
        return [.. buf];
    }
}

/// <summary>Field 35 of Message. Contains device list metadata.</summary>
public sealed class MessageContextInfo
{
    public byte[] DeviceListMetadata { get; set; } = [];  // field 1

    public static MessageContextInfo ParseFrom(byte[] data)
    {
        var msg = new MessageContextInfo();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.DeviceListMetadata = r.ReadBytes(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}
