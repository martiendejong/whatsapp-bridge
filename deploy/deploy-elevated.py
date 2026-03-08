import os
import sys
import time
import shutil
import subprocess
import ctypes
from pathlib import Path

def is_admin():
    """Check if running as Administrator"""
    try:
        return ctypes.windll.shell32.IsUserAnAdmin()
    except:
        return False

def run_as_admin():
    """Re-launch this script as Administrator"""
    if is_admin():
        return True

    print("Not running as Administrator - requesting elevation...")
    script = os.path.abspath(sys.argv[0])
    params = ' '.join([script] + sys.argv[1:])

    try:
        ctypes.windll.shell32.ShellExecuteW(
            None,
            "runas",  # Request admin
            sys.executable,  # Python executable
            params,  # Script path
            None,  # Working directory
            1  # Show window
        )
        sys.exit(0)  # Exit current non-admin process
    except Exception as e:
        print(f"Failed to elevate: {e}")
        return False

# Main deployment logic
def deploy():
    print("WhatsApp Bridge Deployment Script (Elevated)")
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
    <h1>Update in Progress</h1>
    <p>WhatsApp Bridge API is being updated. This takes ~10 seconds.</p>
</body>
</html>"""

    try:
        app_offline.write_text(app_offline_content, encoding='utf-8')
        print("   [OK] App is now offline (files unlocked)")
    except PermissionError:
        print("   [FAIL] Permission denied - elevation failed!")
        input("Press Enter to exit...")
        sys.exit(1)

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

    print(f"   [OK] Copied: {copied} files")
    if failed > 0:
        print(f"   [WARN] Failed: {failed} files")

    # Step 4: Remove app_offline.htm
    print("\n[4/5] Removing app_offline.htm...")
    time.sleep(2)
    try:
        app_offline.unlink()
        print("   [OK] App is starting up...")
    except Exception as e:
        print(f"   [WARN] Error removing app_offline.htm: {e}")

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
            print("\n[SUCCESS] DEPLOYMENT COMPLETE!")
            print("\nBackend deployed with External API support")
            print("Production URL: https://whatsapp.wreckingball.ai")
            print("\nTest External API:")
            print('  curl https://whatsapp.wreckingball.ai/api/external/whatsapp/sessions \\')
            print('    -H "X-Api-Token: YOUR_TOKEN" \\')
            print('    -H "X-Email: YOUR_EMAIL"')
        else:
            print("\n[WARN] API not responding yet. Give it 10-15 more seconds.")
    except Exception as e:
        print(f"\n[WARN] Could not test API: {e}")
        print("API might still be starting up - check manually")

    print("\n" + "=" * 50)
    input("Press Enter to exit...")

if __name__ == "__main__":
    # Request admin rights if not already admin
    if not is_admin():
        run_as_admin()
    else:
        # Running as admin - do the deployment
        deploy()
