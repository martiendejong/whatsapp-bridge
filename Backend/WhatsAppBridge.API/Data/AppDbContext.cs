using Microsoft.EntityFrameworkCore;
using WhatsAppBridge.API.Models;

namespace WhatsAppBridge.API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<ApiConnection> ApiConnections { get; set; }
    public DbSet<WhatsAppSession> WhatsAppSessions { get; set; }
    public DbSet<TwoFactorToken> TwoFactorTokens { get; set; }
    public DbSet<StoredMessage> Messages { get; set; }
    public DbSet<StoredChat> Chats { get; set; }
    public DbSet<BlockedOutboundMessage> BlockedOutboundMessages { get; set; }
    public DbSet<OutboundSendLog> OutboundSendLogs { get; set; }
    public DbSet<InboundContact> InboundContacts { get; set; }
    public DbSet<ApiAuditLog> ApiAuditLogs { get; set; }
    public DbSet<OutboundContact> OutboundContacts { get; set; }
    public DbSet<MonitorSubject> MonitorSubjects { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.Email).HasMaxLength(255);
        });

        modelBuilder.Entity<ApiConnection>(entity =>
        {
            entity.HasIndex(e => e.Token).IsUnique();
            entity.Property(e => e.Token).HasMaxLength(500);
        });

        modelBuilder.Entity<WhatsAppSession>(entity =>
        {
            entity.HasIndex(e => e.SessionId).IsUnique();
            entity.Property(e => e.SessionId).HasMaxLength(100);
        });

        modelBuilder.Entity<StoredMessage>(entity =>
        {
            entity.ToTable("Messages");
            entity.HasIndex(e => new { e.SessionId, e.MessageId }).IsUnique();
            entity.HasIndex(e => new { e.ChatJid, e.Timestamp });
            entity.HasIndex(e => e.ReceivedAt);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.SessionId).HasMaxLength(100);
            entity.Property(e => e.ChatJid).HasMaxLength(200);
            entity.Property(e => e.MessageId).HasMaxLength(200);
            entity.Property(e => e.Sender).HasMaxLength(200);
            entity.Property(e => e.Type).HasMaxLength(40);
        });

        modelBuilder.Entity<StoredChat>(entity =>
        {
            entity.ToTable("Chats");
            entity.HasIndex(e => new { e.UserId, e.Jid }).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Jid).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.Name).HasMaxLength(500);
        });

        modelBuilder.Entity<BlockedOutboundMessage>(entity =>
        {
            entity.HasIndex(e => e.BlockedAtUtc);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Endpoint).HasMaxLength(60);
            entity.Property(e => e.Recipient).HasMaxLength(200);
            entity.Property(e => e.BodyPreview).HasMaxLength(200);
        });

        modelBuilder.Entity<OutboundSendLog>(entity =>
        {
            entity.HasIndex(e => new { e.Recipient, e.SentAtUtc });
            entity.HasIndex(e => e.SentAtUtc);
            // The redirect dedupe asks "did the fallback already get this exact message" — a
            // three-column lookup, so it gets its own index rather than riding on Recipient.
            entity.HasIndex(e => new { e.Recipient, e.BodyHash, e.SentAtUtc });
            entity.Property(e => e.Recipient).HasMaxLength(200);
            entity.Property(e => e.Category).HasMaxLength(60);
            entity.Property(e => e.BodyHash).HasMaxLength(32);
        });

        modelBuilder.Entity<OutboundContact>(entity =>
        {
            entity.HasIndex(e => e.Phone).IsUnique();
            entity.HasIndex(e => e.Alias);
            entity.Property(e => e.Phone).HasMaxLength(40);
            entity.Property(e => e.Alias).HasMaxLength(60);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.TimeZoneId).HasMaxLength(80);
            entity.Property(e => e.Categories).HasMaxLength(400);
            entity.Property(e => e.FallbackPhone).HasMaxLength(40);
        });

        modelBuilder.Entity<InboundContact>(entity =>
        {
            entity.HasIndex(e => e.Sender).IsUnique();
            entity.Property(e => e.Sender).HasMaxLength(200);
        });

        modelBuilder.Entity<MonitorSubject>(entity =>
        {
            entity.HasIndex(e => e.Subject).IsUnique();
            entity.Property(e => e.Subject).HasMaxLength(200);
            entity.Property(e => e.Status).HasMaxLength(10);
            entity.Property(e => e.LastDetail).HasMaxLength(400);
            // See MonitorSubject.Version — the report path and the sweep race at exactly the
            // threshold minute, and the loser of this token is what keeps that to one alert.
            entity.Property(e => e.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ApiAuditLog>(entity =>
        {
            // The three filter dimensions Martien asked for, each indexed with AtUtc because
            // every query is "this number / this event type, newest first".
            entity.HasIndex(e => e.AtUtc);
            entity.HasIndex(e => new { e.Phone, e.AtUtc });
            entity.HasIndex(e => new { e.EventType, e.AtUtc });
            entity.HasIndex(e => new { e.ApiConnectionId, e.AtUtc });
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Method).HasMaxLength(10);
            entity.Property(e => e.Path).HasMaxLength(400);
            entity.Property(e => e.EventType).HasMaxLength(80);
            entity.Property(e => e.Phone).HasMaxLength(40);
            entity.Property(e => e.AuthScheme).HasMaxLength(20);
            entity.Property(e => e.Outcome).HasMaxLength(20);
            entity.Property(e => e.ApiConnectionName).HasMaxLength(200);
            entity.Property(e => e.ClientIp).HasMaxLength(64);
        });
    }
}
