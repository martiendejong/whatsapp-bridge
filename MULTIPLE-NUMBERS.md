# Multiple WhatsApp Numbers Support

WhatsApp Bridge now supports linking multiple WhatsApp numbers to a single account, allowing you to manage multiple WhatsApp connections and choose which number to use for sending messages.

## Features

- **Multiple Sessions**: Link as many WhatsApp numbers as needed to your account
- **Flexible Selection**: Specify which number to use per API call
- **Automatic Fallback**: If no number specified, uses the first active connection
- **Session Identification**: Use either session ID or phone number to select a session

## How It Works

### Linking Multiple Numbers

Use the frontend to scan QR codes for each WhatsApp number you want to link. Each scanned QR code creates a new `WhatsAppSession` associated with your account.

### Session Selection in API Calls

All API endpoints now accept an optional `sessionId` parameter that can be:

1. **Session ID**: The unique identifier for the session (e.g., `"user-1234-session-1"`)
2. **Phone Number**: The phone number associated with the session (e.g., `"1234567890"`)
3. **Omitted**: Uses the first active (connected) session

## API Usage

### Send Message with Specific Number

**Using Session ID:**

```bash
curl -X POST "https://your-server/api/wa/sendMessage" \
  -H "Authorization: Bearer YOUR_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "to": "1234567890",
    "body": "Hello from specific number!",
    "sessionId": "user-123-session-1"
  }'
```

**Using Phone Number:**

```bash
curl -X POST "https://your-server/api/wa/sendMessage" \
  -H "Authorization: Bearer YOUR_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "to": "1234567890",
    "body": "Hello from specific number!",
    "sessionId": "9876543210"
  }'
```

**Without Specifying (uses first active):**

```bash
curl -X POST "https://your-server/api/wa/sendMessage" \
  -H "Authorization: Bearer YOUR_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "to": "1234567890",
    "body": "Hello from default number!"
  }'
```

## Supported Endpoints

All WhatsApp API endpoints support the optional `sessionId` parameter:

### POST Endpoints

- **sendMessage**: `{ "to": "...", "body": "...", "sessionId": "..." }`
- **sendMedia**: `{ "to": "...", "mediaUrl": "...", "caption": "...", "sessionId": "..." }`

### GET Endpoints

- **getMessages**: `?chatId=...&limit=50&sessionId=...`
- **getChats**: `?sessionId=...`
- **getContacts**: `?sessionId=...`
- **checkNumberStatus**: `?number=...&sessionId=...`

## Python Example

```python
import requests

API_URL = "https://your-server/api/wa"
API_TOKEN = "your-api-token"

headers = {
    "Authorization": f"Bearer {API_TOKEN}",
    "Content-Type": "application/json"
}

# Send message from specific number
response = requests.post(
    f"{API_URL}/sendMessage",
    headers=headers,
    json={
        "to": "1234567890",
        "body": "Hello from business line!",
        "sessionId": "business-number-session"
    }
)

# Send message from personal number
response = requests.post(
    f"{API_URL}/sendMessage",
    headers=headers,
    json={
        "to": "1234567890",
        "body": "Hello from personal line!",
        "sessionId": "personal-number-session"
    }
)

# Send from default number (no sessionId)
response = requests.post(
    f"{API_URL}/sendMessage",
    headers=headers,
    json={
        "to": "1234567890",
        "body": "Hello from default number!"
    }
)
```

## Node.js Example

```javascript
const axios = require('axios');

const API_URL = 'https://your-server/api/wa';
const API_TOKEN = 'your-api-token';

const headers = {
    'Authorization': `Bearer ${API_TOKEN}`,
    'Content-Type': 'application/json'
};

// Send from specific number
await axios.post(`${API_URL}/sendMessage`, {
    to: '1234567890',
    body: 'Hello from specific number!',
    sessionId: 'business-number-session'
}, { headers });

// Send from default number
await axios.post(`${API_URL}/sendMessage`, {
    to: '1234567890',
    body: 'Hello from default number!'
}, { headers });
```

