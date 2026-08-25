# WhatsApp Bridge

A complete WhatsApp Web API bridge that allows you to integrate WhatsApp messaging into your applications. Built with ASP.NET Core, React, and Node.js.

## Features

- **User Authentication**: Secure login/signup system with JWT
- **Admin Role System**: Administrator privileges with server-side setup scripts - [See Admin Setup](./Backend/ADMIN-SETUP.md)
- **Account Management**: Users can update their email and password
- **Two-Factor Authentication**: 2FA via WhatsApp or Email with clickable links - [See WhatsApp 2FA Guide](./2FA-WHATSAPP.md) | [See Email 2FA Guide](./2FA-EMAIL.md)
- **Multiple WhatsApp Numbers**: Link multiple WhatsApp numbers to one account and choose which to use - [See Guide](./MULTIPLE-NUMBERS.md)
- **Comprehensive Error Handling**: Friendly error messages with QR expiration detection - [See Error Guide](./ERROR-HANDLING.md)
- **API Token Management**: Create and manage multiple API connections
- **WhatsApp Integration**: Connect WhatsApp via QR code scanning
- **RESTful API**: Clean API endpoints mirroring WhatsApp Web functionality
- **AI Integration**: Comprehensive API documentation for automated systems - [See AI Integration Guide](./AI-INTEGRATION.md), also live at `GET https://whatsapp.wreckingball.ai/api/ai-docs` (no auth required)
- **Optional Encryption**: AES-256 encryption for sensitive data (phone numbers, tokens, messages)
- **Windows VPS Ready**: Complete deployment scripts for production

## Architecture

- **Backend API**: ASP.NET Core 9.0 with Entity Framework Core (SQLite)
- **WhatsApp Service**: Node.js with whatsapp-web.js
- **Frontend**: React 18 + TypeScript + Vite
- **Deployment**: PowerShell scripts for Windows Server with IIS

## Quick Start (Development)

### Prerequisites

- .NET 9 SDK
- Node.js 20+
- Visual Studio Code or Visual Studio 2022

### 1. Start the WhatsApp Service

```bash
cd WhatsAppService
npm install
npm start
```

The service will run on http://localhost:3000

### 2. Start the Backend API

```bash
cd Backend/WhatsAppBridge.API
dotnet run
```

The API will run on http://localhost:5000
Swagger documentation: http://localhost:5000/swagger

### 3. Start the Frontend

```bash
cd Frontend
npm install
npm run dev
```

The web app will run on http://localhost:5173

## Production Deployment (Windows VPS)

### Prerequisites

Run as Administrator:

```powershell
cd deploy
.\install-prerequisites.ps1
```

This installs:
- .NET 9 SDK
- Node.js LTS
- IIS with required features
- ASP.NET Core Hosting Bundle
- NSSM (Windows service manager)

### Deploy All Components

```powershell
.\deploy-all.ps1
```

Or deploy individually:

```powershell
# Deploy backend API
.\deploy-backend.ps1

# Deploy WhatsApp service
.\deploy-whatsapp-service.ps1

# Deploy frontend
.\deploy-frontend.ps1
```

### Default Deployment Locations

- **Backend API**: `C:\inetpub\whatsappbridge-api` (Port 5000)
- **WhatsApp Service**: `C:\Services\WhatsAppBridge` (Port 3000)
- **Frontend**: `C:\inetpub\whatsappbridge-web` (Port 80)

## Admin User Setup

WhatsApp Bridge includes an admin role system. See [Backend/ADMIN-SETUP.md](./Backend/ADMIN-SETUP.md) for detailed instructions.

**Quick setup:**

```bash
# Navigate to Backend directory
cd Backend

# Make a user admin (PowerShell)
.\make-admin.ps1 -Email "admin@example.com"

# Or using bash
./make-admin.sh admin@example.com
```

**Requirements:** SQLite3 command-line tool must be installed on the server.

## Security Configuration

### Enable Encryption

1. Generate encryption keys:

```powershell
cd Backend/WhatsAppBridge.API
dotnet run --generate-keys
```

Or use the provided utility:

```powershell
.\tools\generate-encryption-keys.ps1
```

2. Update `appsettings.Production.json`:

```json
{
  "Encryption": {
    "Enabled": true,
    "Key": "your-base64-encoded-32-byte-key",
    "IV": "your-base64-encoded-16-byte-iv"
  }
}
```

3. Restart the API:

```powershell
iisreset
```

### What Gets Encrypted

When encryption is enabled:
- API tokens
- Phone numbers
- Message content (optional)

## API Documentation

Base URL: `http://your-server:5000/api/wa`

### Authentication

All API requests require a Bearer token:

