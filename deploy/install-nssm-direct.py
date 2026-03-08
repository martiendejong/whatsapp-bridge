#!/usr/bin/env python3
"""Install NSSM directly by uploading the binary"""

import paramiko
import os
import urllib.request
import zipfile
import shutil

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

print("="*70)
print("Installing NSSM (Direct Binary Upload)")
print("="*70)

# Download NSSM locally first
nssm_url = "https://github.com/kirillkovalenko/nssm/releases/download/2.24-101-g897c7ad/nssm-2.24-101-g897c7ad.zip"
local_zip = "C:\\Temp\\nssm.zip"
local_extract = "C:\\Temp\\nssm"

print("\n1. Downloading NSSM from GitHub...")
try:
    os.makedirs("C:\\Temp", exist_ok=True)
    urllib.request.urlretrieve(nssm_url, local_zip)
    print(f"   Downloaded to {local_zip}")
except Exception as e:
    print(f"   ERROR: {e}")
    print("   Trying alternative source...")
    # Fallback: use pre-existing file if available
    if not os.path.exists(local_zip):
        print("   FAILED: Cannot download NSSM")
        exit(1)

print("\n2. Extracting...")
try:
    if os.path.exists(local_extract):
        shutil.rmtree(local_extract)
    with zipfile.ZipFile(local_zip, 'r') as zip_ref:
        zip_ref.extractall(local_extract)

    # Find nssm.exe
    nssm_exe = None
    for root, dirs, files in os.walk(local_extract):
        if 'nssm.exe' in files and 'win64' in root:
            nssm_exe = os.path.join(root, 'nssm.exe')
            break

    if not nssm_exe:
        print("   ERROR: nssm.exe not found in zip")
        exit(1)

    print(f"   Found: {nssm_exe}")

except Exception as e:
    print(f"   ERROR: {e}")
    exit(1)

print("\n3. Uploading to server...")
try:
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)

    sftp = ssh.open_sftp()

    # Create C:\nssm directory
    try:
        sftp.mkdir('/C:/nssm')
    except:
        pass  # Already exists

    # Upload nssm.exe
    remote_path = '/C:/nssm/nssm.exe'
    sftp.put(nssm_exe, remote_path)
    sftp.close()

    print(f"   Uploaded to C:\\nssm\\nssm.exe")

    # Add to system PATH
    print("\n4. Adding to system PATH...")
    cmd = r'powershell -Command "$path = [Environment]::GetEnvironmentVariable(\"Path\", \"Machine\"); if ($path -notlike \"*C:\nssm*\") { [Environment]::SetEnvironmentVariable(\"Path\", \"$path;C:\nssm\", \"Machine\"); Write-Host \"Added to PATH\" } else { Write-Host \"Already in PATH\" }"'

    stdin, stdout, stderr = ssh.exec_command(cmd)
    print(f"   {stdout.read().decode('utf-8', errors='ignore').strip()}")

    # Verify
    print("\n5. Verifying installation...")
    stdin, stdout, stderr = ssh.exec_command('C:\\nssm\\nssm.exe version')
    output = stdout.read().decode('utf-8', errors='ignore').strip()

    if output:
        print(f"   NSSM version: {output.split()[0] if output else 'installed'}")
        print("\n" + "="*70)
        print("NSSM installed successfully!")
        print("="*70)
    else:
        print("   WARNING: Could not verify NSSM")

    ssh.close()

except Exception as e:
    print(f"\n   ERROR: {e}")
    import traceback
    traceback.print_exc()
    exit(1)

# Cleanup
print("\n6. Cleaning up local files...")
try:
    if os.path.exists(local_zip):
        os.remove(local_zip)
    if os.path.exists(local_extract):
        shutil.rmtree(local_extract)
    print("   Done")
except:
    pass

print("\nNSSM is ready! You can now proceed with service installation.")
