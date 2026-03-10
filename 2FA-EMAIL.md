# 2FA via Email

WhatsApp Bridge supports Two-Factor Authentication (2FA) via email, providing an additional security layer for user logins.

## ⚠️ Dependencies

**This feature requires PR #10 (2FA via WhatsApp) to be merged first**, as it builds upon the core 2FA infrastructure established there.

## Features

- **Email-Based 2FA**: Receive verification codes via email
- **HTML Email Templates**: Professional, styled emails
- **Clickable Links**: Click a link in the email to verify instantly
- **Manual Token Entry**: Paste the 6-digit code manually
- **Configurable SMTP**: Works with any SMTP server (Gmail, Outlook, SendGrid, etc.)
- **Default Method**: Email is the default 2FA method

## How It Works

### 1. Login Flow with Email 2FA

When 2FA is enabled with email method:

1. User enters email and password
2. If credentials valid, system generates 6-digit code
3. Code sent via email with HTML template
4. User receives email with code and verification link
5. User clicks link OR enters code manually
6. Upon verification, JWT token issued and user logged in

### 2. Email Template

Professional HTML email with:
- Large, easy-to-read code display
- Green "Verify Login" button with clickable link
- 10-minute expiration warning
- Security reminder footer

## Configuration

### appsettings.json

```json
{
  "BaseUrl": "https://your-domain.com",
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "FromEmail": "noreply@your-domain.com",
    "FromPassword": "your-app-password",
    "FromName": "WhatsApp Bridge"
  }
}
```

### Gmail Configuration

**App Password Setup:**

1. Enable 2-Step Verification in Google Account
2. Go to https://myaccount.google.com/apppasswords
3. Generate app password for "Mail"
4. Use this password in `FromPassword` (not your regular Gmail password)

### Other SMTP Providers

**Outlook/Office 365:**
```json
{
  "SmtpHost": "smtp-mail.outlook.com",
  "SmtpPort": "587"
}
```

**SendGrid:**
```json
{
  "SmtpHost": "smtp.sendgrid.net",
  "SmtpPort": "587",
  "FromEmail": "apikey",
  "FromPassword": "your-sendgrid-api-key"
}
```

**AWS SES:**
```json
{
  "SmtpHost": "email-smtp.us-east-1.amazonaws.com",
  "SmtpPort": "587",
  "FromEmail": "verified@your-domain.com",
  "FromPassword": "your-ses-smtp-password"
}
```

## API Usage

### Enable Email 2FA

```bash
PUT /api/auth/enable-2fa
Authorization: Bearer YOUR_JWT_TOKEN
Content-Type: application/json

{
  "method": "email"
}
```

### Login with Email 2FA

**Step 1: Initial Login**

```bash
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Response:**

```json
{
  "requiresTwoFactor": true,
  "twoFactorMethod": "email",
  "userId": 1,
  "message": "A verification code has been sent to your email."
}
```

**Step 2: Check Email and Verify**

```bash
POST /api/auth/verify-2fa
Content-Type: application/json

{
  "token": "123456"
}
```

**Response:**

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

## Email Template Preview

```html
<!DOCTYPE html>
<html>
<body>
  <div style="max-width: 600px; margin: 0 auto; padding: 20px;">
    <h2>WhatsApp Bridge Login Verification</h2>
    <p>Your verification code is:</p>
    <div style="background: #f4f4f4; border: 2px solid #ddd;
                padding: 15px; font-size: 24px; font-weight: bold;
                text-align: center; letter-spacing: 5px;">
      123456
    </div>
    <p>This code will expire in <strong>10 minutes</strong>.</p>
    <a href="https://your-domain.com/verify-2fa?token=123456"
       style="display: inline-block; background-color: #25D366;
              color: white; padding: 12px 30px; text-decoration: none;">
      Verify Login
    </a>
    <p>If you didn't request this code, please ignore this email.</p>
  </div>
