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
    public string Os             { get; set; } = "Mac OS";
    // PlatformType: CHROME=1, SAFARI=5, CATALINA=12
    public int PlatformType      { get; set; } = 1; // CHROME
    public DevicePropsVersion? Version { get; set; }
    public bool RequireFullSync  { get; set; } = true;
    public HistorySyncConfig? HistorySyncConfig { get; set; }

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteString(buf, 1, Os);
        if (Version != null) ProtoEncoder.WriteMessage(buf, 2, Version.ToByteArray());
        ProtoEncoder.WriteInt32(buf, 3, PlatformType);
        ProtoEncoder.WriteBoolAlways(buf, 4, RequireFullSync);
        if (HistorySyncConfig != null) ProtoEncoder.WriteMessage(buf, 5, HistorySyncConfig.ToByteArray());
        return [.. buf];
    }
}

public sealed class DevicePropsVersion
{
    public uint Primary   { get; set; } = 10;
    public uint Secondary { get; set; } = 15;
    public uint Tertiary  { get; set; } = 7;

    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        ProtoEncoder.WriteUInt32(buf, 1, Primary);
        ProtoEncoder.WriteUInt32(buf, 2, Secondary);
        ProtoEncoder.WriteUInt32(buf, 3, Tertiary);
        return [.. buf];
    }
}

/// <summary>HistorySyncConfig for DeviceProps — matches Baileys DEFAULT_CONNECTION_CONFIG.syncFullHistory=true defaults.</summary>
public sealed class HistorySyncConfig
{
    public byte[] ToByteArray()
    {
        var buf = new List<byte>();
        // storageQuotaMb = 10240 (field 3)
        ProtoEncoder.WriteUInt32(buf, 3, 10240);
        // inlineInitialPayloadInE2EeMsg = true (field 4)
        ProtoEncoder.WriteBoolAlways(buf, 4, true);
        // recentSyncDaysLimit: undefined → skip (field 5)
        // supportCallLogHistory = false (field 6) — explicitly set in Baileys
        ProtoEncoder.WriteBoolAlways(buf, 6, false);
        // supportBotUserAgentChatHistory = true (field 7)
        ProtoEncoder.WriteBoolAlways(buf, 7, true);
        // supportCagReactionsAndPolls = true (field 8)
        ProtoEncoder.WriteBoolAlways(buf, 8, true);
        // supportBizHostedMsg = true (field 9)
        ProtoEncoder.WriteBoolAlways(buf, 9, true);
        // supportRecentSyncChunkMessageCountTuning = true (field 10)
        ProtoEncoder.WriteBoolAlways(buf, 10, true);
        // supportHostedGroupMsg = true (field 11)
        ProtoEncoder.WriteBoolAlways(buf, 11, true);
        // supportFbidBotChatHistory = true (field 12)
        ProtoEncoder.WriteBoolAlways(buf, 12, true);
        // supportAddOnHistorySyncMigration: undefined → skip (field 13)
        // supportMessageAssociation = true (field 14)
        ProtoEncoder.WriteBoolAlways(buf, 14, true);
        // supportGroupHistory = false (field 15) — explicitly set in Baileys
        ProtoEncoder.WriteBoolAlways(buf, 15, false);
        // onDemandReady: undefined → skip (field 16)
        // supportGuestChat: undefined → skip (field 17)
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
