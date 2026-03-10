# Error Handling

WhatsApp Bridge implements comprehensive error handling with user-friendly messages and detailed technical logging.

## Features

- **Friendly Error Messages**: Clear, actionable error messages for API users
- **Error Codes**: Standardized error codes for programmatic handling
- **Technical Logging**: Detailed logging for debugging and monitoring
- **QR Expiration Detection**: Automatic detection of expired QR codes
- **Session Status Tracking**: Clear messages about session connectivity
- **Network Error Handling**: Graceful handling of service unavailability

## Error Response Format

All errors follow a consistent format:

```json
{
  "error": "User-friendly error message",
  "errorCode": "ERROR_CODE",
  "details": {
    "additionalKey": "additionalValue"
  }
}
```

## Error Codes

| Error Code | User Message | Cause |
|------------|--------------|-------|
| `QR_EXPIRED` | Your WhatsApp QR code has expired. Please reconnect your WhatsApp account by scanning a new QR code. | QR code timeout (typically 60 seconds) |
| `SESSION_NOT_FOUND` | WhatsApp session not found or not connected. Please ensure your WhatsApp is connected. | Session doesn't exist or was never initialized |
| `SESSION_DISCONNECTED` | Your WhatsApp connection was lost. Please reconnect by scanning a new QR code. | Phone disconnected or session invalidated |
| `RATE_LIMIT` | Too many requests. Please wait a moment before trying again. | WhatsApp rate limiting |
| `INVALID_NUMBER` | The phone number 'X' is not registered on WhatsApp or is invalid. | Number not on WhatsApp or malformed |
| `SERVICE_UNAVAILABLE` | WhatsApp service is temporarily unavailable. Please try again in a moment. | Network error or service down |
| `MESSAGE_FAILED` | Failed to send message. Please check your connection and try again. | Generic send failure |
| `UNKNOWN_ERROR` | An unexpected error occurred. Our team has been notified. | Unexpected exceptions |

## Error Handling Examples

### Python

```python
import requests

try:
    response = requests.post(
        "https://your-server/api/wa/sendMessage",
        headers={"Authorization": f"Bearer {API_TOKEN}"},
        json={"to": "1234567890", "body": "Hello"}
    )
    response.raise_for_status()
    result = response.json()
    print("Message sent:", result)
except requests.exceptions.HTTPError as e:
    error_data = e.response.json()
    error_code = error_data.get("errorCode")
    error_message = error_data.get("error")

    if error_code == "QR_EXPIRED":
        print("QR code expired! User needs to reconnect WhatsApp.")
        # Redirect user to QR scan page
    elif error_code == "SESSION_DISCONNECTED":
        print("Session disconnected! User needs to reconnect WhatsApp.")
        # Show reconnect dialog
    elif error_code == "RATE_LIMIT":
        print("Rate limited. Waiting before retry...")
        time.sleep(5)
        # Retry request
    else:
        print(f"Error: {error_message}")
```

### Node.js

```javascript
const axios = require('axios');

try {
    const response = await axios.post(
        'https://your-server/api/wa/sendMessage',
        { to: '1234567890', body: 'Hello' },
        { headers: { Authorization: `Bearer ${API_TOKEN}` } }
    );
    console.log('Message sent:', response.data);
} catch (error) {
    if (error.response) {
        const { error: message, errorCode, details } = error.response.data;

        switch (errorCode) {
            case 'QR_EXPIRED':
                console.log('QR expired - redirect to reconnect');
                // window.location.href = '/whatsapp-connect';
                break;
            case 'SESSION_DISCONNECTED':
                console.log('Session disconnected - show reconnect dialog');
                break;
            case 'RATE_LIMIT':
                console.log('Rate limited - waiting before retry');
                await new Promise(resolve => setTimeout(resolve, 5000));
                // Retry request
                break;
            case 'INVALID_NUMBER':
                console.log(`Invalid number: ${details?.number}`);
                break;
            default:
                console.log('Error:', message);
        }
    } else {
        console.error('Network error:', error.message);
    }
}
```

### cURL with jq

```bash
#!/bin/bash

response=$(curl -s -w "\n%{http_code}" \
    -X POST "https://your-server/api/wa/sendMessage" \
    -H "Authorization: Bearer $API_TOKEN" \
    -H "Content-Type: application/json" \
    -d '{"to":"1234567890","body":"Hello"}')

# Separate body and status code
http_code=$(echo "$response" | tail -n 1)
body=$(echo "$response" | head -n -1)

if [ "$http_code" -eq 200 ]; then
    echo "Success: $body"
else
    error_code=$(echo "$body" | jq -r '.errorCode')
    error_message=$(echo "$body" | jq -r '.error')

    case "$error_code" in
        "QR_EXPIRED")
            echo "QR code expired! User needs to reconnect."
            ;;
        "SESSION_DISCONNECTED")
            echo "Session disconnected! User needs to reconnect."
            ;;
        "RATE_LIMIT")
            echo "Rate limited. Waiting 5 seconds..."
            sleep 5
            # Retry...
            ;;
        *)
            echo "Error: $error_message"
            ;;
    esac
fi
```

## Handling Specific Scenarios

### QR Code Expiration

**Symptom:** `errorCode: "QR_EXPIRED"`

**What Happened:** The QR code shown to the user wasn't scanned within the timeout period (typically 60 seconds).

**How to Handle:**
1. Show user a message: "QR code expired. Generating a new one..."
2. Call `/session/create` again to get a fresh QR code
3. Display the new QR code to the user

**Example:**

