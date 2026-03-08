#!/usr/bin/env python3
"""Test WhatsApp session creation API"""

import requests
import json
import time

# Get user token first (login)
print("Step 1: Login to get token...")
login_response = requests.post(
    "https://whatsapp.wreckingball.ai:5001/api/auth/login",
    json={"email": "finally@wreckingball.ai", "password": "Finally123!"},
    verify=False
)

if login_response.status_code == 200:
    token = login_response.json()['token']
    print(f"  Token: {token[:20]}...")
else:
    print(f"  Login failed: {login_response.status_code}")
    exit(1)

# Create WhatsApp session
print("\nStep 2: Create WhatsApp session...")
headers = {
    "Authorization": f"Bearer {token}",
    "Content-Type": "application/json"
}

create_response = requests.post(
    "https://whatsapp.wreckingball.ai:5001/api/whatsapp/sessions/create",
    headers=headers,
    verify=False
)

print(f"  Status: {create_response.status_code}")
print(f"  Response: {create_response.text}")

if create_response.status_code == 200:
    session = create_response.json()
    session_id = session.get('sessionId')

    if session_id:
        print(f"\nStep 3: Get QR code for session {session_id}...")

        # Poll for QR code
        for i in range(5):
            time.sleep(2)

            qr_response = requests.get(
                f"https://whatsapp.wreckingball.ai:5001/api/whatsapp/sessions/{session_id}/qr",
                headers=headers,
                verify=False
            )

            print(f"  Attempt {i+1}: Status {qr_response.status_code}")

            if qr_response.status_code == 200:
                qr_data = qr_response.json()
                print(f"  QR Data: {json.dumps(qr_data, indent=2)}")

                if qr_data.get('qr'):
                    print("\n  QR code is available!")
                    print(f"  QR length: {len(qr_data['qr'])} characters")
                    break
                else:
                    print(f"  Status: {qr_data.get('status', 'unknown')}")
            else:
                print(f"  Error: {qr_response.text}")

        # Get all sessions
        print("\nStep 4: Get all sessions...")
        sessions_response = requests.get(
            "https://whatsapp.wreckingball.ai:5001/api/whatsapp/sessions",
            headers=headers,
            verify=False
        )

        if sessions_response.status_code == 200:
            sessions = sessions_response.json()
            print(f"  Sessions: {json.dumps(sessions, indent=2)}")
