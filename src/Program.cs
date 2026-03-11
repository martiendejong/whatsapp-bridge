using Dawa.Extensions;
using WhatsAppBridge.Services;

var builder = WebApplication.CreateBuilder(args);

// ─── Services ────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Register Dawa WhatsApp client as singleton
builder.Services.AddWhatsApp(options =>
{
    options.SessionDirectory = builder.Configuration["WhatsApp:SessionDirectory"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "whatsapp-bridge", "session");
    options.AutoReconnect = true;
    options.ReconnectDelay = TimeSpan.FromSeconds(5);
    options.QRCodeTimeout = TimeSpan.FromMinutes(5);
});

// Background service that manages the connection + emits QR codes
builder.Services.AddSingleton<QRCodeStore>();
builder.Services.AddHostedService<WhatsAppConnectionService>();

// ─── App ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseCors();
app.UseRouting();
app.MapControllers();

// Root health endpoint
app.MapGet("/", () => Results.Ok(new { service = "WhatsApp Bridge (Dawa)", status = "running" }));

app.Run();
