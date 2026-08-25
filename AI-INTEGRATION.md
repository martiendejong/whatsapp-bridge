# WhatsApp Bridge - AI Integration Guide

## Overview

WhatsApp Bridge provides a RESTful API that allows AI systems and automated applications to send and receive WhatsApp messages programmatically. This guide explains how to integrate with the API.

**Machine-readable version of this guide:** `GET /api/ai-docs` on the base URL below returns this exact document as plain text — no authentication required. That is the canonical URL for an AI agent to discover how to use this bridge; fetch it directly instead of scraping the website.

## Base URL

```
Production: https://whatsapp.wreckingball.ai/api/wa
Development: http://localhost:5149/api/wa
```

## Authentication

All API requests require Bearer token authentication:

```http
Authorization: Bearer YOUR_API_TOKEN
```

### Getting an API Token

1. Register an account at `https://whatsapp.wreckingball.ai`
2. Login to your account
3. Navigate to "API Connections" page
4. Click "Create New Connection"
5. Copy the generated API token (shown once)
6. Connect your WhatsApp by scanning the QR code on the Dashboard/WhatsApp Sessions page

## Identifiers: phone numbers vs. JIDs

Two different identifier shapes are used across the API — passing the wrong one is the most common integration mistake:

- **Bare phone number** (`to`, `number` params): international format digits only, no `+`, no spaces — e.g. `31612345678`. Used by `sendMessage`, `sendMedia`, `checkNumberStatus`.
- **JID** (`chatId`, `*Jid` params): `<number>@s.whatsapp.net` for a 1:1 chat, or `<id>@g.us` for a group — e.g. `31612345678@s.whatsapp.net`. Used by `getMessages`, `requestHistory`, `revokeMessage`, `forwardMessage`, `sendTyping`, `setPresence` target, and all group endpoints.
- Endpoints that take a `chatId` also accept a bare number and will append `@s.whatsapp.net` for you — but an explicit JID is always safer, especially for groups.

## Core Endpoints

### 1. Send Text Message

```http
POST /api/wa/sendMessage
Content-Type: application/json
Authorization: Bearer YOUR_API_TOKEN

{
  "to": "31612345678",
  "body": "Hello from AI! This is an automated message.",
  "sessionId": null
}
```

**Parameters:**
- `to` (string, required): bare phone number, international format, no `+`
- `body` (string, required): message text content
- `sessionId` (string, optional): target a specific connected WhatsApp session/number; omitted = your most recently connected session

**Response:**
```json
{ "success": true }
```

### 2. Send Media Message

Send an image, video, audio, or document with an optional caption, fetched from a public URL.

```http
POST /api/wa/sendMedia
Content-Type: application/json
Authorization: Bearer YOUR_API_TOKEN

{
  "to": "31612345678",
  "mediaUrl": "https://example.com/image.jpg",
  "caption": "Check out this image!"
}
```

**Parameters:**
- `to` (string, required): bare phone number
- `mediaUrl` (string, required): publicly fetchable URL to the media file — the bridge downloads it server-side
- `caption` (string, optional): text caption
- Media type/MIME type is inferred from the fetched response's `Content-Type` header (`image/*`, `audio/*`, `video/*`, else treated as a document)

**Response:**
```json
{ "success": true }
```

### 3. Get Chat Messages

```http
GET /api/wa/getMessages?chatId=31612345678@s.whatsapp.net&limit=50
Authorization: Bearer YOUR_API_TOKEN
```

**Parameters:**
- `chatId` (string, required): JID (bare number also accepted, see above)
- `limit` (integer, optional, default 50): max messages returned, most recent last
- `sessionId` (string, optional)

Only messages the bridge has already seen (received live, or pulled via `requestHistory`) are returned — this is a local cache, not a live query to WhatsApp.

**Response:**
```json
{
  "messages": [
    {
      "id": "3EB0XXXXXXXXXXXXX",
      "from": "31612345678@s.whatsapp.net",
      "to": "31698765432@s.whatsapp.net",
      "body": "Message text",
      "timestamp": 1773099000,
      "type": "text"
    }
  ]
}
```

Media, quoted-reply, and reaction messages carry additional fields (`mediaUrl`, `mimeType`, `fileName`, `mediaKey`, `quotedMessageId`, `quotedText`, `reactionEmoji`, `isRevoked`, `status`, etc.) — treat unknown fields as optional/nullable.

### 4. Get All Chats

```http
GET /api/wa/getChats
Authorization: Bearer YOUR_API_TOKEN
```

**Response:**
```json
[
  { "jid": "31612345678@s.whatsapp.net", "name": "John Doe", "phone": "31612345678", "archived": false, "pinned": false }
]
```

