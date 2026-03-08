#!/usr/bin/env python3
"""End-to-end test of the WhatsApp QR code flow"""

import requests
import json
import time

BASE_URL = "https://whatsapp.wreckingball.ai:5001"

print("="*70)
print("End-to-End WhatsApp QR Code Test")
print("="*70)

# 1. Register/Login
print("\n1. Trying to log in...")
try:
    login_response = requests.post(
        f"{BASE_URL}/api/auth/login",
        json={"email": "test@whatsapp.com", "password": "Test123!"},
        verify=False  # SSL verification disabled for self-signed cert
    )

    if login_response.status_code == 200:
        token = login_response.json()['token']
        print(f"   Logged in successfully")
    elif login_response.status_code == 401:
        print(f"   Login failed, registering new account...")
        reg_response = requests.post(
            f"{BASE_URL}/api/auth/register",
            json={"email": "test@whatsapp.com", "password": "Test123!"},
            verify=False
        )
        if reg_response.status_code == 200:
            token = reg_response.json()['token']
            print(f"   Registered and logged in successfully")
        else:
            print(f"   Registration failed: {reg_response.status_code}")
            print(f"   Response: {reg_response.text}")
            exit(1)
    else:
        print(f"   Login failed: {login_response.status_code}")
        print(f"   Response: {login_response.text}")
        exit(1)
except Exception as e:
    print(f"   ERROR: {e}")
    exit(1)

# 2. Create WhatsApp session
print("\n2. Creating WhatsApp session...")
headers = {
    "Authorization": f"Bearer {token}",
    "Content-Type": "application/json"
}

try:
    create_response = requests.post(
        f"{BASE_URL}/api/whatsapp/sessions/create",
        headers=headers,
        verify=False,
        timeout=60  # Give it time to generate QR
    )

    print(f"   Status: {create_response.status_code}")

    if create_response.status_code == 200:
        data = create_response.json()
        print(f"   Session ID: {data.get('sessionId')}")
        print(f"   QR Code: {'YES - ' + str(len(data.get('qrCode', ''))) + ' chars' if data.get('qrCode') else 'NO QR CODE'}")
        print(f"   Status: {data.get('status')}")

        if data.get('qrCode'):
            print("\n" + "="*70)
            print("SUCCESS! QR Code generated!")
            print("="*70)
            print("\nThe QR code is a base64 data URL that can be displayed as an image.")
            print("Frontend should now show the QR code when you click 'Connect WhatsApp'.")
            print("\n" + "="*70)
        else:
            print("\n" + "="*70)
            print("FAILED - No QR code in response")
            print("="*70)
            print(f"Full response: {json.dumps(data, indent=2)}")
    else:
        print(f"   FAILED")
        print(f"   Response: {create_response.text}")

except Exception as e:
    print(f"   ERROR: {e}")
    import traceback
    traceback.print_exc()

print()