```bash
Authorization: Bearer YOUR_API_TOKEN
```

### Endpoints

#### Send Text Message

```bash
POST /api/wa/sendMessage
Content-Type: application/json

{
  "to": "1234567890",
  "body": "Hello from WhatsApp Bridge!"
}
```

#### Send Media

```bash
POST /api/wa/sendMedia
Content-Type: application/json

{
  "to": "1234567890",
  "mediaUrl": "https://example.com/image.jpg",
  "caption": "Check this out!"
}
```

#### Get Messages

```bash
GET /api/wa/getMessages?chatId=1234567890@c.us&limit=50
```

#### Get Chats

```bash
GET /api/wa/getChats
```

#### Get Contacts

```bash
GET /api/wa/getContacts
```

#### Check Number Status

```bash
GET /api/wa/checkNumberStatus?number=1234567890
```

## Project Structure

```
whatsappbridge/
├── Backend/
│   └── WhatsAppBridge.API/
│       ├── Controllers/          # API controllers
│       ├── Models/               # Database models
│       ├── Services/             # Business logic
│       └── Data/                 # EF Core context
├── WhatsAppService/              # Node.js WhatsApp service
│   ├── index.js                  # Main service file
│   └── package.json
├── Frontend/                     # React application
│   ├── src/
│   │   ├── pages/               # Page components
│   │   ├── components/          # Reusable components
│   │   ├── api.ts               # API client
│   │   └── AuthContext.tsx      # Auth state management
│   └── package.json
├── deploy/                       # Deployment scripts
│   ├── install-prerequisites.ps1
│   ├── deploy-backend.ps1
│   ├── deploy-whatsapp-service.ps1
│   ├── deploy-frontend.ps1
│   └── deploy-all.ps1
└── tools/                        # Utility scripts
    └── generate-encryption-keys.ps1
```

## Usage Flow

1. **Register/Login**: Create an account or login
2. **Create API Connection**: Generate an API token
3. **Connect WhatsApp**: Scan QR code with your phone
4. **Use API**: Send messages, retrieve chats, manage contacts

## Troubleshooting

### "I sent a message but the conversation isn't showing under /messages"

`/messages` (and the durable message store behind it) only ever contains chats that
belong to **this bridge's own linked WhatsApp account** — the number you scanned the
QR code with when you connected the session. It has no way to see:

- **Messages sent from your personal phone's own WhatsApp app.** That's a completely
  separate WhatsApp account/session; the bridge cannot read another account's chats,
  even if both accounts happen to message the same contact. If you want a conversation
  with a contact to show up under `/messages`, it must be sent *through this bridge*
  (via `POST /api/wa/sendMessage`, the JWT `sessions/{id}/send` endpoint, or the
  "Verstuur" button on the `/messages` page itself) — not from your own phone.
- **Chat history that predates when the durable store was introduced.** `GET
  /api/wa/getChats` reads live from the connected WhatsApp session and can list a chat
  (e.g. an old conversation still cached on the phone) that has zero rows in the
  durable `Messages` table, because that table is only ever populated by messages
  sent/received *through the bridge* after the durable store went live. A chat visible
  via `getChats` is not proof any message from it was ever persisted or sent by the
  bridge.
- **A brand-new contact with no prior bridge-sent/received message.** The `/messages`
  page only lists chats that already have at least one row in the durable store —
  there's currently no "start a new conversation" action in the UI, so the very first
  message to a contact must go through the send API directly.

If a message really was sent through the bridge's own send paths and still doesn't
show up, that's a real bug — check that the underlying WhatsApp send call
(`WhatsAppBridgeService.SendMessageAsync`) actually succeeded (a thrown
`WhatsAppServiceException` short-circuits before the message is persisted) rather than
assuming the persistence step itself failed.

### WhatsApp Service Won't Start

Check the service logs:

```powershell
Get-Content C:\Services\WhatsAppBridge\service-error.log -Tail 50
```

### Backend API Errors

Check IIS logs or run in development mode:

```bash
cd Backend/WhatsAppBridge.API
dotnet run
```

### Frontend Build Fails

Clear node_modules and reinstall:

```bash
cd Frontend
rm -rf node_modules
npm install
npm run build
```

## Development

### Adding New API Endpoints

1. Add method to `WhatsAppService/index.js`
2. Add method to `WhatsAppBridgeService.cs`
3. Add endpoint to `WhatsAppApiController.cs`
4. Update API documentation

### Database Migrations

```bash
cd Backend/WhatsAppBridge.API
dotnet ef migrations add MigrationName
dotnet ef database update
```

## License

MIT License - feel free to use in your projects.

## Support

For issues and feature requests, please create an issue in the repository.