### 5. Get Contacts

```http
GET /api/wa/getContacts
Authorization: Bearer YOUR_API_TOKEN
```

**Response:**
```json
[
  { "id": "31612345678@s.whatsapp.net", "name": "John Doe", "number": "31612345678" }
]
```

### 6. Check Number Status

```http
GET /api/wa/checkNumberStatus?number=31612345678
Authorization: Bearer YOUR_API_TOKEN
```

**Response:**
```json
{ "number": "31612345678", "isWhatsApp": true }
```

> **Known limitation:** this endpoint currently always returns `isWhatsApp: true` without actually verifying registration — real lookup isn't implemented yet. Don't rely on it to validate a number.

## Additional Endpoints

Beyond the core send/read endpoints above, the full API surface (all under `/api/wa`, all Bearer-authenticated, `sessionId` optional on every one unless noted):

| Method & Path | Purpose |
|---|---|
| `POST /requestHistory` | Ask WhatsApp to push older message history for a chat into the local cache. Body: `{ "chatId", "count": 50, "noAnchor": false }`. Best-effort — always returns `200`, never breaks the live connection. |
| `POST /downloadMedia` | Download + decrypt a media attachment. Body: `{ "mediaUrl", "mediaKey", "mimeType" }` (from a message's media fields). Returns the raw file bytes. |
| `POST /revokeMessage` | Delete-for-everyone a sent message. Body: `{ "chatJid", "messageId", "fromMe": true }`. |
| `POST /forwardMessage` | Forward text to another chat. Body: `{ "toJid", "text" }`. |
| `POST /sendTyping` | Send/clear the typing indicator. Body: `{ "chatJid", "isTyping": true }`. |
| `POST /setPresence` | Set your online/offline presence. Body: `{ "available": true }`. |
| `POST /createGroup` | Create a group. Body: `{ "subject", "participants": ["31612345678", ...] }`. |
| `POST /leaveGroup` | Leave a group. Body: `{ "groupJid" }`. |
| `POST /addGroupParticipants` | Body: `{ "groupJid", "participants": [...] }`. |
| `POST /removeGroupParticipants` | Body: `{ "groupJid", "participants": [...] }`. |
| `POST /getGroupInviteLink` | Body: `{ "groupJid" }`. Returns the invite link. |
| `POST /updateGroupSubject` | Rename a group. Body: `{ "groupJid", "subject" }`. |
| `GET /messageStatus?messageId=xxx` | Delivery/read status for a previously sent message. |

## Error Handling

All endpoints return standard HTTP status codes:

- `200 OK`: Request successful
- `400 Bad Request`: Invalid parameters, or no active/matching WhatsApp session
- `401 Unauthorized`: Invalid or missing API token
- `500 Internal Server Error`: Unexpected server error

**Error response shape (send/media endpoints, on WhatsApp-side failures):**
```json
{ "error": "Human-readable message", "errorCode": "MESSAGE_FAILED", "details": null }
```

Other endpoints on failure return `{ "error": "..." }` without `errorCode`/`details`.

## Rate Limiting

The bridge itself does not currently enforce a request rate limit. WhatsApp's own servers apply anti-spam heuristics to accounts that send too fast or too much (especially to numbers that haven't messaged you first) — space out bulk sends (e.g. one message per second) and expect occasional throttling from WhatsApp's side, not from this API.

## Best Practices for AI Integration

### 1. Connection Management

Always verify your WhatsApp session is connected before sending messages:

```python
import requests

API_URL = "https://whatsapp.wreckingball.ai/api/wa"
API_TOKEN = "your-api-token"

headers = {"Authorization": f"Bearer {API_TOKEN}"}

response = requests.get(f"{API_URL}/getChats", headers=headers)
if response.status_code == 400:
    print("No active WhatsApp session - scan the QR code in the dashboard")
```

### 2. Error Handling

```python
import time

def send_message_with_retry(to, body, max_retries=3):
    for attempt in range(max_retries):
        response = requests.post(
            f"{API_URL}/sendMessage",
            headers=headers,
            json={"to": to, "body": body}
        )

        if response.status_code == 200:
            return response.json()

        if response.status_code >= 500:
            time.sleep(2 ** attempt)  # Exponential backoff
            continue

        break  # 400/401 won't succeed on retry

    return None
```

### 3. Phone Number Formatting

```python
def format_phone_number(number: str) -> str:
    """Format phone number to international format without +"""
    number = number.replace(" ", "").replace("-", "").replace("(", "").replace(")", "")
    if number.startswith("+"):
        number = number[1:]
    if number.startswith("00"):
        number = number[2:]
    return number

def to_jid(number: str) -> str:
    return f"{format_phone_number(number)}@s.whatsapp.net"
```

