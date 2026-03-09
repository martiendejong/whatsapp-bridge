# WhatsApp Bridge - AI Integration Guide

## Overview

WhatsApp Bridge provides a RESTful API that allows AI systems and automated applications to send and receive WhatsApp messages programmatically. This guide explains how to integrate with the API.

## Base URL

```
Production: https://whatsapp.wreckingball.ai/api/wa
Development: http://localhost:5000/api/wa
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
5. Copy the generated API token
6. Connect your WhatsApp by scanning QR code

## API Endpoints

### 1. Send Text Message

Send a text message to a WhatsApp number.

```http
POST /api/wa/sendMessage
Content-Type: application/json
Authorization: Bearer YOUR_API_TOKEN

{
  "to": "31612345678",
  "body": "Hello from AI! This is an automated message."
}
```

**Parameters:**
- `to` (string, required): Phone number in international format without + (e.g., "31612345678")
- `body` (string, required): Message text content

**Response:**
```json
{
  "success": true,
  "messageId": "3EB0XXXXXXXXXXXXX"
}
```

### 2. Send Media Message

Send an image, video, or document with optional caption.

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
- `to` (string, required): Phone number
- `mediaUrl` (string, required): Public URL to media file
- `caption` (string, optional): Text caption for media

**Supported Media Types:**
- Images: JPG, PNG, GIF
- Videos: MP4, AVI, MOV
- Documents: PDF, DOC, DOCX, XLS, XLSX
- Audio: MP3, WAV, OGG

### 3. Get Messages

Retrieve messages from a specific chat.

```http
GET /api/wa/getMessages?chatId=31612345678@c.us&limit=50
Authorization: Bearer YOUR_API_TOKEN
```

**Parameters:**
- `chatId` (string, required): Chat identifier (format: `number@c.us`)
- `limit` (integer, optional): Maximum messages to retrieve (default: 50)

**Response:**
```json
{
  "messages": [
    {
      "id": "3EB0XXXXXXXXXXXXX",
      "from": "31612345678@c.us",
      "body": "Message text",
      "timestamp": 1773099000,
      "hasMedia": false
    }
  ]
}
```

### 4. Get All Chats

Retrieve list of all chats.

```http
GET /api/wa/getChats
Authorization: Bearer YOUR_API_TOKEN
```

**Response:**
```json
{
  "chats": [
    {
      "id": "31612345678@c.us",
      "name": "John Doe",
      "unreadCount": 3,
      "lastMessage": {
        "body": "Last message text",
        "timestamp": 1773099000
      }
    }
  ]
}
```

### 5. Get Contacts

Retrieve all WhatsApp contacts.

```http
GET /api/wa/getContacts
Authorization: Bearer YOUR_API_TOKEN
```

**Response:**
```json
{
  "contacts": [
    {
      "id": "31612345678@c.us",
      "name": "John Doe",
      "number": "31612345678",
      "isMyContact": true
    }
  ]
}
```

### 6. Check Number Status

Verify if a phone number has WhatsApp.

```http
GET /api/wa/checkNumberStatus?number=31612345678
Authorization: Bearer YOUR_API_TOKEN
```

**Response:**
```json
{
  "exists": true,
  "number": "31612345678@c.us"
}
```

## Error Handling

All endpoints return standard HTTP status codes:

- `200 OK`: Request successful
- `400 Bad Request`: Invalid parameters
- `401 Unauthorized`: Invalid or missing API token
- `404 Not Found`: Resource not found
- `500 Internal Server Error`: Server error

**Error Response Format:**
```json
{
  "error": "Error message description",
  "code": "ERROR_CODE"
}
```

**Common Error Codes:**
- `INVALID_TOKEN`: API token is invalid or expired
- `SESSION_DISCONNECTED`: WhatsApp session is not connected (scan QR code)
- `INVALID_NUMBER`: Phone number format is invalid
- `MESSAGE_SEND_FAILED`: Failed to send message
- `RATE_LIMIT_EXCEEDED`: Too many requests

## Rate Limiting

- Maximum 60 requests per minute per API token
- Maximum 1000 messages per day per WhatsApp session
- Bulk operations limited to 100 items per request

## Best Practices for AI Integration

### 1. Connection Management

Always verify your WhatsApp session is connected before sending messages:

```python
import requests

API_URL = "https://whatsapp.wreckingball.ai/api/wa"
API_TOKEN = "your-api-token"

headers = {"Authorization": f"Bearer {API_TOKEN}"}

# Check session status before sending
response = requests.get(f"{API_URL}/getChats", headers=headers)
if response.status_code == 401:
    print("Session disconnected - please scan QR code")
```

### 2. Error Handling

Implement exponential backoff for retries:

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

        if response.status_code == 429:  # Rate limit
            time.sleep(2 ** attempt)  # Exponential backoff
            continue

        break

    return None
```

### 3. Phone Number Formatting

Always format phone numbers correctly:

```python
def format_phone_number(number: str) -> str:
    """Format phone number to international format without +"""
    # Remove spaces, dashes, parentheses
    number = number.replace(" ", "").replace("-", "").replace("(", "").replace(")", "")

    # Remove + if present
    if number.startswith("+"):
        number = number[1:]

    # Remove leading zeros for country code
    if number.startswith("00"):
        number = number[2:]

    return number
```

### 4. Message Queue

For bulk messaging, implement a queue to respect rate limits:

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
        time.sleep(1)  # Rate limiting: 1 message per second

# Start worker thread
threading.Thread(target=worker, daemon=True).start()

# Add messages to queue
message_queue.put(("31612345678", "Message 1"))
message_queue.put(("31687654321", "Message 2"))

# Wait for all messages to be sent
message_queue.join()
```

### 5. Webhook Integration (Coming Soon)

Future versions will support webhooks for receiving incoming messages:

```json
{
  "event": "message.received",
  "data": {
    "from": "31612345678@c.us",
    "body": "Incoming message text",
    "timestamp": 1773099000
  }
}
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
curl -X GET "https://whatsapp.wreckingball.ai/api/wa/getMessages?chatId=31612345678@c.us&limit=50" \
  -H "Authorization: Bearer YOUR_API_TOKEN"
```

## Security

- **Never expose your API token**: Store it securely in environment variables
- **Use HTTPS only**: All production requests must use HTTPS
- **Rotate tokens regularly**: Generate new tokens every 90 days
- **Monitor usage**: Check your API usage dashboard regularly
- **Report abuse**: Contact support if you detect unauthorized access

## Support

- **API Documentation**: https://whatsapp.wreckingball.ai/docs
- **Status Page**: https://status.wreckingball.ai
- **GitHub Issues**: https://github.com/martiendejong/whatsappbridge/issues
- **Email Support**: support@wreckingball.ai

## Changelog

### Version 1.0.0 (2026-03-10)
- Initial release
- Text messaging support
- Media messaging support
- Message retrieval
- Contact management
- Session management

---

**License:** MIT
**Maintained by:** Martien de Jong
**Generated by:** Claude Code AI Assistant
