"""Robust deploy: upload DLLs to temp names, stop pool, swap with retry (handles Defender lock)."""
import paramiko, requests, sys, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HOST = "85.215.217.154"
h = {"X-API-Key": "pm_eCvecyNvmiaVF5btRSjKkwvYZI72W7yV1RxtonC1"}
cred = requests.get("https://vault.prospergenics.com/api/projects/6/credentials/14", headers=h, timeout=15).json()
USER, PASS = cred["username"], cred["password"]

BUILD = r"E:\projects\whatsappbridge\Backend\WhatsAppBridge.API\bin\Release\net8.0"
DEST = "C:/inetpub/whatsappbridge-api"
DLLS = ["Dawa.dll", "WhatsAppBridge.API.dll"]
POOL = "WhatsAppBridgeAPIPool"
APPCMD = r"C:\Windows\System32\inetsrv\appcmd.exe"

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(HOST, port=22, username=USER, password=PASS, timeout=30)

def run(cmd):
    stdin, stdout, stderr = ssh.exec_command(cmd)
    return (stdout.read().decode(errors="replace") + stderr.read().decode(errors="replace")).strip()

def ps(script):
    import base64
    b64 = base64.b64encode(script.encode("utf-16-le")).decode()
    return run(f"powershell -NoProfile -EncodedCommand {b64}")

print("[1] Upload DLLs to .new temp names (pool still running — temp files aren't locked)...")
sftp = ssh.open_sftp()
for d in DLLS:
    sftp.put(f"{BUILD}\\{d}", f"{DEST}/{d}.new")
    print(f"    uploaded {d}.new")
sftp.close()

print("[2] Stop app pool via appcmd...")
print("   ", run(f'"{APPCMD}" stop apppool /apppool.name:{POOL}'))
time.sleep(4)

print("[3] Swap .new -> final with retry (waits out the Defender scan lock)...")
swap = f'''
$dest = "{DEST.replace('/', chr(92))}"
$dlls = @({",".join('"'+d+'"' for d in DLLS)})
$ok = $true
foreach ($d in $dlls) {{
  $final = Join-Path $dest $d
  $new = "$final.new"
  $done = $false
  for ($i=0; $i -lt 30; $i++) {{
    try {{
      Move-Item -Path $new -Destination $final -Force -ErrorAction Stop
      $done = $true; break
    }} catch {{ Start-Sleep -Milliseconds 1000 }}
  }}
  if ($done) {{ Write-Output "swapped $d" }} else {{ Write-Output "FAILED to swap $d (still locked)"; $ok = $false }}
}}
if ($ok) {{ "ALL SWAPPED" }} else {{ "SWAP INCOMPLETE" }}
'''
print("   ", ps(swap))

print("[4] Start app pool...")
print("   ", run(f'"{APPCMD}" start apppool /apppool.name:{POOL}'))
time.sleep(6)
print("[5] Verify DLL timestamps:")
print("   ", ps("(Get-Item 'C:\\inetpub\\whatsappbridge-api\\Dawa.dll').LastWriteTime; (Get-Item 'C:\\inetpub\\whatsappbridge-api\\WhatsAppBridge.API.dll').LastWriteTime"))

ssh.close()
print("\nDone.")
