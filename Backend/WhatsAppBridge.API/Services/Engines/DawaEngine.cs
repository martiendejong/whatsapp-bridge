using Dawa;
using Dawa.Messages;
using Dawa.Noise;

namespace WhatsAppBridge.API.Services.Engines;

/// <summary>
/// The original in-process engine: a thin adapter over Dawa's <see cref="WhatsAppClient"/>.
/// </summary>
public sealed class DawaEngine : IWhatsAppEngine
{
    private readonly WhatsAppClient _client;

    public DawaEngine(string sessionDirectory, ILoggerFactory loggerFactory)
    {
        _client = WhatsAppClient.Create(sessionDirectory, loggerFactory);
    }

    public string EngineName => "dawa";
    public bool IsConnected => _client.IsConnected;
    public string? MyJid => _client.MyJid;
    public bool HasSavedSession => _client.HasSavedSession;

    public event EventHandler<string>? QRCodeReceived
    { add => _client.QRCodeReceived += value; remove => _client.QRCodeReceived -= value; }
    public event EventHandler? Connected
    { add => _client.Connected += value; remove => _client.Connected -= value; }
    public event EventHandler? Disconnected
    { add => _client.Disconnected += value; remove => _client.Disconnected -= value; }
    public event EventHandler<IncomingMessage>? MessageReceived
    { add => _client.MessageReceived += value; remove => _client.MessageReceived -= value; }
    public event EventHandler<IncomingMessage>? HistoryMessageReceived
    { add => _client.HistoryMessageReceived += value; remove => _client.HistoryMessageReceived -= value; }
    public event EventHandler<int>? HistorySyncCompleted
    { add => _client.HistorySyncCompleted += value; remove => _client.HistorySyncCompleted -= value; }
    public event EventHandler<(string MessageId, string Jid, MessageStatus Status)>? MessageStatusUpdated
    { add => _client.MessageStatusUpdated += value; remove => _client.MessageStatusUpdated -= value; }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => _client.ConnectAsync(cancellationToken);

    public Task<(string MessageId, string Jid)> SendMessageAsync(string to, string text, CancellationToken ct = default)
        => _client.SendMessageAsync(to, text, ct);

    public Task<(string MessageId, string Jid)> SendReplyAsync(string to, string text, string quotedMsgId, string quotedFromJid, CancellationToken ct = default)
        => _client.SendReplyAsync(to, text, quotedMsgId, quotedFromJid, ct);

    public Task SendMediaAsync(string to, byte[] fileBytes, string mediaType, string mimeType, string caption, string fileName)
        => _client.SendMediaAsync(to, fileBytes, mediaType, mimeType, caption, fileName);

    public Task SendReactionAsync(string targetJid, string targetMessageId, bool targetFromMe, string emoji, CancellationToken ct)
        => _client.SendReactionAsync(targetJid, targetMessageId, targetFromMe, emoji, ct);

    public Task SendTypingAsync(string jid, bool isTyping, CancellationToken ct) => _client.SendTypingAsync(jid, isTyping, ct);
    public Task SendUserPresenceAsync(bool isOnline, CancellationToken ct) => _client.SendUserPresenceAsync(isOnline, ct);

    public Task RevokeMessageAsync(string jid, string messageId, bool fromMe, long timestamp, CancellationToken ct)
        => _client.RevokeMessageAsync(jid, messageId, fromMe, timestamp, ct);

    public Task ForwardMessageAsync(string toJid, string text, CancellationToken ct) => _client.ForwardMessageAsync(toJid, text, ct);

    public Task SendReadReceiptAsync(string jid, string messageId, long timestamp, CancellationToken ct)
        => _client.SendReadReceiptAsync(jid, messageId, timestamp, ct);

    public Task SendManualRetryReceiptAsync(string senderJid, string msgId, long timestamp, CancellationToken ct)
        => _client.SendManualRetryReceiptAsync(senderJid, msgId, timestamp, ct);

    public Task<List<(string Jid, string Name)>> GetContactsAsync(CancellationToken ct) => _client.GetContactsAsync(ct);

    public Task<List<(string Jid, string Name, bool Archived, bool Pinned)>> GetChatsAsync(CancellationToken ct)
        => _client.GetChatsAsync(ct);

    public Task<string?> GetProfilePictureAsync(string jid, CancellationToken ct) => _client.GetProfilePictureAsync(jid, ct);
    public Task SubscribePresenceAsync(string jid, CancellationToken ct) => _client.SubscribePresenceAsync(jid, ct);
    public PresenceInfo? GetPresence(string jid) => _client.GetPresence(jid);
    public Task<string?> ResolveLidAsync(string lidJid, CancellationToken ct) => _client.ResolveLidAsync(lidJid, ct);

    public Task<List<IncomingMessage>> FetchMessageHistoryAsync(string jid, int count, CancellationToken ct)
        => _client.FetchMessageHistoryAsync(jid, count, ct);

    public Task<List<IncomingMessage>> RequestOnDemandHistorySyncAsync(string jid, int count, CancellationToken ct,
        string? oldestMsgId = null, bool oldestFromMe = false, long oldestTimestampMs = 0)
        => _client.RequestOnDemandHistorySyncAsync(jid, count, ct, oldestMsgId, oldestFromMe, oldestTimestampMs);

    public List<string> GetGroupJids() => _client.GetGroupJids();
    public Task<GroupMetadata?> FetchGroupMetadataAsync(string groupJid, CancellationToken ct) => _client.FetchGroupMetadataAsync(groupJid, ct);
    public Task<string?> CreateGroupAsync(string subject, IEnumerable<string> participantJids, CancellationToken ct)
        => _client.CreateGroupAsync(subject, participantJids, ct);
    public Task LeaveGroupAsync(string groupJid, CancellationToken ct) => _client.LeaveGroupAsync(groupJid, ct);
    public Task<Dictionary<string, string>> AddGroupParticipantsAsync(string groupJid, IEnumerable<string> jids, CancellationToken ct)
        => _client.AddGroupParticipantsAsync(groupJid, jids, ct);
    public Task<Dictionary<string, string>> RemoveGroupParticipantsAsync(string groupJid, IEnumerable<string> jids, CancellationToken ct)
        => _client.RemoveGroupParticipantsAsync(groupJid, jids, ct);
    public Task<string?> GetGroupInviteLinkAsync(string groupJid, CancellationToken ct) => _client.GetGroupInviteLinkAsync(groupJid, ct);
    public Task UpdateGroupSubjectAsync(string groupJid, string newSubject, CancellationToken ct)
        => _client.UpdateGroupSubjectAsync(groupJid, newSubject, ct);

    public MessageStatus? GetMessageStatus(string messageId) => _client.GetMessageStatus(messageId);
    public object GetDebugInfo() => _client.GetCacheDebugInfo();

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
