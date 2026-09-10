using Dawa.Messages;
using Dawa.Noise;

namespace WhatsAppBridge.API.Services.Engines;

/// <summary>
/// Abstraction over a WhatsApp client engine so the bridge can run either Dawa
/// (in-process C# port of Baileys) or the real Baileys library (Node.js sidecar).
/// The surface mirrors exactly what <see cref="WhatsAppBridgeService"/> consumes;
/// Dawa's message/group/presence records double as the shared DTOs.
/// Engines that cannot support an operation throw <see cref="NotSupportedException"/>.
/// </summary>
public interface IWhatsAppEngine : IAsyncDisposable
{
    /// <summary>"dawa" or "baileys" — matches the persisted admin setting.</summary>
    string EngineName { get; }

    bool IsConnected { get; }
    string? MyJid { get; }
    bool HasSavedSession { get; }

    event EventHandler<string>? QRCodeReceived;
    event EventHandler? Connected;
    event EventHandler? Disconnected;
    event EventHandler<IncomingMessage>? MessageReceived;
    event EventHandler<IncomingMessage>? HistoryMessageReceived;
    event EventHandler<int>? HistorySyncCompleted;
    event EventHandler<(string MessageId, string Jid, MessageStatus Status)>? MessageStatusUpdated;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<(string MessageId, string Jid)> SendMessageAsync(string to, string text, CancellationToken ct = default);
    Task<(string MessageId, string Jid)> SendReplyAsync(string to, string text, string quotedMsgId, string quotedFromJid, CancellationToken ct = default);
    Task SendMediaAsync(string to, byte[] fileBytes, string mediaType, string mimeType, string caption, string fileName);
    Task SendReactionAsync(string targetJid, string targetMessageId, bool targetFromMe, string emoji, CancellationToken ct);
    Task SendTypingAsync(string jid, bool isTyping, CancellationToken ct);
    Task SendUserPresenceAsync(bool isOnline, CancellationToken ct);
    Task RevokeMessageAsync(string jid, string messageId, bool fromMe, long timestamp, CancellationToken ct);
    Task ForwardMessageAsync(string toJid, string text, CancellationToken ct);
    Task SendReadReceiptAsync(string jid, string messageId, long timestamp, CancellationToken ct);
    Task SendManualRetryReceiptAsync(string senderJid, string msgId, long timestamp, CancellationToken ct);

    Task<List<(string Jid, string Name)>> GetContactsAsync(CancellationToken ct);
    Task<List<(string Jid, string Name, bool Archived, bool Pinned)>> GetChatsAsync(CancellationToken ct);
    Task<string?> GetProfilePictureAsync(string jid, CancellationToken ct);
    Task SubscribePresenceAsync(string jid, CancellationToken ct);
    PresenceInfo? GetPresence(string jid);
    Task<string?> ResolveLidAsync(string lidJid, CancellationToken ct);

    Task<List<IncomingMessage>> FetchMessageHistoryAsync(string jid, int count, CancellationToken ct);
    Task<List<IncomingMessage>> RequestOnDemandHistorySyncAsync(string jid, int count, CancellationToken ct,
        string? oldestMsgId = null, bool oldestFromMe = false, long oldestTimestampMs = 0);

    List<string> GetGroupJids();
    Task<GroupMetadata?> FetchGroupMetadataAsync(string groupJid, CancellationToken ct);
    Task<string?> CreateGroupAsync(string subject, IEnumerable<string> participantJids, CancellationToken ct);
    Task LeaveGroupAsync(string groupJid, CancellationToken ct);
    Task<Dictionary<string, string>> AddGroupParticipantsAsync(string groupJid, IEnumerable<string> jids, CancellationToken ct);
    Task<Dictionary<string, string>> RemoveGroupParticipantsAsync(string groupJid, IEnumerable<string> jids, CancellationToken ct);
    Task<string?> GetGroupInviteLinkAsync(string groupJid, CancellationToken ct);
    Task UpdateGroupSubjectAsync(string groupJid, string newSubject, CancellationToken ct);

    MessageStatus? GetMessageStatus(string messageId);
    object GetDebugInfo();
}