### 4. Message Queue

For bulk messaging, respect WhatsApp's own anti-spam pacing (see Rate Limiting above):

```python
from queue import Queue
import threading
import time

message_queue = Queue()

def worker():
    while True:
        to, body = message_queue.get()
        send_message_with_retry(to, body)
        message_queue.task_done()
        time.sleep(1)  # ~1 message per second

threading.Thread(target=worker, daemon=True).start()

message_queue.put(("31612345678", "Message 1"))
message_queue.put(("31687654321", "Message 2"))

message_queue.join()
```

## Example Integrations

### Python

```python
import requests

class WhatsAppBridge:
    def __init__(self, api_token: str, base_url: str = "https://whatsapp.wreckingball.ai/api/wa"):
        self.api_token = api_token
        self.base_url = base_url
        self.headers = {"Authorization": f"Bearer {api_token}"}

    def send_message(self, to: str, body: str) -> dict:
        response = requests.post(
            f"{self.base_url}/sendMessage",
            headers=self.headers,
            json={"to": to, "body": body}
        )
        response.raise_for_status()
        return response.json()

    def send_media(self, to: str, media_url: str, caption: str = "") -> dict:
        response = requests.post(
            f"{self.base_url}/sendMedia",
            headers=self.headers,
            json={"to": to, "mediaUrl": media_url, "caption": caption}
        )
        response.raise_for_status()
        return response.json()

    def get_messages(self, chat_id: str, limit: int = 50) -> dict:
        response = requests.get(
            f"{self.base_url}/getMessages",
            headers=self.headers,
            params={"chatId": chat_id, "limit": limit}
        )
        response.raise_for_status()
        return response.json()

# Usage
bridge = WhatsAppBridge("your-api-token")
bridge.send_message("31612345678", "Hello from Python!")
```

### Node.js

```javascript
const axios = require('axios');

class WhatsAppBridge {
    constructor(apiToken, baseUrl = 'https://whatsapp.wreckingball.ai/api/wa') {
        this.apiToken = apiToken;
        this.baseUrl = baseUrl;
        this.headers = { 'Authorization': `Bearer ${apiToken}` };
    }

    async sendMessage(to, body) {
        const response = await axios.post(
            `${this.baseUrl}/sendMessage`,
            { to, body },
            { headers: this.headers }
        );
        return response.data;
    }

    async sendMedia(to, mediaUrl, caption = '') {
        const response = await axios.post(
            `${this.baseUrl}/sendMedia`,
            { to, mediaUrl, caption },
            { headers: this.headers }
        );
        return response.data;
    }

    async getMessages(chatId, limit = 50) {
        const response = await axios.get(
            `${this.baseUrl}/getMessages`,
            {
                headers: this.headers,
                params: { chatId, limit }
            }
        );
        return response.data;
    }
}

// Usage
const bridge = new WhatsAppBridge('your-api-token');
await bridge.sendMessage('31612345678', 'Hello from Node.js!');
```

### cURL

```bash
# Send message
curl -X POST https://whatsapp.wreckingball.ai/api/wa/sendMessage \
  -H "Authorization: Bearer YOUR_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"to":"31612345678","body":"Hello from cURL!"}'

# Get messages
curl -X GET "https://whatsapp.wreckingball.ai/api/wa/getMessages?chatId=31612345678@s.whatsapp.net&limit=50" \
  -H "Authorization: Bearer YOUR_API_TOKEN"
```

## Security

- **Never expose your API token**: Store it securely in environment variables
- **Use HTTPS only**: All production requests must use HTTPS
- **Report abuse**: contact the account owner if you detect unauthorized access

## Support

- **GitHub Issues**: https://github.com/martiendejong/whatsappbridge/issues

## Changelog

### 2026-07-23
- Synced this guide with the current API surface: added the 13 endpoints shipped since the original release (history sync, media download, revoke/forward, typing/presence, full group management, delivery status)
- Fixed JID format throughout (`@s.whatsapp.net` / `@g.us`, not `@c.us`) and corrected response examples (`sendMessage`/`sendMedia` return `{success:true}`, not a `messageId`; `getChats`/`getContacts` field names)
- Removed the unenforced numeric rate-limit claims; documented WhatsApp's own anti-spam behavior instead
- Flagged `checkNumberStatus` as a stub (always returns `isWhatsApp: true`)
- Published this guide live at `GET /api/ai-docs` — previously it only existed as a file in this repo, not a reachable URL

### Version 1.0.0 (2026-03-10)
- Initial release: text messaging, media messaging, message retrieval, contact management, session management

---

**License:** MIT
**Maintained by:** Martien de Jong
