#!/usr/bin/env python3
"""Test API from local machine"""

import requests
import json

def test_endpoint(url, data):
    print(f"\nTesting: {url}")
    try:
        response = requests.post(
            url,
            json=data,
            headers={"Content-Type": "application/json"},
            timeout=10,
            verify=False  # Skip SSL verification for self-signed cert
        )
        print(f"  Status: {response.status_code}")
        print(f"  Response: {response.text[:200]}")
        return response.status_code < 500
    except requests.exceptions.RequestException as e:
        print(f"  Error: {e}")
        return False

print("="*70)
print("Testing WhatsApp Bridge API Endpoints")
print("="*70)

# Test data
data = {
    "email": "test@example.com",
    "password": "Test123!"
}

# Test HTTPS port 5001
success_https = test_endpoint("https://whatsapp.wreckingball.ai:5001/api/auth/register", data)

# Test HTTP port 5000
success_http = test_endpoint("http://whatsapp.wreckingball.ai:5000/api/auth/register", data)

print("\n" + "="*70)
if success_https or success_http:
    print("SUCCESS! API is responding")
    if success_https:
        print("  HTTPS (port 5001): Working")
    if success_http:
        print("  HTTP (port 5000): Working")
else:
    print("FAILED: API not responding on either port")
print("="*70)
