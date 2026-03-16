namespace Dawa.Proto;

// ─────────────────────────────────────────────────────────────────────────────
// History-sync proto decoders.
// Field numbers verified against Baileys WAProto/index.js encode methods.
// Uses the existing ProtoReader from ProtoEncoder.cs (byte[] based).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Embedded in ProtocolMessage field 6 (HISTORY_SYNC_NOTIFICATION).
/// Points to the encrypted, compressed HistorySync blob on the CDN.
/// </summary>
public sealed class HistorySyncNotification
{
    public byte[] FileSha256    { get; set; } = [];  // field 1
    public ulong  FileLength    { get; set; }         // field 2
    public byte[] MediaKey      { get; set; } = [];  // field 3
    public byte[] FileEncSha256 { get; set; } = [];  // field 4
    public string DirectPath    { get; set; } = "";  // field 5
    public int    SyncType      { get; set; }         // field 6
    public int    Progress      { get; set; }         // field 9
    public byte[] InlineBlob    { get; set; } = [];  // field 10 (newer WA: zlib blob sent inline)

    public const int INITIAL_BOOTSTRAP = 0;
    public const int RECENT            = 2;
    public const int FULL              = 3;
    public const int PUSH_NAME         = 4;
    public const int ON_DEMAND         = 5;
    public const int NON_BLOCKING_DATA = 6;

    public static HistorySyncNotification Decode(byte[] data)
    {
        var r   = ProtoEncoder.CreateReader(data);
        var obj = new HistorySyncNotification();
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1:  obj.FileSha256    = r.ReadBytes();       break;
                case 2:  obj.FileLength    = r.ReadVarint();      break;
                case 3:  obj.MediaKey      = r.ReadBytes();       break;
                case 4:  obj.FileEncSha256 = r.ReadBytes();       break;
                case 5:  obj.DirectPath    = r.ReadString();      break;
                case 6:  obj.SyncType      = r.ReadInt32();       break;
                case 9:  obj.Progress      = r.ReadInt32();       break;
                case 10: obj.InlineBlob    = r.ReadBytes();       break;
                default: r.Skip(wt);                              break;
            }
        }
        return obj;
    }
}

/// <summary>Top-level proto decoded from the decrypted+decompressed history blob.</summary>
public sealed class HistorySync
{
    public int                 SyncType      { get; set; }
    public List<Conversation>  Conversations { get; set; } = [];
    public List<Pushname>      PushNames     { get; set; } = [];

    public static HistorySync Decode(byte[] data)
    {
        var r   = ProtoEncoder.CreateReader(data);
        var obj = new HistorySync();
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1: obj.SyncType = r.ReadInt32();                                          break;
                case 2: obj.Conversations.Add(Conversation.Decode(r.ReadBytes()));             break;
                case 7: obj.PushNames.Add(Pushname.Decode(r.ReadBytes()));                    break;
                default: r.Skip(wt);                                                            break;
            }
        }
        return obj;
    }
}

public sealed class Conversation
{
    public string               Id       { get; set; } = "";
    public List<HistorySyncMsg> Messages { get; set; } = [];
    public string               Name     { get; set; } = "";
    public bool                 Archived { get; set; }

    public static Conversation Decode(byte[] data)
    {
        var r   = ProtoEncoder.CreateReader(data);
        var obj = new Conversation();
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1:  obj.Id       = r.ReadString();                                 break;
                case 2:  obj.Messages.Add(HistorySyncMsg.Decode(r.ReadBytes()));       break;
                case 13: obj.Name     = r.ReadString();                                 break;
                case 16: obj.Archived = r.ReadBool();                                   break;
                default: r.Skip(wt);                                                     break;
            }
        }
        return obj;
    }
}

public sealed class HistorySyncMsg
{
    public HistoryWebMessageInfo? Message { get; set; }

    public static HistorySyncMsg Decode(byte[] data)
    {
        var r   = ProtoEncoder.CreateReader(data);
        var obj = new HistorySyncMsg();
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1: obj.Message = HistoryWebMessageInfo.Decode(r.ReadBytes()); break;
                default: r.Skip(wt); break;
            }
        }
        return obj;
    }
}

