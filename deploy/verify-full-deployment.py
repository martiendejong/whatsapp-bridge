#!/usr/bin/env python3
"""Verify all components are deployed and working"""

import requests
import paramiko

print("="*70)
print("Full Deployment Verification")
print("="*70)

# 1. Check Frontend
print("\n1. Frontend (https://whatsapp.wreckingball.ai):")
try:
    response = requests.get("https://whatsapp.wreckingball.ai", verify=False, timeout=10)
    print(f"   Status: {response.status_code}")
    print(f"   Size: {len(response.content)} bytes")
    if response.status_code == 200:
        print("   [OK] Frontend is deployed and accessible")
    else:
        print("   [FAIL] Frontend returned unexpected status")
except Exception as e:
    print(f"   [ERROR] {e}")

# 2. Check Backend API
print("\n2. Backend API (https://whatsapp.wreckingball.ai:5001):")
try:
    response = requests.get("https://whatsapp.wreckingball.ai:5001/api/health", verify=False, timeout=10)
    print(f"   Status: {response.status_code}")
    if response.status_code == 200:
        print("   [OK] Backend API is running")
    else:
        print(f"   [FAIL] Unexpected status: {response.status_code}")
except Exception as e:
    print(f"   [FAIL] ERROR: {e}")

# 3. Check WhatsApp Service (from server)
print("\n3. WhatsApp Service (localhost:3000 on server):")
ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
try:
    ssh.connect('85.215.217.154', username='administrator', password='3WsXcFr$7YhNmKi*')

    stdin, stdout, stderr = ssh.exec_command(
        'powershell -Command "try { $r = Invoke-WebRequest -Uri http://localhost:3000/health -UseBasicParsing -TimeoutSec 5; Write-Host $r.StatusCode } catch { Write-Host \\"ERROR: $($_.Exception.Message)\\" }"'
    )
    result = stdout.read().decode('utf-8', errors='ignore').strip()

    if '200' in result:
        print("   [OK] WhatsApp Service is running")
    else:
        print(f"   Status: {result}")

    ssh.close()
except Exception as e:
    print(f"   [FAIL] ERROR: {e}")

# 4. Test QR Code Generation (end-to-end)
print("\n4. QR Code Generation (end-to-end test):")
try:
    # Login
    login_response = requests.post(
        "https://whatsapp.wreckingball.ai:5001/api/auth/login",
        json={"email": "test@whatsapp.com", "password": "Test123!"},
        verify=False,
        timeout=10
    )

    if login_response.status_code == 200:
        token = login_response.json()['token']

        # Create session
        headers = {"Authorization": f"Bearer {token}"}
        create_response = requests.post(
            "https://whatsapp.wreckingfall.ai:5001/api/whatsapp/sessions/create",
            headers=headers,
            verify=False,
            timeout=60
        )

        if create_response.status_code == 200:
            data = create_response.json()
            if data.get('qrCode'):
                print(f"   [OK] QR Code generated ({len(data['qrCode'])} chars)")
            else:
                print("   [FAIL] No QR code in response")
        else:
            print(f"   [FAIL] Session creation failed: {create_response.status_code}")
    else:
        print(f"   Login status: {login_response.status_code}")

except Exception as e:
    print(f"   [FAIL] ERROR: {e}")

print("\n" + "="*70)
print("Deployment Status Summary")
print("="*70)
print("\nIf all checks passed ([OK]), the system is fully deployed.")
print("Users can access: https://whatsapp.wreckingball.ai/whatsapp-sessions")
print("="*70)
