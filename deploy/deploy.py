import os
import time
import shutil
import subprocess
from pathlib import Path

print("WhatsApp Bridge Deployment Script")
print("=" * 50)

# Paths
source_dir = Path("E:/projects/whatsappbridge/Backend/WhatsAppBridge.API/bin/publish")
target_dir = Path("C:/inetpub/whatsappbridge-api")
app_offline = target_dir / "app_offline.htm"

# Step 1: Create app_offline.htm
print("\n[1/5] Creating app_offline.htm...")
app_offline_content = """<!DOCTYPE html>
<html>
<head><title>Updating...</title></head>
<body style="font-family:Arial;text-align:center;padding:50px;">
    <h1>⚙️ Update in Progress</h1>
    <p>WhatsApp Bridge API is being updated. This takes ~10 seconds.</p>
</body>
</html>"""

try:
    app_offline.write_text(app_offline_content, encoding='utf-8')
    print("   ✓ App is now offline (files unlocked)")
except PermissionError:
    print("   ✗ Permission denied - run this script as Administrator!")
    exit(1)

# Step 2: Wait for IIS to release locks
print("\n[2/5] Waiting for IIS to release file locks...")
time.sleep(5)

# Step 3: Copy files
print("\n[3/5] Copying new files...")
copied = 0
failed = 0

for item in source_dir.rglob('*'):
    if item.is_file():
        relative_path = item.relative_to(source_dir)
        target_path = target_dir / relative_path

        try:
            target_path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, target_path)
            copied += 1
        except Exception as e:
            failed += 1
            if failed <= 3:  # Only show first 3 errors
                print(f"   ! Failed to copy {relative_path}: {e}")

print(f"   ✓ Copied: {copied} files")
if failed > 0:
    print(f"   ! Failed: {failed} files")

# Step 4: Remove app_offline.htm
print("\n[4/5] Removing app_offline.htm...")
time.sleep(2)
try:
    app_offline.unlink()
    print("   ✓ App is starting up...")
except Exception as e:
    print(f"   ! Error removing app_offline.htm: {e}")

# Step 5: Wait and test
print("\n[5/5] Waiting for app to start...")
time.sleep(5)

print("\n" + "=" * 50)
print("Testing API...")
try:
    result = subprocess.run(
        ["powershell", "-Command", "Invoke-WebRequest -Uri http://localhost:5000/swagger -UseBasicParsing -TimeoutSec 10"],
        capture_output=True,
        text=True,
        timeout=15
    )

    if "200" in result.stdout or "StatusCode" in result.stdout:
        print("\n✓✓✓ DEPLOYMENT SUCCESS! ✓✓✓")
        print("\nBackend deployed with External API support")
        print("Production URL: https://whatsapp.wreckingball.ai")
        print("\nTest External API:")
        print('  curl https://whatsapp.wreckingball.ai/api/external/whatsapp/sessions \\')
        print('    -H "X-Api-Token: YOUR_TOKEN" \\')
        print('    -H "X-Email: YOUR_EMAIL"')
    else:
        print("\n⚠ WARNING: API not responding yet. Give it 10-15 more seconds.")
        print(f"Output: {result.stdout}")
except Exception as e:
    print(f"\n⚠ Could not test API: {e}")
    print("API might still be starting up - check manually in a moment")

print("\n" + "=" * 50)
