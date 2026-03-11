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

/// <summary>WhatsApp message content proto.</summary>
public sealed class WAMessage
{
    public string? Conversation { get; set; }
    public ExtendedTextMessage? ExtendedTextMessage { get; set; }

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, Conversation);
        if (ExtendedTextMessage != null)
            ProtoEncoder.WriteMessage(buf, 2, ExtendedTextMessage.ToByteArray());
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
                case 1:
                    msg.Conversation = r.ReadString();
                    break;
                case 2:
                    msg.ExtendedTextMessage = ExtendedTextMessage.ParseFrom(r.ReadBytes());
                    break;
                default:
                    r.Skip(wire);
                    break;
            }
        }
        return msg;
    }
}

public sealed class ExtendedTextMessage
{
    public string Text { get; set; } = "";

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