/// <summary>Minimal decode of WebMessageInfo — only fields needed for history display.</summary>
public sealed class HistoryWebMessageInfo
{
    public HistoryMessageKey? Key         { get; set; }  // field 1
    public HistoryWaMessage?  MessageBody { get; set; }  // field 2
    public ulong              Timestamp   { get; set; }  // field 3
    public string             Participant { get; set; } = ""; // field 5 (group sender)
    public string             PushName    { get; set; } = ""; // field 19

    public static HistoryWebMessageInfo Decode(byte[] data)
    {
        var r   = ProtoEncoder.CreateReader(data);
        var obj = new HistoryWebMessageInfo();
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1:  obj.Key         = HistoryMessageKey.Decode(r.ReadBytes()); break;
                case 2:  obj.MessageBody = HistoryWaMessage.Decode(r.ReadBytes()); break;
                case 3:  obj.Timestamp   = r.ReadVarint();                          break;
                case 5:  obj.Participant = r.ReadString();                          break;
                case 19: obj.PushName    = r.ReadString();                          break;
                default: r.Skip(wt);                                                 break;
            }
        }
        return obj;
    }
}

public sealed class HistoryMessageKey
{
    public string RemoteJid   { get; set; } = "";
    public bool   FromMe      { get; set; }
    public string Id          { get; set; } = "";
    public string Participant { get; set; } = ""; // field 4 (group sender)

    public static HistoryMessageKey Decode(byte[] data)
    {
        var r   = ProtoEncoder.CreateReader(data);
        var obj = new HistoryMessageKey();
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1: obj.RemoteJid   = r.ReadString(); break;
                case 2: obj.FromMe      = r.ReadBool();   break;
                case 3: obj.Id          = r.ReadString(); break;
                case 4: obj.Participant = r.ReadString(); break;
                default: r.Skip(wt);                       break;
            }
        }
        return obj;
    }
}

/// <summary>Minimal Message decode — plain text + captions.</summary>
public sealed class HistoryWaMessage
{
    public string Conversation { get; set; } = ""; // field 1 — plain text
    public string ExtendedText { get; set; } = ""; // field 6 → ExtendedTextMessage.text (field 1)
    public string ImageCaption { get; set; } = ""; // field 4 → ImageMessage.caption (field 7)
    public string VideoCaption { get; set; } = ""; // field 13→ VideoMessage.caption (field 7)

    public string EffectiveText =>
        Conversation.Length > 0 ? Conversation :
        ExtendedText.Length  > 0 ? ExtendedText :
        ImageCaption.Length  > 0 ? $"[image] {ImageCaption}".Trim() :
        VideoCaption.Length  > 0 ? $"[video] {VideoCaption}".Trim() :
        "";

    public static HistoryWaMessage Decode(byte[] data)
    {
        var r   = ProtoEncoder.CreateReader(data);
        var obj = new HistoryWaMessage();
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1:  obj.Conversation = r.ReadString();                        break;
                case 4:  obj.ImageCaption = ReadCaptionField7(r.ReadBytes());     break; // ImageMessage
                case 6:  obj.ExtendedText = ReadTextField1(r.ReadBytes());        break; // ExtendedTextMessage
                case 13: obj.VideoCaption = ReadCaptionField7(r.ReadBytes());     break; // VideoMessage
                default: r.Skip(wt);                                               break;
            }
        }
        return obj;
    }

    private static string ReadTextField1(byte[] data)
    {
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (f, wt) = r.ReadTag();
            if (f == 1) return r.ReadString();
            r.Skip(wt);
        }
        return "";
    }

    private static string ReadCaptionField7(byte[] data)
    {
        var r = ProtoEncoder.CreateReader(data);
        while (r.HasMore)
        {
            var (f, wt) = r.ReadTag();
            if (f == 7) return r.ReadString();
            r.Skip(wt);
        }
        return "";
    }
}

public sealed class Pushname
{
    public string Id       { get; set; } = "";
    public string PushName { get; set; } = "";

    public static Pushname Decode(byte[] data)
    {
        var r   = ProtoEncoder.CreateReader(data);
        var obj = new Pushname();
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1: obj.Id       = r.ReadString(); break;
                case 2: obj.PushName = r.ReadString(); break;
                default: r.Skip(wt);                   break;
            }
        }
        return obj;
    }
}
