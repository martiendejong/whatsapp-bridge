@echo off
REM Quick SSL Setup - Run this after DNS is live

echo ============================================================
echo WhatsApp Bridge - SSL Certificate Setup
echo ============================================================
echo.
echo This will install a free Let's Encrypt SSL certificate
echo for whatsapp.wreckingball.ai
echo.
echo Prerequisites:
echo   - DNS A record added (whatsapp -> 85.215.217.154)
echo   - DNS propagated (check with: nslookup whatsapp.wreckingball.ai)
echo   - Port 80 accessible from internet
echo.
pause

echo.
echo Running SSL installation...
python install-ssl-remote.py

if %errorlevel% equ 0 (
    echo.
    echo ============================================================
    echo SUCCESS! SSL Certificate Installed
    echo ============================================================
    echo.
    echo Your application is now secure:
    echo   https://whatsapp.wreckingball.ai
    echo   https://whatsapp.wreckingball.ai:5001/swagger
    echo.
    echo Certificate will auto-renew every 60 days.
    echo.
) else (
    echo.
    echo ============================================================
    echo ERROR: SSL Installation Failed
    echo ============================================================
    echo.
    echo Common issues:
    echo   - DNS not yet propagated (wait 5-30 minutes)
    echo   - Port 80 blocked by firewall
    echo   - Server not accessible from internet
    echo.
    echo Check SSL_INSTALLATION_GUIDE.md for troubleshooting
    echo.
)

pause
