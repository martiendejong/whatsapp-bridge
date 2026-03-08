@echo off
echo Deploying WhatsApp Bridge Backend with External API...
echo.

REM Stop IIS
echo [1/4] Stopping IIS...
net stop w3svc

REM Wait a moment
timeout /t 3 /nobreak > nul

REM Copy files
echo [2/4] Copying files...
xcopy /E /Y /I "E:\projects\whatsappbridge\Backend\WhatsAppBridge.API\bin\publish\*" "C:\inetpub\whatsappbridge-api\"

REM Start IIS
echo [3/4] Starting IIS...
net start w3svc

REM Wait for IIS to start
timeout /t 3 /nobreak > nul

echo [4/4] Testing API...
curl -s http://localhost:5000/health > nul
if %errorlevel% == 0 (
    echo.
    echo ================================
    echo   Deployment SUCCESS!
    echo ================================
    echo.
    echo Backend is running with External API support
    echo Test at: https://whatsapp.wreckingball.ai/api/external/whatsapp/sessions
) else (
    echo.
    echo WARNING: API not responding yet, give it a few more seconds
)

echo.
pause
