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
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddSingleton<EncryptionService>();
// Singleton: forwards allow-listed "/task ..." inbound messages to jengo-agi intake (default-OFF, additive)
builder.Services.AddSingleton<TaskIntakeForwarder>();
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
            Timestamp INTEGER NOT NULL,
            ReceivedAt TEXT NOT NULL,
            IsHistory INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Messages_SessionId_MessageId ON Messages (SessionId, MessageId);
        CREATE INDEX IF NOT EXISTS IX_Messages_ChatJid_Timestamp ON Messages (ChatJid, Timestamp);
        CREATE INDEX IF NOT EXISTS IX_Messages_ReceivedAt ON Messages (ReceivedAt);
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
