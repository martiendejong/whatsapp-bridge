using System.Net;
using System.Net.Mail;

namespace WhatsAppBridge.API.Services;

public class EmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Send 2FA token via email
    /// </summary>
    public async Task<bool> SendTwoFactorTokenAsync(string toEmail, string token)
    {
        try
        {
            var smtpHost = _configuration["Email:SmtpHost"] ?? "smtp.gmail.com";
            var smtpPort = int.Parse(_configuration["Email:SmtpPort"] ?? "587");
            var fromEmail = _configuration["Email:FromEmail"] ?? throw new InvalidOperationException("Email:FromEmail not configured");
            var fromPassword = _configuration["Email:FromPassword"] ?? throw new InvalidOperationException("Email:FromPassword not configured");
            var fromName = _configuration["Email:FromName"] ?? "WhatsApp Bridge";

            var baseUrl = _configuration["BaseUrl"] ?? "http://localhost:5173";
            var verificationLink = $"{baseUrl}/verify-2fa?token={token}";

            var subject = "Your WhatsApp Bridge Login Code";
            var body = $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .code {{
            background-color: #f4f4f4;
            border: 2px solid #ddd;
            padding: 15px;
            font-size: 24px;
            font-weight: bold;
            text-align: center;
            letter-spacing: 5px;
            margin: 20px 0;
        }}
        .button {{
            display: inline-block;
            background-color: #25D366;
            color: white;
            padding: 12px 30px;
            text-decoration: none;
            border-radius: 5px;
            margin: 20px 0;
        }}
        .footer {{ margin-top: 30px; font-size: 12px; color: #666; }}
    </style>
</head>
<body>
    <div class=""container"">
        <h2>WhatsApp Bridge Login Verification</h2>
        <p>Your verification code is:</p>
        <div class=""code"">{token}</div>
        <p>This code will expire in <strong>10 minutes</strong>.</p>
        <p>Or click the button below to verify instantly:</p>
        <a href=""{verificationLink}"" class=""button"">Verify Login</a>
        <div class=""footer"">
            <p>If you didn't request this code, please ignore this email.</p>
            <p>For security reasons, never share this code with anyone.</p>
        </div>
    </div>
</body>
</html>";

            using var smtpClient = new SmtpClient(smtpHost, smtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(fromEmail, fromPassword)
            };

            var mailMessage = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            mailMessage.To.Add(toEmail);

            await smtpClient.SendMailAsync(mailMessage);

            _logger.LogInformation("2FA token email sent to {Email}", toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending 2FA token email to {Email}", toEmail);
            return false;
        }
    }
}
