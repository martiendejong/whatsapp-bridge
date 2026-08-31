using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using WhatsAppBridge.API.Data;
using WhatsAppBridge.API.Services;
using WhatsAppBridge.API.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Optional local override file (gitignored) — real secrets like TaskIntake:ApiKey live here,
// or in environment variables (e.g. TASKINTAKE__APIKEY). Placeholders only in appsettings.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Configure IIS integration for production
if (builder.Environment.IsProduction())
{
    // Use IIS integration - IIS will handle all port binding
    builder.WebHost.UseIIS();
}
else
{
    // In development, allow binding to localhost
    builder.WebHost.UseUrls("http://localhost:5149");
}

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "WhatsApp Bridge API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token.",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new()
    {
        {
            new()
            {
                Reference = new()
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Authentication (JWT + API Key)
var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtKey))
        };
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.DefaultScheme,
        options => { });

// Services
builder.Services.AddHttpClient();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<IamService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddSingleton<EncryptionService>();
// Singleton: forwards allow-listed "/task ..." inbound messages to jengo-agi intake (default-OFF, additive)
builder.Services.AddSingleton<TaskIntakeForwarder>();
// Singleton: pushes every live inbound message to jengo-agi for direct replies (default-OFF, additive)
builder.Services.AddSingleton<InboundWebhookForwarder>();
// Singleton: asks coachingplatform (CoachOS) for an AI reply for non-allow-listed senders
// (task 1067, default-OFF, additive). Never sends anything itself — see class doc comment.
builder.Services.AddSingleton<CoachOsIntakeForwarder>();
builder.Services.AddScoped<OutboundGuardrailService>();
// Singleton: transcribes inbound audio via OpenAI Whisper (task 869ejuycr). Resolves its API
// key lazily from config or the Prospergenics vault — see WhisperTranscriptionService.
builder.Services.AddSingleton<WhisperTranscriptionService>();
// Singleton: holds long-lived Dawa WhatsAppClient instances (one per user session)
builder.Services.AddSingleton<WhatsAppBridgeService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:5173" })
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Deploy-time version tracking: lets JengoAGI (or anyone) confirm which version a running
// instance actually has, instead of guessing from build timestamps/commit counts. The version
// comes from the assembly's <Version> (WhatsAppBridge.API.csproj), which deploy/bump-version.ps1
// keeps in sync with the repo-root VERSION file on every release.
app.MapGet("/api/version", () =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
    return Results.Ok(new { version, buildTimeUtc = System.IO.File.GetLastWriteTimeUtc(typeof(Program).Assembly.Location) });
}).AllowAnonymous();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated() no-ops when the database already exists, so tables added later
    // (like the durable Messages store, task 869ecbkv7) must be self-healed explicitly.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS Messages (
            Id INTEGER NOT NULL CONSTRAINT PK_Messages PRIMARY KEY AUTOINCREMENT,
            SessionId TEXT NOT NULL,
            ChatJid TEXT NOT NULL,
            MessageId TEXT NOT NULL,
            FromMe INTEGER NOT NULL,
            Sender TEXT NOT NULL,
            Body TEXT NOT NULL,
            Type TEXT NOT NULL,
            MediaUrl TEXT NULL,
            MediaKey TEXT NULL,
            MimeType TEXT NULL,
            Timestamp INTEGER NOT NULL,
            ReceivedAt TEXT NOT NULL,
            IsHistory INTEGER NOT NULL,
            Transcript TEXT NULL,
            LocalMediaPath TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Messages_SessionId_MessageId ON Messages (SessionId, MessageId);
        CREATE INDEX IF NOT EXISTS IX_Messages_ChatJid_Timestamp ON Messages (ChatJid, Timestamp);
        CREATE INDEX IF NOT EXISTS IX_Messages_ReceivedAt ON Messages (ReceivedAt);
        """);

    // Self-heal: UserId on Messages (ownership stable across QR re-pairs - a re-pair replaces
    // the session row/GUID, which orphaned pre-re-pair messages for session-scoped queries).
    // Backfill from the current session rows where possible; remaining orphans keep NULL and
    // are still returned via the legacy SessionId fallback in the read endpoints.
    var hasUserId = db.Database.SqlQueryRaw<int>(
        "SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'UserId'").AsEnumerable().First();
    if (hasUserId == 0)
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Messages ADD COLUMN UserId INTEGER NULL;");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Messages_UserId ON Messages (UserId);");
    }
    db.Database.ExecuteSqlRaw(
        "UPDATE Messages SET UserId = (SELECT w.UserId FROM WhatsAppSessions w WHERE w.SessionId = Messages.SessionId) " +
        "WHERE UserId IS NULL AND EXISTS (SELECT 1 FROM WhatsAppSessions w WHERE w.SessionId = Messages.SessionId);");

    // One-time backfill for the 6 messages orphaned by the 2026-08-03 re-pair incident that
    // motivated this fix: WhatsAppSessions row 24 (SessionId starting 768fd204) was replaced by
    // row 27 (4ab31111) before UserId existed, so no live session remains for the generic
    // backfill above to resolve their owner from. This was already applied once as a manual
    // datafix during the initial deploy; encoding it here makes it reproducible (e.g. after a
    // restore from an older backup) instead of living only in a one-off SQL command. Scoped to
    // UserId IS NULL, so it is a no-op everywhere else and after it has run once here.
    db.Database.ExecuteSqlRaw(
        "UPDATE Messages SET UserId = 4 WHERE UserId IS NULL AND SessionId LIKE '768fd204%';");

    // Columns added after the table already existed elsewhere (task 869ecw8du: MediaKey +
    // MimeType, needed to download-and-decrypt media via the bridge instead of dead-linking
    // to the encrypted WhatsApp CDN URL). SQLite has no "ADD COLUMN IF NOT EXISTS", so guard
    // each with a duplicate-column catch instead.
    foreach (var alterSql in new[]
             {
                 "ALTER TABLE Messages ADD COLUMN MediaKey TEXT NULL",
                 "ALTER TABLE Messages ADD COLUMN MimeType TEXT NULL",
                 // Task 869ejuycr: Whisper transcript + eagerly-decrypted local media cache path.
                 "ALTER TABLE Messages ADD COLUMN Transcript TEXT NULL",
                 "ALTER TABLE Messages ADD COLUMN LocalMediaPath TEXT NULL",
             })
    {
        try
        {
            db.Database.ExecuteSqlRaw(alterSql);
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already present from a prior startup or the CREATE TABLE above.
        }
    }

    // Outbound guardrail audit trail (task 869edf485): EnsureCreated() no-ops on an
    // already-existing DB, so a table added after go-live must be self-healed explicitly.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS BlockedOutboundMessages (
            Id INTEGER NOT NULL CONSTRAINT PK_BlockedOutboundMessages PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NULL,
            Endpoint TEXT NOT NULL,
            Recipient TEXT NOT NULL,
            BodyPreview TEXT NOT NULL,
            Reason TEXT NOT NULL,
            BlockedAtUtc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_BlockedOutboundMessages_BlockedAtUtc ON BlockedOutboundMessages (BlockedAtUtc);
        CREATE INDEX IF NOT EXISTS IX_BlockedOutboundMessages_UserId ON BlockedOutboundMessages (UserId);
        """);

    // Outbound guardrail volume-cap accounting (task 897): every ALLOWED send, so the
    // guardrail can count sends per recipient/24h and globally/hour. Same self-heal reason
    // as BlockedOutboundMessages above.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS OutboundSendLogs (
            Id INTEGER NOT NULL CONSTRAINT PK_OutboundSendLogs PRIMARY KEY AUTOINCREMENT,
            Recipient TEXT NOT NULL,
            SentAtUtc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_OutboundSendLogs_Recipient_SentAtUtc ON OutboundSendLogs (Recipient, SentAtUtc);
        CREATE INDEX IF NOT EXISTS IX_OutboundSendLogs_SentAtUtc ON OutboundSendLogs (SentAtUtc);
        """);

    // CoachOS service-route reply-window tracking (task 1067): every genuine inbound message's
    // sender + timestamp, so OutboundGuardrailService can prove a reply is answering a real prior
    // inbound message before allowing it through the allow-list exception. Same self-heal reason
    // as the tables above.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS InboundContacts (
            Id INTEGER NOT NULL CONSTRAINT PK_InboundContacts PRIMARY KEY AUTOINCREMENT,
            Sender TEXT NOT NULL,
            LastInboundAtUtc TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_InboundContacts_Sender ON InboundContacts (Sender);
        """);

    // Durable chat list (fix/message-persistence-survives-deploy): getChats upserts every live
    // result here and falls back to it when Dawa is offline, so known contacts survive
    // restarts and re-pairs. Same self-heal reason as above: EnsureCreated() no-ops on an
    // existing DB, and this deployment does not run EF migrations.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS Chats (
            Id INTEGER NOT NULL CONSTRAINT PK_Chats PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            Jid TEXT NOT NULL,
            Name TEXT NOT NULL,
            Phone TEXT NOT NULL,
            LastSeenAt TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Chats_UserId_Jid ON Chats (UserId, Jid);
        CREATE INDEX IF NOT EXISTS IX_Chats_UserId ON Chats (UserId);
        """);
}

// Restore WhatsApp sessions on startup — includes "disconnected" sessions that have saved credentials
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var whatsappService = app.Services.GetRequiredService<WhatsAppBridgeService>();
    var sessionsRoot = app.Configuration["WhatsApp:SessionsDirectory"]
        ?? Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions");

    // Restore any session that has a saved creds.json, regardless of last-known DB status.
    // Do NOT filter by status — "failed" sessions have creds and can still reconnect.
    var allSessions = db.WhatsAppSessions
        .Select(s => s.SessionId)
        .ToList();
    foreach (var sessionId in allSessions)
    {
        var credsPath = Path.Combine(sessionsRoot, sessionId, "creds.json");
        if (File.Exists(credsPath))
            await whatsappService.RestoreSessionAsync(sessionId);
    }
}

app.Run();
