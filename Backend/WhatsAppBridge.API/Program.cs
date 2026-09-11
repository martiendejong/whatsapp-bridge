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
builder.Services.AddScoped<OutboundRoutingService>();
builder.Services.AddScoped<OutboundGuardrailService>();
// Singleton: transcribes inbound audio via OpenAI Whisper (task 869ejuycr). Resolves its API
// key lazily from config or the Prospergenics vault — see WhisperTranscriptionService.
builder.Services.AddSingleton<WhisperTranscriptionService>();
// Singleton: admin-selected WhatsApp engine ("dawa"/"baileys"), persisted in AppSettings
builder.Services.AddSingleton<EngineSettingsService>();
// Singleton: holds long-lived WhatsApp engine instances (one per user session)
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

// BEFORE authentication and authorization, and the position is the point. The audit row is
// written in this middleware's `finally`, which runs after everything downstream — including
// the auth middlewares — has finished, so the row still sees the authenticated identity and the
// final status code. What sitting upstream buys is the refusals: when this middleware sat after
// UseAuthorization, a request rejected with 401/403 never reached it at all, so the log recorded
// every successful call and none of the credential-guessing — the one traffic class an audit
// log exists to catch. (After UseCors on purpose: a short-circuited CORS preflight is browser
// plumbing, not API traffic worth a row.)
//
// Deliberately middleware rather than per-controller: only 3 of the 20+ endpoints invoke the
// outbound guardrail, and an audit trail wired in the same per-controller way would inherit
// that same gap on every endpoint added later.
app.UseMiddleware<WhatsAppBridge.API.Middleware.ApiAuditMiddleware>();

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
            LocalMediaPath TEXT NULL,
            PushName TEXT NULL
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
                 // Sender display names on the messages page (push name captured per message).
                 "ALTER TABLE Messages ADD COLUMN PushName TEXT NULL",
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

    // Category + BodyHash arrived after this table was already live, and SQLite has no
    // "ADD COLUMN IF NOT EXISTS" — so ask the schema first. Without these two the routing
    // redirect cannot tell a duplicate fan-out from a genuine repeat alert.
    AddColumnIfMissing(db, "OutboundSendLogs", "Category", "TEXT NULL");
    AddColumnIfMissing(db, "OutboundSendLogs", "BodyHash", "TEXT NULL");
    // DEFAULT 1, deliberately backwards: rows from before this column existed were
    // overwhelmingly real deliveries, and reading them as unconfirmed attempts would switch the
    // duplicate suppression off for its whole lookback window on deploy day. New rows are
    // written 0 by the guardrail and flipped to 1 only after the send actually succeeds.
    AddColumnIfMissing(db, "OutboundSendLogs", "Delivered", "INTEGER NOT NULL DEFAULT 1");
    db.Database.ExecuteSqlRaw(
        "CREATE INDEX IF NOT EXISTS IX_OutboundSendLogs_Recipient_BodyHash_SentAtUtc " +
        "ON OutboundSendLogs (Recipient, BodyHash, SentAtUtc);");

    // Single-row bookkeeping the tables above cannot express: "has X ever happened on this
    // database". First (and so far only) use is the routing seed marker below.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS AppFlags (
            Name TEXT NOT NULL CONSTRAINT PK_AppFlags PRIMARY KEY,
            Value TEXT NOT NULL,
            SetAtUtc TEXT NOT NULL
        );
        """);

    // Outbound routing policy: who may be messaged, about what, and at what hour in THEIR
    // timezone. Creating the table and filling it changes nothing on its own — the policy is
    // only enforced once OutboundRouting:Enabled is set, which is false by default.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS OutboundContacts (
            Id INTEGER NOT NULL CONSTRAINT PK_OutboundContacts PRIMARY KEY AUTOINCREMENT,
            Phone TEXT NOT NULL,
            Name TEXT NOT NULL,
            Alias TEXT NULL,
            Enabled INTEGER NOT NULL DEFAULT 1,
            TimeZoneId TEXT NOT NULL DEFAULT 'Europe/Amsterdam',
            WindowStartHour INTEGER NOT NULL DEFAULT 0,
            WindowEndHour INTEGER NOT NULL DEFAULT 24,
            Categories TEXT NOT NULL DEFAULT '*',
            FallbackPhone TEXT NULL,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_OutboundContacts_Phone ON OutboundContacts (Phone);
        CREATE INDEX IF NOT EXISTS IX_OutboundContacts_Alias ON OutboundContacts (Alias);
        """);

    // Seed the policy once, from config — and "once" means once per DATABASE, not "whenever the
    // table is empty". The emptiness check alone had a trap: deleting every contact in the UI is
    // the natural way to disarm routing without a deploy, and the next process restart (the
    // watchdog restarts services routinely) would quietly re-seed the lot and re-arm the policy
    // Martien had just switched off. So the seed leaves a marker in AppFlags, and an empty table
    // WITH the marker is respected as a decision. To genuinely re-seed: delete the
    // 'OutboundRoutingSeeded' row from AppFlags and restart.
    //
    // Wrapped, because everything in here is driven by hand-edited configuration and the failure
    // mode without the wrapper is the worst one available: two entries with the same number
    // violate the unique index, SaveChanges throws inside startup, and the bridge does not come
    // up at all. A policy that cannot be seeded is a problem; a WhatsApp bridge that will not
    // start because of a typo in a contact list is an outage.
    try
    {
        // Backfill for databases whose contacts predate the marker (seeded by an earlier build
        // of this feature). Without this, exactly those databases still had the resurrection
        // trap: contacts present, marker absent, so emptying the table in the UI plus one
        // restart re-seeded the policy anyway. A populated table IS the evidence that seeding
        // (or manual entry) already happened — record it as such.
        if (db.OutboundContacts.Any() && !AppFlagExists(db, "OutboundRoutingSeeded"))
        {
            SetAppFlag(db, "OutboundRoutingSeeded", "backfilled: contacts already present");
        }
        else if (!db.OutboundContacts.Any() && !AppFlagExists(db, "OutboundRoutingSeeded"))
        {
            var seedSection = app.Configuration.GetSection("OutboundRouting:Seed");
            var seed = seedSection.Get<List<WhatsAppBridge.API.Models.OutboundContact>>() ?? new();

            // Distinguish "no seed configured" from "a seed is configured but did not bind" —
            // one malformed hour turns the whole list into zero entries, and without this the
            // only symptom is an empty table that looks exactly like the intended default.
            if (seed.Count == 0 && seedSection.GetChildren().Any())
            {
                app.Logger.LogError(
                    "OutboundRouting:Seed has {Count} configured entries but none of them bound to a contact. " +
                    "Check the field names and that the hours are numbers, not strings. No contacts were seeded.",
                    seedSection.GetChildren().Count());
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var accepted = 0;
            foreach (var contact in seed)
            {
                var phone = WhatsAppBridge.API.Services.PhoneNumber.Normalize(contact.Phone);
                if (!WhatsAppBridge.API.Services.PhoneNumber.IsUsable(phone))
                {
                    app.Logger.LogError("Skipped routing seed entry '{Name}': '{Phone}' is not a usable number.",
                        contact.Name, contact.Phone);
                    continue;
                }
                if (!seen.Add(phone))
                {
                    app.Logger.LogError("Skipped duplicate routing seed entry for {Phone} ('{Name}').", phone, contact.Name);
                    continue;
                }

                // The same bar RoutingController.Upsert sets. The seed used to skip these checks,
                // which produced the exact failure Upsert's validation exists to prevent — a
                // typo'd timezone seeded cleanly and then fell back to UTC at send time, shifting
                // the contact's whole window by hours with nothing anywhere saying why.
                try
                {
                    TimeZoneInfo.FindSystemTimeZoneById(contact.TimeZoneId);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    app.Logger.LogError("Skipped routing seed entry '{Name}': unknown TimeZoneId '{Tz}'.",
                        contact.Name, contact.TimeZoneId);
                    continue;
                }
                if (contact.WindowStartHour is < 0 or > 23 || contact.WindowEndHour is < 1 or > 24)
                {
                    app.Logger.LogError(
                        "Skipped routing seed entry '{Name}': window {Start}-{End} is out of range (start 0-23, end 1-24).",
                        contact.Name, contact.WindowStartHour, contact.WindowEndHour);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(contact.Categories))
                {
                    app.Logger.LogError(
                        "Skipped routing seed entry '{Name}': Categories is empty. Use '*' or a comma-separated list.",
                        contact.Name);
                    continue;
                }

                contact.Id = 0;
                contact.Phone = phone;
                contact.CreatedAtUtc = contact.UpdatedAtUtc = DateTime.UtcNow;
                db.OutboundContacts.Add(contact);
                accepted++;
            }

            if (accepted > 0)
            {
                db.SaveChanges();
                // Marker only on a successful seed: a malformed config that bound nothing must
                // stay retryable, or fixing the typo would do nothing until someone also finds
                // and deletes the flag.
                SetAppFlag(db, "OutboundRoutingSeeded", $"{accepted} contacts");
                app.Logger.LogInformation(
                    "Seeded {Count} outbound routing contacts from configuration. Routing enforcement is {State}.",
                    accepted,
                    app.Configuration.GetValue("OutboundRouting:Enabled", false) ? "ON" : "OFF (OutboundRouting:Enabled is false)");
            }
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Seeding outbound routing contacts failed. The bridge continues without a seeded " +
                                "policy; add contacts under Routing in the admin UI.");
    }

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

    // Full API audit trail: one row per request, written by ApiAuditMiddleware. Answers
    // "wat is er via deze API-key verstuurd, naar welk nummer, en ging het eruit of niet".
    // Same self-heal (CREATE IF NOT EXISTS at startup) reason as the tables above.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS ApiAuditLogs (
            Id INTEGER NOT NULL CONSTRAINT PK_ApiAuditLogs PRIMARY KEY AUTOINCREMENT,
            AtUtc TEXT NOT NULL,
            UserId INTEGER NULL,
            ApiConnectionId INTEGER NULL,
            ApiConnectionName TEXT NULL,
            AuthScheme TEXT NOT NULL,
            Method TEXT NOT NULL,
            Path TEXT NOT NULL,
            EventType TEXT NOT NULL,
            Phone TEXT NULL,
            Body TEXT NULL,
            ResponsePreview TEXT NULL,
            StatusCode INTEGER NOT NULL,
            Outcome TEXT NOT NULL,
            DurationMs INTEGER NOT NULL,
            ClientIp TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_ApiAuditLogs_AtUtc ON ApiAuditLogs (AtUtc);
        CREATE INDEX IF NOT EXISTS IX_ApiAuditLogs_Phone_AtUtc ON ApiAuditLogs (Phone, AtUtc);
        CREATE INDEX IF NOT EXISTS IX_ApiAuditLogs_EventType_AtUtc ON ApiAuditLogs (EventType, AtUtc);
        CREATE INDEX IF NOT EXISTS IX_ApiAuditLogs_ApiConnectionId_AtUtc ON ApiAuditLogs (ApiConnectionId, AtUtc);
        CREATE INDEX IF NOT EXISTS IX_ApiAuditLogs_UserId ON ApiAuditLogs (UserId);
        """);

    // Global key/value settings (engine switch feature): holds the admin-selected WhatsApp
    // engine ("dawa"/"baileys"). Same self-heal reason as the tables above.
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS AppSettings (
            Key TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL
        );
        """);

    // Engine column on sessions (informational: which engine the session last connected with).
    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE WhatsAppSessions ADD COLUMN Engine TEXT NULL;");
    }
    catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
    {
        // Column already present from a prior startup.
    }

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
            LastSeenAt TEXT NOT NULL,
            CustomName TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Chats_UserId_Jid ON Chats (UserId, Jid);
        CREATE INDEX IF NOT EXISTS IX_Chats_UserId ON Chats (UserId);
        """);

    // User-supplied display-name override per contact (sender-names feature). After the
    // CREATE above so the table is guaranteed to exist on every upgrade path.
    try
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Chats ADD COLUMN CustomName TEXT NULL;");
    }
    catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
    {
        // Column already present from a prior startup or the CREATE TABLE above.
    }
}

// Restore WhatsApp sessions on startup — includes "disconnected" sessions that have saved credentials
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var whatsappService = app.Services.GetRequiredService<WhatsAppBridgeService>();
    var sessionsRoot = app.Configuration["WhatsApp:SessionsDirectory"]
        ?? Path.Combine(AppContext.BaseDirectory, "whatsapp-sessions");

    // Restore any session that has saved creds for the selected engine, regardless of
    // last-known DB status ("failed" sessions have creds and can still reconnect).
    // RestoreSessionAsync itself checks the engine-specific creds location
    // (creds.json for Dawa, baileys-auth/creds.json for Baileys) and skips otherwise.
    var allSessions = db.WhatsAppSessions
        .Select(s => s.SessionId)
        .ToList();
    foreach (var sessionId in allSessions)
    {
        if (Directory.Exists(Path.Combine(sessionsRoot, sessionId)))
            await whatsappService.RestoreSessionAsync(sessionId);
    }
}

app.Run();

/// <summary>
/// SQLite's ALTER TABLE has no IF NOT EXISTS, and re-running a plain ADD COLUMN on an existing
/// column throws — which at startup means the app refuses to boot. So read the current schema
/// via pragma and only add what is genuinely absent. Idempotent by construction, in keeping
/// with the CREATE TABLE IF NOT EXISTS blocks above.
/// </summary>
static void AddColumnIfMissing(WhatsAppBridge.API.Data.AppDbContext db, string table, string column, string definition)
{
    var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) connection.Open();

    using (var probe = connection.CreateCommand())
    {
        probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        if (Convert.ToInt32(probe.ExecuteScalar()) > 0) return;
    }

    using var alter = connection.CreateCommand();
    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
    alter.ExecuteNonQuery();
}

/// <summary>
/// Reads a one-time marker from AppFlags. ADO rather than an EF entity: these two helpers are
/// the only consumers, and a DbSet would put startup bookkeeping in the application model.
/// </summary>
static bool AppFlagExists(WhatsAppBridge.API.Data.AppDbContext db, string name)
{
    var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) connection.Open();

    using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM AppFlags WHERE Name = @name;";
    var p = cmd.CreateParameter();
    p.ParameterName = "@name";
    p.Value = name;
    cmd.Parameters.Add(p);
    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
}

static void SetAppFlag(WhatsAppBridge.API.Data.AppDbContext db, string name, string value)
{
    var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) connection.Open();

    using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        INSERT INTO AppFlags (Name, Value, SetAtUtc) VALUES (@name, @value, @at)
        ON CONFLICT(Name) DO UPDATE SET Value = @value, SetAtUtc = @at;
        """;
    var pn = cmd.CreateParameter(); pn.ParameterName = "@name"; pn.Value = name; cmd.Parameters.Add(pn);
    var pv = cmd.CreateParameter(); pv.ParameterName = "@value"; pv.Value = value; cmd.Parameters.Add(pv);
    var pa = cmd.CreateParameter(); pa.ParameterName = "@at"; pa.Value = DateTime.UtcNow.ToString("O"); cmd.Parameters.Add(pa);
    cmd.ExecuteNonQuery();
}
