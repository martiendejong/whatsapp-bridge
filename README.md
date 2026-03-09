# WhatsApp Bridge

> ASP.NET Core HTTP API for WhatsApp messaging — powered by [Dawa](https://github.com/martiendejong/dawa)

Replaces the Node.js/Baileys bridge with a native .NET service that runs perfectly under IIS or as a Windows Service.

---

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/whatsapp/status` | Connection state, session info |
| `GET` | `/api/whatsapp/qr` | QR code data (JSON) |
| `GET` | `/api/whatsapp/qr.png` | QR code as PNG image |
| `POST` | `/api/whatsapp/send` | Send a text message |
| `POST` | `/api/whatsapp/logout` | Delete session (force re-pair) |

---

## Quick Start

```bash
# Clone
git clone https://github.com/martiendejong/whatsapp-bridge.git
cd whatsapp-bridge/src

# Run
dotnet run

# Open browser to see QR code
start http://localhost:5005/api/whatsapp/qr.png
```

---

## Send a message

```bash
curl -X POST http://localhost:5005/api/whatsapp/send \
  -H "Content-Type: application/json" \
  -d '{"to": "+31612345678", "text": "Hello from the bridge!"}'
```

From your IIS application:

```csharp
using var http = new HttpClient();
await http.PostAsJsonAsync("http://localhost:5005/api/whatsapp/send", new
{
    to = "+31612345678",
    text = "Hello from C#!"
});
```

---

## Configuration

`appsettings.json`:

```json
{
  "WhatsApp": {
    "SessionDirectory": "C:\\ProgramData\\whatsapp-bridge\\session"
  },
  "Urls": "http://localhost:5005"
}
```

---

## First-time pairing

1. Start the service
2. Open `http://localhost:5005/api/whatsapp/qr.png` in a browser
3. Open WhatsApp on your phone → Linked Devices → Link a Device
4. Scan the QR code
5. Done — session is saved, no re-scan needed on restart

---

## IIS deployment

```xml
<!-- web.config -->
<configuration>
  <system.webServer>
    <handlers>
      <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" />
    </handlers>
    <aspNetCore processPath="dotnet"
                arguments=".\WhatsAppBridge.dll"
                stdoutLogEnabled="true"
                stdoutLogFile=".\logs\stdout" />
  </system.webServer>
</configuration>
```

Publish:
```bash
dotnet publish src -c Release -o publish
```

---

## Architecture

```
Client App (IIS)
     │
     │ HTTP POST /api/whatsapp/send
     ▼
WhatsApp Bridge (ASP.NET Core)
     │
     │ uses Dawa library
     ▼
WhatsApp Web Protocol
(Noise XX + Signal + WebSocket)
```

---

## License

Apache 2.0
