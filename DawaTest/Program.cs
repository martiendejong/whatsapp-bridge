using Dawa;

// --fresh flag wipes the saved session so you always get a fresh QR
var sessionDir = Path.Combine(AppContext.BaseDirectory, "dawa-test-session");
if (args.Contains("--fresh") && Directory.Exists(sessionDir))
{
    Directory.Delete(sessionDir, true);
    Console.WriteLine("Cleared saved session.");
}

// Shared state written by Dawa events, read by the HTTP server
var latestQr = "";
var status = "connecting";

var client = WhatsAppClient.Create(sessionDir);

client.QRCodeReceived += (_, qr) =>
{
    latestQr = qr;
    status = "qr_ready";
    Console.WriteLine("[Dawa] QR ready — scan with WhatsApp");
};

client.Connected += (_, _) =>
{
    status = "connected";
    Console.WriteLine("[Dawa] Connected! You can close the browser tab.");
};

client.Disconnected += (_, _) =>
{
    if (status != "connected")
        status = "failed";
    Console.WriteLine("[Dawa] Disconnected.");
};

// Start WhatsApp client in background
_ = client.ConnectAsync();

// Minimal HTTP server on port 9191
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:9191");
builder.Logging.SetMinimumLevel(LogLevel.Warning); // silence Kestrel noise

var app = builder.Build();

// JSON endpoint polled by the page
app.MapGet("/qr", () => new { qr = latestQr, status });

// Test page
app.MapGet("/", () => Results.Content("""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Dawa QR Test</title>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/qrcodejs/1.0.0/qrcode.min.js"></script>
  <style>
    body { font-family: sans-serif; text-align: center; padding: 40px; background: #f5f5f5; }
    #card { display: inline-block; background: white; padding: 32px 48px; border-radius: 12px;
            box-shadow: 0 2px 12px rgba(0,0,0,.12); margin-top: 24px; }
    #status { font-size: 18px; margin-bottom: 16px; color: #555; }
    #ok { font-size: 64px; color: #22c55e; display: none; }
    #error { color: #dc2626; display: none; }
  </style>
</head>
<body>
  <h2>WhatsApp QR Test</h2>
  <div id="card">
    <div id="status">Connecting...</div>
    <div id="qr"></div>
    <div id="ok">✓ OK!</div>
    <div id="error">Server rejected the connection.<br>Try again in a few minutes.</div>
  </div>

  <script>
    let currentQr = null;
    let qrObj = null;

    async function poll() {
      try {
        const r = await fetch('/qr');
        const d = await r.json();

        document.getElementById('status').textContent = d.status;

        if (d.status === 'connected') {
          document.getElementById('qr').style.display = 'none';
          document.getElementById('ok').style.display = 'block';
          return; // stop polling
        }

        if (d.status === 'failed') {
          document.getElementById('qr').style.display = 'none';
          document.getElementById('error').style.display = 'block';
          return; // stop polling
        }

        if (d.qr && d.qr !== currentQr) {
          currentQr = d.qr;
          document.getElementById('qr').innerHTML = '';
          qrObj = new QRCode(document.getElementById('qr'), {
            text: currentQr,
            width: 256,
            height: 256,
            correctLevel: QRCode.CorrectLevel.L
          });
        }
      } catch (e) {
        document.getElementById('status').textContent = 'Error: ' + e.message;
      }
      setTimeout(poll, 2000);
    }

    poll();
  </script>
</body>
</html>
""", "text/html"));

Console.WriteLine("Open http://localhost:9191 in your browser");
Console.WriteLine("(Add --fresh flag to force a new QR instead of restoring saved session)");

// Open browser automatically
try
{
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "http://localhost:9191",
        UseShellExecute = true
    });
}
catch { /* ignore if browser can't open */ }

await app.RunAsync();
await client.DisposeAsync();