```python
def connect_whatsapp(session_id):
    try:
        # Try to send test message
        send_message(session_id, test_number, "Test")
    except WhatsAppError as e:
        if e.error_code == "QR_EXPIRED":
            # Generate new QR
            new_qr = create_new_session(session_id)
            show_qr_to_user(new_qr)
        else:
            raise
```

### Session Disconnected

**Symptom:** `errorCode: "SESSION_DISCONNECTED"`

**What Happened:**
- User logged out of WhatsApp Web on their phone
- Phone lost internet connection for extended period
- WhatsApp service restarted and lost session state

**How to Handle:**
1. Inform user: "WhatsApp disconnected. Please reconnect."
2. Mark session as disconnected in database
3. Provide "Reconnect" button that initiates new QR scan

### Rate Limiting

**Symptom:** `errorCode: "RATE_LIMIT"`

**What Happened:** Too many messages sent in short time period (WhatsApp anti-spam protection).

**How to Handle:**
1. Implement exponential backoff retry strategy
2. Queue messages for later delivery
3. Consider distributing load across multiple sessions

**Example:**

```python
import time

def send_with_retry(session_id, to, body, max_retries=3):
    for attempt in range(max_retries):
        try:
            return send_message(session_id, to, body)
        except WhatsAppError as e:
            if e.error_code == "RATE_LIMIT":
                wait_time = 2 ** attempt  # Exponential backoff
                print(f"Rate limited. Waiting {wait_time}s...")
                time.sleep(wait_time)
            else:
                raise
    raise Exception("Max retries exceeded")
```

### Invalid Number

**Symptom:** `errorCode: "INVALID_NUMBER"`

**What Happened:**
- Number not registered on WhatsApp
- Invalid phone number format
- Number blocked or deactivated

**How to Handle:**
1. Validate phone numbers before sending (use `checkNumberStatus` endpoint)
2. Show user: "This number is not on WhatsApp"
3. Ask user to verify the number

## Technical Logging

All errors are logged with full technical details:

```csharp
_logger.LogError(ex, "Error sending message. SessionId: {SessionId}, To: {To}", sessionId, to);
```

**Log Levels:**
- **Error**: All failed operations with full exception details
- **Warning**: Recoverable issues (rate limits, temporary failures)
- **Info**: Successful operations
- **Debug**: Detailed request/response data (if enabled)

**Log Format:**

```
[2026-03-10 10:30:45] ERROR: Failed to send message. Status: 410, Error: Session disconnected
  SessionId: user-123-session-1
  To: 1234567890
  Exception: WhatsAppServiceException
  Message: Session user-123-session-1 disconnected
```

## Monitoring and Alerts

### Recommended Monitoring

1. **Error Rate**: Alert if error rate > 5% of requests
2. **QR Expiration**: Track how often users let QR codes expire
3. **Session Disconnects**: Alert on frequent disconnects (may indicate service issues)
4. **Rate Limits**: Track rate limit errors (may need load balancing)
5. **Service Availability**: Alert if `SERVICE_UNAVAILABLE` errors spike

### Health Check Endpoint

Consider implementing a health check endpoint:

```csharp
[HttpGet("health")]
public async Task<IActionResult> Health()
{
    try
    {
        // Check WhatsApp service connectivity
        var response = await _httpClient.GetAsync($"{_whatsappServiceUrl}/health");
        return Ok(new { status = "healthy", whatsappService = response.IsSuccessStatusCode });
    }
    catch
    {
        return StatusCode(503, new { status = "unhealthy", whatsappService = false });
    }
}
```

## Best Practices

1. **Always Handle Errors**: Never assume API calls will succeed
2. **Show Friendly Messages**: Display `error` field to users, not technical details
3. **Log Technical Details**: Always log the full exception for debugging
4. **Implement Retries**: Use exponential backoff for transient errors
5. **Validate Inputs**: Check phone numbers before sending
6. **Monitor Session Health**: Regularly check session status
7. **Graceful Degradation**: Queue messages if service is temporarily unavailable

## Testing Error Scenarios

### Manual Testing

```bash
# Test QR expired (requires mocking in WhatsApp service)
curl -X POST https://your-server/api/wa/sendMessage \
  -H "Authorization: Bearer TOKEN" \
  -d '{"to":"1234567890","body":"Test"}'

# Expected: {"error":"Your WhatsApp QR code has expired...","errorCode":"QR_EXPIRED"}
```

### Unit Testing

```csharp
[Fact]
public async Task SendMessage_WhenQrExpired_ReturnsQrExpiredError()
{
    // Arrange
    _whatsappServiceMock
        .Setup(x => x.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
        .ThrowsAsync(new WhatsAppServiceException(WhatsAppError.QrExpired("test-session")));

    // Act
    var result = await _controller.SendMessage(new SendMessageRequest("123", "Test"));

    // Assert
    var badRequest = Assert.IsType<ObjectResult>(result);
    Assert.Equal(400, badRequest.StatusCode);
    dynamic value = badRequest.Value;
    Assert.Equal("QR_EXPIRED", value.errorCode);
}
```

## Migration from Previous Version

If upgrading from a version without comprehensive error handling:

1. **Update API Clients**: Add error code handling in your integration code
2. **Update UI**: Show friendly error messages from API responses
3. **Add Monitoring**: Set up alerts for new error codes
4. **Test Scenarios**: Verify QR expiration, disconnects, and rate limiting work correctly

## Support

If you encounter errors not covered by this documentation:

1. Check the technical logs for detailed error information
2. Verify WhatsApp service is running (`http://localhost:3000/health`)
3. Check database for session status
4. Review network connectivity between services

For persistent issues, file a GitHub issue with:
- Error code and message
- Technical logs
- Steps to reproduce
