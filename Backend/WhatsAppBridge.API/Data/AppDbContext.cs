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
            entity.Property(e => e.Recipient).HasMaxLength(200);
        });

        modelBuilder.Entity<InboundContact>(entity =>
        {
            entity.HasIndex(e => e.Sender).IsUnique();
            entity.Property(e => e.Sender).HasMaxLength(200);
        });
    }
}
