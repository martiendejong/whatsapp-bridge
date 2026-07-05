"""Final deploy: stop the JengoVPSMCPServer service (it holds Dawa.dll), swap, restart everything."""
import paramiko, requests, base64, sys, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
HOST="85.215.217.154"; h={"X-API-Key":"pm_eCvecyNvmiaVF5btRSjKkwvYZI72W7yV1RxtonC1"}
cred=requests.get("https://vault.prospergenics.com/api/projects/6/credentials/14",headers=h,timeout=15).json()
ssh=paramiko.SSHClient(); ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect(HOST,port=22,username=cred["username"],password=cred["password"],timeout=30)
def run(c):
    i,o,e=ssh.exec_command(c); return (o.read().decode(errors="replace")+e.read().decode(errors="replace")).strip()
def ps(s): return run("powershell -NoProfile -EncodedCommand "+base64.b64encode(s.encode("utf-16-le")).decode())
A=r"C:\Windows\System32\inetsrv\appcmd.exe"; P="WhatsAppBridgeAPIPool"

print("[1] Stop JengoVPSMCPServer (releases its Dawa.dll lock)...")
print("   ", ps("Stop-Service JengoVPSMCPServer -Force; Start-Sleep 2; (Get-Service JengoVPSMCPServer).Status"))
print("[2] Pool: autostart off, stop, kill w3wp...")
run(f'"{A}" set apppool "{P}" /autoStart:false /startMode:OnDemand'); print("   stop:", run(f'"{A}" stop apppool /apppool.name:{P}'))
print("   ", ps(f"Get-CimInstance Win32_Process -Filter \"Name='w3wp.exe'\" | Where-Object {{$_.CommandLine -like '*{P}*'}} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }}; 'kill done'"))
print("[3] Poll unlock + swap...")
print("   ", ps("""
$f='C:\\inetpub\\whatsappbridge-api\\Dawa.dll'; $ok=$false
for($i=0;$i -lt 30;$i++){ try{ $s=[System.IO.File]::Open($f,'Open','ReadWrite','None'); $s.Close(); $ok=$true; break }catch{ Start-Sleep -Milliseconds 1000 } }
"unlock: $(if($ok){'yes '+$i+'s'}else{'NO'})"
if($ok){ foreach($d in 'Dawa.dll','WhatsAppBridge.API.dll'){ $fin="C:\\inetpub\\whatsappbridge-api\\$d"; try{ Move-Item "$fin.new" $fin -Force -EA Stop; "swapped $d" }catch{ "FAIL ${d}: "+$_.Exception.Message.Substring(0,45) } } }
"""))
print("[4] Restore pool + restart JengoVPSMCPServer...")
run(f'"{A}" set apppool "{P}" /autoStart:true /startMode:AlwaysRunning'); print("   start:", run(f'"{A}" start apppool /apppool.name:{P}'))
print("   ", ps("Start-Service JengoVPSMCPServer; Start-Sleep 2; (Get-Service JengoVPSMCPServer).Status"))
time.sleep(6)
print("[5] Dawa.dll ts:", ps("(Get-Item 'C:\\inetpub\\whatsappbridge-api\\Dawa.dll').LastWriteTime"))
ssh.close()
