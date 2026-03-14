namespace Dawa.Proto;

// SyncdSnapshot (returned in <snapshot> node of app state sync IQ result)
// field 1 = SyncdVersion (message, skip)
// field 2 = SyncdRecord[] (repeated message)
// field 3 = mac (bytes, skip)
// field 4 = keyId (SyncdIndex message → field 1 = blob bytes)
public sealed class SyncdSnapshot
{
    public List<SyncdRecord> Records { get; } = [];
    public byte[] KeyId { get; set; } = [];

    public static SyncdSnapshot ParseFrom(byte[] data)
    {
        var msg = new SyncdSnapshot();
        var r = new ProtoReader(data);
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 2: msg.Records.Add(SyncdRecord.ParseFrom(r.ReadBytes())); break;
                case 4: msg.KeyId = ParseIndex(r.ReadBytes()); break;
                default: r.Skip(wt); break;
            }
        }
        return msg;
    }

    private static byte[] ParseIndex(byte[] data)
    {
        var r = new ProtoReader(data);
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            if (field == 1) return r.ReadBytes();
            r.Skip(wt);
        }
        return [];
    }
}

// SyncdRecord
// field 1 = index (SyncdIndex → blob)
// field 2 = value (SyncdValue → blob)  -- this is the encrypted payload
// field 3 = keyId (SyncdIndex → blob)
public sealed class SyncdRecord
{
    public byte[] IndexBlob { get; set; } = [];
    public byte[] ValueBlob { get; set; } = [];  // IV(16) + ciphertext
    public byte[] KeyId { get; set; } = [];

    public static SyncdRecord ParseFrom(byte[] data)
    {
        var msg = new SyncdRecord();
        var r = new ProtoReader(data);
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1: msg.IndexBlob = ParseBlob(r.ReadBytes()); break;
                case 2: msg.ValueBlob = ParseBlob(r.ReadBytes()); break;
                case 3: msg.KeyId     = ParseBlob(r.ReadBytes()); break;
                default: r.Skip(wt); break;
            }
        }
        return msg;
    }

    private static byte[] ParseBlob(byte[] data)
    {
        var r = new ProtoReader(data);
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            if (field == 1) return r.ReadBytes();
            r.Skip(wt);
        }
        return [];
    }
}

// SyncActionData (decrypted payload of each SyncdRecord.ValueBlob)
// field 1 = index (bytes → UTF-8 JSON, e.g. ["contact","31612345678@s.whatsapp.net"])
// field 2 = value (SyncActionValue)
public sealed class SyncActionData
{
    public string[] Index { get; set; } = [];   // parsed from JSON bytes
    public SyncActionValue? Value { get; set; }

    public static SyncActionData ParseFrom(byte[] data)
    {
        var msg = new SyncActionData();
        var r = new ProtoReader(data);
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1:
                    var indexJson = System.Text.Encoding.UTF8.GetString(r.ReadBytes());
                    // JSON array like ["contact","31612345678@s.whatsapp.net"]
                    try { msg.Index = System.Text.Json.JsonSerializer.Deserialize<string[]>(indexJson) ?? []; }
                    catch { msg.Index = []; }
                    break;
                case 2: msg.Value = SyncActionValue.ParseFrom(r.ReadBytes()); break;
                default: r.Skip(wt); break;
            }
        }
        return msg;
    }
}

// SyncActionValue
// field 1 = timestamp (int64, skip)
// field 3 = contactAction (SyncActionContactAction)
// (many other field types we don't need — skip all)
public sealed class SyncActionValue
{
    public SyncActionContactAction? ContactAction { get; set; }

    public static SyncActionValue ParseFrom(byte[] data)
    {
        var msg = new SyncActionValue();
        var r = new ProtoReader(data);
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            if (field == 3) msg.ContactAction = SyncActionContactAction.ParseFrom(r.ReadBytes());
            else r.Skip(wt);
        }
        return msg;
    }
}

// SyncActionContactAction
// field 1 = fullName (string)
// field 2 = firstName (string)
public sealed class SyncActionContactAction
{
    public string FullName { get; set; } = "";
    public string FirstName { get; set; } = "";

    public static SyncActionContactAction ParseFrom(byte[] data)
    {
        var msg = new SyncActionContactAction();
        var r = new ProtoReader(data);
        while (r.HasMore)
        {
            var (field, wt) = r.ReadTag();
            switch (field)
            {
                case 1: msg.FullName  = r.ReadString(); break;
                case 2: msg.FirstName = r.ReadString(); break;
                default: r.Skip(wt); break;
            }
        }
        return msg;
    }
}