</body>
</html>
```

## Frontend Implementation

Same as WhatsApp 2FA - the verification flow is identical:

```typescript
async function login(email: string, password: string) {
  const response = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password })
  });

  const data = await response.json();

  if (data.requiresTwoFactor) {
    // Show message based on method
    if (data.twoFactorMethod === 'email') {
      showMessage('Check your email for verification code');
    } else {
      showMessage('Check your WhatsApp for verification code');
    }
    navigate('/verify-2fa');
  } else {
    localStorage.setItem('token', data.token);
    navigate('/dashboard');
  }
}
```

## Error Handling

### Email Send Failure

```json
{
  "message": "Failed to send email 2FA code. Please check your email configuration."
}
```

**Common Causes:**
- SMTP credentials incorrect
- SMTP server unreachable
- From email not verified (for some providers)
- App password not generated (for Gmail)

### Invalid Token

```json
{
  "message": "Invalid or expired verification code"
}
```

## Security Considerations

### SMTP Credentials

**Best Practices:**
- Store SMTP password in environment variables, not config files
- Use app passwords, not account passwords
- Use SendGrid/AWS SES for production (more reliable than Gmail)
- Enable SPF and DKIM records for your domain

**Environment Variables:**

```bash
# .env
EMAIL__FROMEMAIL=noreply@your-domain.com
EMAIL__FROMPASSWORD=your-app-password
```

```csharp
// Program.cs - reads from environment
builder.Configuration.AddEnvironmentVariables();
```

### Rate Limiting

Prevent email bombing:

```csharp
// Limit 2FA email sends per user
if (user.LastTwoFactorEmailSent.HasValue &&
    (DateTime.UtcNow - user.LastTwoFactorEmailSent.Value).TotalMinutes < 1)
{
    return BadRequest(new { message = "Please wait before requesting another code" });
}
```

### Email Verification

For production, verify user emails before enabling 2FA:

```csharp
if (!user.EmailVerified && user.TwoFactorMethod == "email")
{
    return BadRequest(new { message = "Please verify your email first" });
}
```

## Comparison: Email vs WhatsApp 2FA

| Feature | Email 2FA | WhatsApp 2FA |
|---------|-----------|--------------|
| **Setup** | No phone needed | Requires WhatsApp session |
| **Delivery** | Near instant | Requires active WhatsApp service |
| **Reliability** | Depends on SMTP | Depends on WhatsApp connection |
| **User Preference** | More universal | More secure (phone-based) |
| **Cost** | Free (Gmail) or paid (SendGrid) | Free (uses existing WhatsApp) |

## Testing

### Manual Testing

```bash
# 1. Enable email 2FA for user
curl -X PUT http://localhost:5000/api/auth/enable-2fa \
  -H "Authorization: Bearer TOKEN" \
  -d '{"method":"email"}'

# 2. Login
curl -X POST http://localhost:5000/api/auth/login \
  -d '{"email":"user@example.com","password":"password"}'

# 3. Check email inbox for code

# 4. Verify
curl -X POST http://localhost:5000/api/auth/verify-2fa \
  -d '{"token":"123456"}'
```

### Unit Testing

```csharp
[Fact]
public async Task EmailService_SendsEmail_Successfully()
{
    // Arrange
    var emailService = new EmailService(_configuration, _logger);

    // Act
    var result = await emailService.SendTwoFactorTokenAsync("test@example.com", "123456");

    // Assert
    Assert.True(result);
}

[Fact]
public async Task Login_WithEmailTwoFactor_SendsEmail()
{
    // Arrange
    var user = new User { TwoFactorEnabled = true, TwoFactorMethod = "email" };

    // Act
    var result = await _authController.Login(new LoginRequest("test@example.com", "password"));

    // Assert
    var okResult = Assert.IsType<OkObjectResult>(result);
    dynamic value = okResult.Value;
    Assert.Equal("email", value.twoFactorMethod);
    // Verify email was sent
    _emailServiceMock.Verify(x => x.SendTwoFactorTokenAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
}
```

## Troubleshooting

### Emails Not Received

**Check:**
1. **Spam folder** - 2FA emails often flagged as spam
2. **SMTP credentials** - Test with a simple email first
3. **Firewall** - Port 587 must be open
4. **Email provider** - Some block SMTP relay

**Gmail Specific:**
- Enable "Less secure app access" OR use app password
- Check Google account security settings

### Emails Delayed

**Causes:**
- SMTP server slow
- Email provider's queue
- SPF/DKIM not configured

**Solutions:**
- Use SendGrid/AWS SES for better delivery
- Configure SPF and DKIM records
- Monitor SMTP logs

### HTML Not Rendering

**Cause:** Some email clients don't support HTML

**Solution:** EmailService already handles this - HTML is used, plain text fallback automatic

## Production Recommendations

### Use Professional Email Service

**Don't use Gmail in production.** Use:

1. **SendGrid** - 100 emails/day free
2. **AWS SES** - $0.10 per 1000 emails
3. **Mailgun** - 5000 emails/month free
4. **Postmark** - Excellent deliverability

### Configure DNS Records

**SPF Record:**
```
v=spf1 include:_spf.google.com ~all
```

**DKIM:** Configure with your email provider

**DMARC:**
```
v=DMARC1; p=quarantine; rua=mailto:dmarc@your-domain.com
```

### Monitor Email Delivery

Log all email sends:

```csharp
_logger.LogInformation("2FA email sent to {Email}, MessageId: {MessageId}", email, messageId);
```

Track bounces and failures.

## Best Practices

1. **Verify Email First**: Require email verification before enabling 2FA
2. **Rate Limiting**: Max 3 codes per 15 minutes
3. **Branding**: Use company logo in email template
4. **Clear Subject**: "Your [Company] Login Code" - no generic subjects
5. **Fallback**: Offer WhatsApp 2FA if email fails
6. **Security Footer**: Remind users never to share codes
7. **Unsubscribe**: Not applicable for security emails (no unsubscribe needed)

## Future Enhancements

- Custom email templates per company
- Email template editor in admin panel
- Multilingual email templates
- SMS fallback if email fails
- Push notifications as alternative

---

**Note:** This feature builds upon the 2FA infrastructure from PR #10. Ensure that PR is merged before deploying this feature.
