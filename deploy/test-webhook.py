#!/usr/bin/env python3
"""Test the webhook endpoint"""

import requests

print("Testing webhook endpoint...")

# Create a test session first
print("\n1. Creating test session...")
login_response = requests.post(
    "https://whatsapp.wreckingball.ai:5001/api/auth/login",
    json={"email": "test@whatsapp.com", "password": "Test123!"},
    verify=False,
    timeout=10
)

token = login_response.json()['token']

create_response = requests.post(
    "https://whatsapp.wreckingball.ai:5001/api/whatsapp/sessions/create",
    headers={"Authorization": f"Bearer {token}"},
    verify=False,
    timeout=60
)

session_data = create_response.json()
session_id = session_data['sessionId']
print(f"   Session created: {session_id}")
print(f"   Initial status: {session_data['status']}")

# Simulate webhook call (as if WhatsApp Service is calling it)
print("\n2. Simulating webhook (session authenticated)...")
webhook_response = requests.post(
    "http://whatsapp.wreckingball.ai:5000/api/whatsapp/webhook/status",
    json={
        "sessionId": session_id,
        "status": "authenticated",
        "phoneNumber": None
    },
    timeout=10
)

print(f"   Webhook status: {webhook_response.status_code}")

# Check if status updated
print("\n3. Checking if status was updated...")
check_response = requests.get(
    f"https://whatsapp.wreckingball.ai:5001/api/whatsapp/sessions/{session_id}/qr",
    headers={"Authorization": f"Bearer {token}"},
    verify=False,
    timeout=10
)

updated_status = check_response.json()['status']
print(f"   Updated status: {updated_status}")

if updated_status == "authenticated":
    print("\n[SUCCESS] Webhook is working!")
else:
    print(f"\n[FAIL] Status didn't update (still {updated_status})")
