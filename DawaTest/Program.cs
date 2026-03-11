using Dawa;
using Dawa.Crypto;

// ─── XEdDSA self-test ────────────────────────────────────────────────────────
{
    var (priv, pub) = Curve25519Helper.GenerateKeyPair();
    var msg = System.Text.Encoding.UTF8.GetBytes("hello world");
    var sig = XEdDSA.Sign(priv, msg);
    var ok  = XEdDSA.Verify(pub, msg, sig);
    Console.WriteLine($"[XEdDSA self-test] Sign+Verify: {(ok ? "PASS ✓" : "FAIL ✗")}");

    // Also verify the actual pre-key signing (message = [0x05 || pubKey])
    var (idPriv, idPub) = Curve25519Helper.GenerateKeyPair();
    var (spkPriv, spkPub) = Curve25519Helper.GenerateKeyPair();
    var preKeyMsg = new byte[33];
    preKeyMsg[0] = 0x05;
    spkPub.CopyTo(preKeyMsg, 1);
    var spkSig  = XEdDSA.Sign(idPriv, preKeyMsg);
    var spkOk   = XEdDSA.Verify(idPub, preKeyMsg, spkSig);
    Console.WriteLine($"[XEdDSA self-test] PreKey Sign+Verify: {(spkOk ? "PASS ✓" : "FAIL ✗")}");
}
// ─────────────────────────────────────────────────────────────────────────────

// --fresh flag wipes the saved session so you always get a fresh QR
var sessionDir = Path.Combine(AppContext.BaseDirectory, "dawa-test-session");
if (args.Contains("--fresh") && Directory.Exists(sessionDir))
{
    Directory.Delete(sessionDir, true);
    Console.WriteLine("Cleared saved session.");
}

// Build the web app first so we can grab its ILoggerFactory for Dawa
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:9191");

// Suppress framework noise, show everything from Dawa
builder.Logging.AddConsole();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("Dawa", LogLevel.Debug);

var app = builder.Build();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

// Shared state written by Dawa events, read by the HTTP server
var latestQr = "";
var status = "connecting";

var client = WhatsAppClient.Create(sessionDir, loggerFactory);

client.QRCodeReceived += (_, qr) =>
{
    latestQr = qr;
    status = "qr_ready";
    Console.WriteLine("[Dawa] QR ready — scan with WhatsApp");
};

client.Connected += (_, _) =>
{
    status = "connected";
    Console.WriteLine("[Dawa] Connected!");
};

client.Disconnected += (_, _) =>
{
    // If we have a QR code, keep showing it — client will auto-reconnect to pick up pair-success.
    // Only mark failed if we never reached the QR stage.
    if (status == "connected")
        status = "disconnected";
    else if (latestQr == "")
        status = "failed";
    // else: QR is displayed, reconnect in progress — keep status as qr_ready
    Console.WriteLine("[Dawa] Disconnected.");
};

// Start WhatsApp client in background
_ = client.ConnectAsync();

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
    <div id="ok">&#10003; OK!</div>
    <div id="error">Server rejected the connection.<br>Try again in a few minutes.</div>
  </div>

  <script>
    let currentQr = null;

    async function poll() {
      try {
        const r = await fetch('/qr');
        const d = await r.json();

        document.getElementById('status').textContent = d.status;

        if (d.status === 'connected') {
          document.getElementById('qr').style.display = 'none';
          document.getElementById('ok').style.display = 'block';
          return;
        }

        if (d.status === 'failed') {
          document.getElementById('qr').style.display = 'none';
          document.getElementById('error').style.display = 'block';
          return; // stop polling
        }

        if (d.qr && d.qr !== currentQr) {
          currentQr = d.qr;
          document.getElementById('qr').innerHTML = '';
          new QRCode(document.getElementById('qr'), {
            text: currentQr,
            width: 256,
            height: 256,
            correctLevel: QRCode.CorrectLevel.M
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
Console.WriteLine("(Use --fresh to force a new QR instead of restoring saved session)");

try
{
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "http://localhost:9191",
        UseShellExecute = true
    });
}
catch { }

await app.RunAsync();
await client.DisposeAsync();