## Error Handling

### Session Not Found

If you specify a `sessionId` that doesn't exist or isn't connected:

```json
{
  "error": "WhatsApp session 'invalid-session' not found or not connected"
}
```

### No Active Sessions

If you don't specify a `sessionId` and no sessions are active:

```json
{
  "error": "No active WhatsApp session"
}
```

## Use Cases

### 1. Business + Personal Numbers

Manage both business and personal WhatsApp accounts from one application:

```python
# Business messages
send_message(to="client@company.com", body="...", session="business-line")

# Personal messages
send_message(to="friend", body="...", session="personal-line")
```

### 2. Multi-Region Operations

Use different numbers for different regions:

```python
# US customers
send_message(to="us-customer", body="...", session="+1-555-0100")

# EU customers
send_message(to="eu-customer", body="...", session="+44-20-1234")
```

### 3. Department-Specific Numbers

Route messages through department-specific numbers:

```python
# Sales department
send_message(to="lead", body="...", session="sales-number")

# Support department
send_message(to="customer", body="...", session="support-number")
```

### 4. Load Balancing

Distribute messages across multiple numbers to avoid rate limits:

```python
sessions = ["session-1", "session-2", "session-3"]
current_session = sessions[message_count % len(sessions)]
send_message(to="...", body="...", session=current_session)
```

## Session Management

### Listing Available Sessions

You can query the database or use the frontend to see all linked sessions:

- Session ID
- Phone number
- Connection status (connected, disconnected, qr_pending)
- Last seen timestamp

### Active Session Priority

When no `sessionId` is specified, the system selects the active session with the most recent `ConnectedAt` timestamp.

## Security Considerations

- **Encryption**: If encryption is enabled, phone numbers in the database are encrypted
- **Session Isolation**: Each session is isolated; one disconnection doesn't affect others
- **Token Validation**: All API calls require valid authentication regardless of session selection

## Migration from Single Number

Existing installations automatically support multiple numbers:

1. **No Breaking Changes**: Existing code without `sessionId` continues to work
2. **Gradual Adoption**: Add `sessionId` parameters only where needed
3. **Backward Compatible**: Single-number setups function identically

## Best Practices

1. **Use Descriptive Session IDs**: Name sessions clearly (e.g., `"customer-support"`, `"sales-team"`)
2. **Handle Errors Gracefully**: Always check for session-not-found errors
3. **Monitor Connection Status**: Track which sessions are active
4. **Load Balance Wisely**: Don't exceed WhatsApp's rate limits per number
5. **Log Session Usage**: Track which session sent which messages for audit trails

## Troubleshooting

### Session ID Not Working

- Verify the session exists in the database
- Check the session status is `"connected"`
- Ensure the session belongs to your user account

### Phone Number Not Working

- Confirm the phone number format matches the database
- Check if encryption is enabled (phone numbers are encrypted)
- Verify the phone number is associated with a connected session

### Always Uses Same Number

- Explicitly specify `sessionId` in API calls
- Check if other sessions are disconnected
- Verify the session ID is correct

## Technical Details

### Session Selection Logic

1. If `sessionId` provided:
   - Try to match as session ID
   - Try to match as phone number (with decryption if needed)
   - Return error if no match found
2. If `sessionId` omitted:
   - Select first connected session ordered by `ConnectedAt` DESC
   - Return error if no connected sessions exist

### Database Schema

```sql
CREATE TABLE WhatsAppSessions (
    Id INTEGER PRIMARY KEY,
    UserId INTEGER NOT NULL,
    SessionId TEXT NOT NULL,
    PhoneNumber TEXT,
    Status TEXT DEFAULT 'disconnected',
    CreatedAt DATETIME,
    ConnectedAt DATETIME,
    LastSeenAt DATETIME,
    FOREIGN KEY (UserId) REFERENCES Users(Id)
);
```

Multiple sessions per user are supported by the `UserId` foreign key.
