# 2FA via WhatsApp

WhatsApp Bridge now supports Two-Factor Authentication (2FA) via WhatsApp messages, providing an additional security layer for user logins.

## Features

- **WhatsApp-Based 2FA**: Receive verification codes via WhatsApp
- **Clickable Links**: Click a link in the message to verify instantly
- **Manual Token Entry**: Paste the 6-digit code manually
- **Configurable**: Enable/disable 2FA per user
- **Automatic Phone Detection**: Uses linked WhatsApp number or user's phone

## How It Works

### 1. Enable 2FA

Users can enable 2FA via WhatsApp in their account settings (requires phone number or linked WhatsApp session).

### 2. Login Flow

When 2FA is enabled:

1. User enters email and password
2. If credentials valid, system generates 6-digit code
3. Code sent via WhatsApp to user's phone
4. User receives message with code and verification link
5. User clicks link OR enters code manually
6. Upon verification, JWT token issued and user logged in

### 3. Token Expiration

- **Validity**: 10 minutes from generation
- **Single Use**: Token invalidated after successful verification
- **Automatic Cleanup**: Expired tokens removed hourly

## API Endpoints

### Enable 2FA (Account Settings)

```bash
PUT /api/auth/enable-2fa
Authorization: Bearer YOUR_JWT_TOKEN
Content-Type: application/json

{
  "method": "whatsapp",
  "phoneNumber": "+1234567890"  // Optional if WhatsApp already linked
}
```

### Login with 2FA

**Step 1: Initial Login**

```bash
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Response (2FA Required):**

```json
{
  "requiresTwoFactor": true,
  "twoFactorMethod": "whatsapp",
  "userId": 1,
  "message": "A verification code has been sent to your WhatsApp."
}
```

**Step 2: Verify 2FA Code**

```bash
POST /api/auth/verify-2fa
Content-Type: application/json

