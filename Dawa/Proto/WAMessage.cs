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
    public ImageMessage? ImageMessage { get; set; }                  // field 3
    public AudioMessage? AudioMessage { get; set; }                  // field 4
    public ExtendedTextMessage? ExtendedTextMessage { get; set; }    // field 6
    public ProtocolMessage? ProtocolMsg { get; set; }                // field 12
    public DocumentMessage? DocumentMessage { get; set; }               // field 15
    public DeviceSentMessage? DeviceSentMessage { get; set; }           // field 31
    public MessageContextInfo? MessageContextInfo { get; set; }         // field 35
    public HistorySyncNotification? HistorySyncNotification { get; set; } // field 46
    public ReactionMessage? ReactionMessage { get; set; }               // field 85

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

    /// <summary>True if this is a HistorySync notification message (field 46 present).</summary>
    public bool IsHistorySync => HistorySyncNotification != null;

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, Conversation);
        if (ImageMessage != null)    ProtoEncoder.WriteMessage(buf, 3,  ImageMessage.ToByteArray());
        if (AudioMessage != null)    ProtoEncoder.WriteMessage(buf, 4,  AudioMessage.ToByteArray());
        if (ExtendedTextMessage != null)
            ProtoEncoder.WriteMessage(buf, 6, ExtendedTextMessage.ToByteArray());
        if (DocumentMessage != null) ProtoEncoder.WriteMessage(buf, 15, DocumentMessage.ToByteArray());
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
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1:  msg.Conversation = r.ReadString(); break;
                case 2:  msg.SenderKeyDist = SenderKeyDistributionMessage.ParseFrom(r.ReadBytes()); break;
                case 3:  msg.ImageMessage = ImageMessage.ParseFrom(r.ReadBytes()); break;
                case 4:  msg.AudioMessage = AudioMessage.ParseFrom(r.ReadBytes()); break;
                case 6:  msg.ExtendedTextMessage = ExtendedTextMessage.ParseFrom(r.ReadBytes()); break;
                case 12: msg.ProtocolMsg = ProtocolMessage.ParseFrom(r.ReadBytes()); break;
                case 15: msg.DocumentMessage = DocumentMessage.ParseFrom(r.ReadBytes()); break;
                case 31: msg.DeviceSentMessage = DeviceSentMessage.ParseFrom(r.ReadBytes()); break;
                case 35: msg.MessageContextInfo = MessageContextInfo.ParseFrom(r.ReadBytes()); break;
                case 46: msg.HistorySyncNotification = HistorySyncNotification.ParseFrom(r.ReadBytes()); break;
                case 85: msg.ReactionMessage = ReactionMessage.ParseFrom(r.ReadBytes()); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }

    public byte[] ToByteArrayWithReaction()
    {
        var buf = new List<byte>();
        if (ReactionMessage != null)
            ProtoEncoder.WriteMessage(buf, 85, ReactionMessage.ToByteArray());
        return [.. buf];
    }

    public byte[] ToByteArrayWithMedia()
    {
        var buf = new List<byte>();
        if (ImageMessage != null)    ProtoEncoder.WriteMessage(buf, 3,  ImageMessage.ToByteArray());
        if (AudioMessage != null)    ProtoEncoder.WriteMessage(buf, 4,  AudioMessage.ToByteArray());
        if (DocumentMessage != null) ProtoEncoder.WriteMessage(buf, 15, DocumentMessage.ToByteArray());
        return [.. buf];
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

/// <summary>Field 3 of Message. An image with optional caption.</summary>
public sealed class ImageMessage
{
    public string Url { get; set; } = "";           // field 1
    public string MimeType { get; set; } = "";      // field 2
    public byte[] FileSha256 { get; set; } = [];    // field 3
    public ulong FileLength { get; set; }            // field 4
    public byte[] MediaKey { get; set; } = [];      // field 7
    public byte[] FileEncSha256 { get; set; } = []; // field 8
    public string DirectPath { get; set; } = "";    // field 10
    public string Caption { get; set; } = "";       // field 16
    public long MediaKeyTimestamp { get; set; }      // field 25

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, Url);
        ProtoEncoder.WriteString(buf, 2, MimeType);
        if (FileSha256.Length > 0)    ProtoEncoder.WriteBytes(buf, 3, FileSha256);
        if (FileLength > 0)           ProtoEncoder.WriteUInt64(buf, 4, FileLength);
        if (MediaKey.Length > 0)      ProtoEncoder.WriteBytes(buf, 7, MediaKey);
        if (FileEncSha256.Length > 0) ProtoEncoder.WriteBytes(buf, 8, FileEncSha256);
        ProtoEncoder.WriteString(buf, 10, DirectPath);
        if (!string.IsNullOrEmpty(Caption)) ProtoEncoder.WriteString(buf, 16, Caption);
        if (MediaKeyTimestamp != 0)   ProtoEncoder.WriteUInt64(buf, 25, (ulong)MediaKeyTimestamp);
        return [.. buf];
    }

    public static ImageMessage ParseFrom(byte[] data)
    {
        var msg = new ImageMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1:  msg.Url = r.ReadString(); break;
                case 2:  msg.MimeType = r.ReadString(); break;
                case 3:  msg.FileSha256 = r.ReadBytes(); break;
                case 4:  msg.FileLength = r.ReadUInt64(); break;
                case 7:  msg.MediaKey = r.ReadBytes(); break;
                case 8:  msg.FileEncSha256 = r.ReadBytes(); break;
                case 10: msg.DirectPath = r.ReadString(); break;
                case 16: msg.Caption = r.ReadString(); break;
                case 25: msg.MediaKeyTimestamp = (long)r.ReadUInt64(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>Field 4 of Message. An audio clip or voice note (ptt=true).</summary>
public sealed class AudioMessage
{
    public string Url { get; set; } = "";           // field 1
    public string MimeType { get; set; } = "";      // field 2
    public byte[] FileSha256 { get; set; } = [];    // field 3
    public ulong FileLength { get; set; }            // field 4
    public uint Seconds { get; set; }               // field 5 (duration)
    public bool Ptt { get; set; }                   // field 6 (push-to-talk = voice note)
    public byte[] MediaKey { get; set; } = [];      // field 7
    public byte[] FileEncSha256 { get; set; } = []; // field 8
    public string DirectPath { get; set; } = "";    // field 9
    public long MediaKeyTimestamp { get; set; }      // field 12

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, Url);
        ProtoEncoder.WriteString(buf, 2, MimeType);
        if (FileSha256.Length > 0)    ProtoEncoder.WriteBytes(buf, 3, FileSha256);
        if (FileLength > 0)           ProtoEncoder.WriteUInt64(buf, 4, FileLength);
        if (Seconds > 0)              ProtoEncoder.WriteUInt32(buf, 5, Seconds);
        if (Ptt)                      ProtoEncoder.WriteBool(buf, 6, Ptt);
        if (MediaKey.Length > 0)      ProtoEncoder.WriteBytes(buf, 7, MediaKey);
        if (FileEncSha256.Length > 0) ProtoEncoder.WriteBytes(buf, 8, FileEncSha256);
        ProtoEncoder.WriteString(buf, 9, DirectPath);
        if (MediaKeyTimestamp != 0)   ProtoEncoder.WriteUInt64(buf, 12, (ulong)MediaKeyTimestamp);
        return [.. buf];
    }

    public static AudioMessage ParseFrom(byte[] data)
    {
        var msg = new AudioMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1:  msg.Url = r.ReadString(); break;
                case 2:  msg.MimeType = r.ReadString(); break;
                case 3:  msg.FileSha256 = r.ReadBytes(); break;
                case 4:  msg.FileLength = r.ReadUInt64(); break;
                case 5:  msg.Seconds = r.ReadUInt32(); break;
                case 6:  msg.Ptt = r.ReadBool(); break;
                case 7:  msg.MediaKey = r.ReadBytes(); break;
                case 8:  msg.FileEncSha256 = r.ReadBytes(); break;
                case 9:  msg.DirectPath = r.ReadString(); break;
                case 12: msg.MediaKeyTimestamp = (long)r.ReadUInt64(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }
}

/// <summary>Field 15 of Message. A document/file attachment.</summary>
public sealed class DocumentMessage
{
    public string Url { get; set; } = "";           // field 1
    public string MimeType { get; set; } = "";      // field 2
    public string Title { get; set; } = "";         // field 3
    public byte[] FileSha256 { get; set; } = [];    // field 4
    public ulong FileLength { get; set; }            // field 5
    public byte[] MediaKey { get; set; } = [];      // field 7
    public string FileName { get; set; } = "";      // field 8
    public byte[] FileEncSha256 { get; set; } = []; // field 9
    public string DirectPath { get; set; } = "";    // field 10
    public long MediaKeyTimestamp { get; set; }      // field 16

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, Url);
        ProtoEncoder.WriteString(buf, 2, MimeType);
        ProtoEncoder.WriteString(buf, 3, Title);
        if (FileSha256.Length > 0)    ProtoEncoder.WriteBytes(buf, 4, FileSha256);
        if (FileLength > 0)           ProtoEncoder.WriteUInt64(buf, 5, FileLength);
        if (MediaKey.Length > 0)      ProtoEncoder.WriteBytes(buf, 7, MediaKey);
        ProtoEncoder.WriteString(buf, 8, FileName);
        if (FileEncSha256.Length > 0) ProtoEncoder.WriteBytes(buf, 9, FileEncSha256);
        ProtoEncoder.WriteString(buf, 10, DirectPath);
        if (MediaKeyTimestamp != 0)   ProtoEncoder.WriteUInt64(buf, 16, (ulong)MediaKeyTimestamp);
        return [.. buf];
    }

    public static DocumentMessage ParseFrom(byte[] data)
    {
        var msg = new DocumentMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1:  msg.Url = r.ReadString(); break;
                case 2:  msg.MimeType = r.ReadString(); break;
                case 3:  msg.Title = r.ReadString(); break;
                case 4:  msg.FileSha256 = r.ReadBytes(); break;
                case 5:  msg.FileLength = r.ReadUInt64(); break;
                case 7:  msg.MediaKey = r.ReadBytes(); break;
                case 8:  msg.FileName = r.ReadString(); break;
                case 9:  msg.FileEncSha256 = r.ReadBytes(); break;
                case 10: msg.DirectPath = r.ReadString(); break;
                case 16: msg.MediaKeyTimestamp = (long)r.ReadUInt64(); break;
                default: r.Skip(wire); break;
            }
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
    public int Type { get; set; }  // field 5: type enum

    public static ProtocolMessage ParseFrom(byte[] data)
    {
        var msg = new ProtocolMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 5: msg.Type = r.ReadInt32(); break;
                default: r.Skip(wire); break;
            }
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

    public static HistorySyncNotification ParseFrom(byte[] data)
    {
        var msg = new HistorySyncNotification();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.FileSha256    = r.ReadBytes(); break;
                case 2: msg.FileLength    = r.ReadUInt64(); break;
                case 3: msg.MediaKey      = r.ReadBytes(); break;
                case 4: msg.FileEncSha256 = r.ReadBytes(); break;
                case 6: msg.DirectPath   = r.ReadString(); break;
                case 7: msg.SyncType     = r.ReadInt32(); break;
                case 8: msg.ChunkOrder   = r.ReadUInt32(); break;
                case 9: msg.OriginalMessageId = r.ReadString(); break;
                default: r.Skip(wire); break;
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
                case 2:  msg.Messages.Add(WebMessageInfo.ParseFrom(r.ReadBytes())); break;
                case 11: msg.Name = r.ReadString(); break;
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

/// <summary>Field 85 of Message. Emoji reaction to another message.</summary>
public sealed class ReactionMessage
{
    /// <summary>The message being reacted to.</summary>
    public MessageKey? Key { get; set; }                  // field 1
    /// <summary>The reaction emoji (e.g. "👍") or "" to remove reaction.</summary>
    public string Text { get; set; } = "";                // field 2
    /// <summary>Sender timestamp in milliseconds.</summary>
    public long SenderTimestampMs { get; set; }           // field 4

    public static ReactionMessage ParseFrom(byte[] data)
    {
        var msg = new ReactionMessage();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.Key = MessageKey.ParseFrom(r.ReadBytes()); break;
                case 2: msg.Text = r.ReadString(); break;
                case 4: msg.SenderTimestampMs = (long)r.ReadUInt64(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        if (Key != null) ProtoEncoder.WriteMessage(buf, 1, Key.ToByteArray());
        if (!string.IsNullOrEmpty(Text)) ProtoEncoder.WriteString(buf, 2, Text);
        if (SenderTimestampMs != 0) ProtoEncoder.WriteUInt64(buf, 4, (ulong)SenderTimestampMs);
        return [.. buf];
    }
}

/// <summary>Key that identifies a specific WhatsApp message.</summary>
public sealed class MessageKey
{
    public string RemoteJid { get; set; } = "";   // field 1
    public bool   FromMe    { get; set; }          // field 2
    public string Id        { get; set; } = "";    // field 3

    public static MessageKey ParseFrom(byte[] data)
    {
        var msg = new MessageKey();
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (field, wire) = r.ReadTag();
            switch (field)
            {
                case 1: msg.RemoteJid = r.ReadString(); break;
                case 2: msg.FromMe    = r.ReadBool();   break;
                case 3: msg.Id        = r.ReadString(); break;
                default: r.Skip(wire); break;
            }
        }
        return msg;
    }

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        if (!string.IsNullOrEmpty(RemoteJid)) ProtoEncoder.WriteString(buf, 1, RemoteJid);
        if (FromMe) ProtoEncoder.WriteBool(buf, 2, FromMe);
        if (!string.IsNullOrEmpty(Id)) ProtoEncoder.WriteString(buf, 3, Id);
        return [.. buf];
    }
}
