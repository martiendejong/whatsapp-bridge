#!/usr/bin/env python3
"""Test reverse proxy signup"""

import requests
import json

# Test via reverse proxy
url = "https://whatsapp.wreckingball.ai/api/auth/register"
data = {
    "email": "proxytest2@test.com",
    "password": "Test123!"
}

print("Testing via reverse proxy...")
print(f"URL: {url}")
print(f"Data: {json.dumps(data)}")
print()

try:
    response = requests.post(
        url,
        json=data,
        headers={"Content-Type": "application/json"},
        timeout=15,
        verify=False
    )
    print(f"Status: {response.status_code}")
    print(f"Headers: {dict(response.headers)}")
    print(f"Body: {response.text}")
except Exception as e:
    print(f"Error: {e}")
    import traceback
    traceback.print_exc()