{
  "token": "123456"
}
```

**Response (Success):**

```json
{
  "user": {
    "id": 1,
    "email": "user@example.com",
    "lastLoginAt": "2026-03-10T10:30:00Z"
  },
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

## WhatsApp Message Format

When a user logs in, they receive:

```
Your WhatsApp Bridge login code is: *123456*

Or click here to verify: https://your-domain.com/verify-2fa?token=123456

This code expires in 10 minutes.
```

## Frontend Implementation

### Login Component

```typescript
async function login(email: string, password: string) {
  const response = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password })
  });

  const data = await response.json();

  if (data.requiresTwoFactor) {
    // Redirect to 2FA verification page
    navigate('/verify-2fa', { state: { userId: data.userId, method: data.twoFactorMethod } });
  } else {
    // Login successful, store token
    localStorage.setItem('token', data.token);
    navigate('/dashboard');
  }
}
```

### 2FA Verification Component

```typescript
function Verify2FA() {
  const [token, setToken] = useState('');
  const location = useLocation();

  // Check if token in URL (from WhatsApp link click)
  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const urlToken = params.get('token');
    if (urlToken) {
      verify2FA(urlToken);
    }
  }, []);

  async function verify2FA(code: string) {
    const response = await fetch('/api/auth/verify-2fa', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: code })
    });

    const data = await response.json();

    if (response.ok) {
      localStorage.setItem('token', data.token);
      navigate('/dashboard');
    } else {
      setError(data.message);
    }
  }

  return (
    <div>
      <h2>Enter Verification Code</h2>
      <p>A code was sent to your WhatsApp</p>
      <input
        value={token}
        onChange={(e) => setToken(e.target.value)}
        placeholder="123456"
        maxLength={6}
      />
      <button onClick={() => verify2FA(token)}>Verify</button>
    </div>
  );
}
```

## Database Schema

### User Table Updates

```sql
ALTER TABLE Users ADD COLUMN PhoneNumber TEXT;
ALTER TABLE Users ADD COLUMN TwoFactorEnabled BOOLEAN DEFAULT 0;
ALTER TABLE Users ADD COLUMN TwoFactorMethod TEXT DEFAULT 'email';
```

### TwoFactorTokens Table

```sql
CREATE TABLE TwoFactorTokens (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId INTEGER NOT NULL,
    Token TEXT NOT NULL,
    Method TEXT NOT NULL,
    CreatedAt DATETIME NOT NULL,
    ExpiresAt DATETIME NOT NULL,
    IsUsed BOOLEAN DEFAULT 0,
    UsedAt DATETIME,
    FOREIGN KEY (UserId) REFERENCES Users(Id)
);
```

## Security Considerations

### Token Generation

- **6-digit codes**: 1 million possible combinations
- **Cryptographically secure**: Uses `RandomNumberGenerator`
- **Single use**: Token invalidated after verification
- **Time-limited**: 10-minute expiration

### Phone Number Privacy

- Phone numbers stored in database (encrypted if encryption enabled)
- Not exposed in API responses
- Only used for 2FA delivery

### Rate Limiting

Consider implementing rate limiting:
- Max 3 login attempts per 5 minutes
- Max 5 2FA code requests per hour per user
- Temporary account lock after 10 failed 2FA verifications

## Configuration

### appsettings.json

```json
{
  "BaseUrl": "https://your-domain.com",
  "TwoFactor": {
    "TokenLength": 6,
    "ExpirationMinutes": 10,
    "CleanupIntervalHours": 1
  }
}
```

## Error Handling

### Invalid/Expired Token

```json
{
  "message": "Invalid or expired verification code"
}
```

**Frontend Action**: Show error, allow user to request new code

### No Phone Number

```json
{
  "message": "Failed to send WhatsApp 2FA code. Please ensure you have a phone number linked."
}
```

**Frontend Action**: Redirect to account settings to add phone number

### WhatsApp Service Down

If WhatsApp service is unavailable, fallback to email 2FA:

```typescript
if (error.message.includes('WhatsApp')) {
  // Offer email 2FA alternative
  showEmailFallbackOption();
}
```

## Use Cases

### 1. Enhanced Security

Require 2FA for admin accounts:

```csharp
if (user.IsAdmin && !user.TwoFactorEnabled)
{
    return BadRequest(new { message = "Admin accounts must enable 2FA" });
}
```

### 2. Suspicious Login Detection

Trigger 2FA on suspicious logins:

```csharp
if (IsSuspiciousLogin(user, ipAddress))
{
    user.TwoFactorEnabled = true; // Force 2FA
}
```

### 3. Device Verification

Remember verified devices to skip 2FA:

```csharp
if (!IsKnownDevice(deviceId) && user.TwoFactorEnabled)
{
    // Require 2FA
}
```

## Testing

### Manual Testing

```bash
# 1. Login with 2FA enabled user
curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"user@example.com","password":"password"}'

# Response should indicate 2FA required

# 2. Check WhatsApp for code

# 3. Verify code
curl -X POST http://localhost:5000/api/auth/verify-2fa \
  -H "Content-Type: application/json" \
  -d '{"token":"123456"}'

# Should return JWT token
```

### Unit Testing

```csharp
[Fact]
public async Task Login_With2FAEnabled_ReturnsRequiresTwoFactor()
{
    // Arrange
    var user = new User { Email = "test@example.com", TwoFactorEnabled = true };

    // Act
    var result = await _authController.Login(new LoginRequest("test@example.com", "password"));

    // Assert
    var okResult = Assert.IsType<OkObjectResult>(result);
    dynamic value = okResult.Value;
    Assert.True(value.requiresTwoFactor);
}

[Fact]
public async Task Verify2FA_WithValidToken_ReturnsJWT()
{
    // Arrange
    var token = await _twoFactorService.CreateTokenAsync(userId, "whatsapp");

    // Act
    var result = await _authController.Verify2FA(new Verify2FARequest(token.Token));

    // Assert
    var okResult = Assert.IsType<OkObjectResult>(result);
    dynamic value = okResult.Value;
    Assert.NotNull(value.token);
}
```

## Troubleshooting

### Code Not Received

**Causes:**
- WhatsApp service disconnected
- Phone number incorrect
- Rate limiting

**Solutions:**
- Check WhatsApp session status
- Verify phone number format
- Wait and retry

### Code Invalid

**Causes:**
- Token expired (>10 minutes)
- Token already used
- Typo in manual entry

**Solutions:**
- Request new code
- Use link instead of manual entry
- Check expiration time

### Can't Enable 2FA

**Causes:**
- No phone number provided
- No WhatsApp session linked

**Solutions:**
- Add phone number in account settings
- Link WhatsApp account first

## Best Practices

1. **Backup Codes**: Consider implementing backup codes for when WhatsApp unavailable
2. **Remember Device**: Offer "Trust this device" option
3. **Fallback Method**: Always offer email 2FA as backup
4. **Clear Instructions**: Guide users through 2FA setup
5. **Support**: Provide easy way to disable 2FA if user loses phone access

## Future Enhancements

- **Backup codes**: One-time recovery codes
- **Biometric verification**: Fingerprint/face ID on mobile
- **Authenticator app**: TOTP-based authentication
- **Remember device**: Skip 2FA on trusted devices
- **SMS fallback**: Send code via SMS if WhatsApp fails
