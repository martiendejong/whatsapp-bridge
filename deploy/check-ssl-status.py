#!/usr/bin/env python3
"""Check SSL and API configuration"""

import paramiko
import sys

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"

def run_cmd(ssh, cmd):
    """Execute command and return clean output"""
    stdin, stdout, stderr = ssh.exec_command(cmd)
    out = stdout.read().decode('utf-8', errors='ignore').strip()
    err = stderr.read().decode('utf-8', errors='ignore').strip()
    return out, err

def main():
    print("="*70)
    print("SSL & API Configuration Check")
    print("="*70)

    try:
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD, timeout=10)
        print(f"Connected to {SSH_HOST}\n")

        # Check IIS bindings
        print("IIS Bindings:")
        out, _ = run_cmd(ssh, r'powershell -Command "Get-WebBinding | Where-Object { $_.bindingInformation -like \"*whatsapp*\" } | Format-Table protocol,bindingInformation,sslFlags -AutoSize | Out-String -Width 200"')
        print(out if out else "  No WhatsApp bindings found")

        # Check listening ports
        print("\nListening Ports (80, 443, 5000, 5001):")
        out, _ = run_cmd(ssh, 'netstat -ano | findstr "LISTENING" | findstr ":80 :443 :5000 :5001"')
        if out:
            for line in out.split('\n')[:10]:
                if line.strip():
                    print(f"  {line.strip()}")
        else:
            print("  No ports listening")

        # Check if SSL certificate installed
        print("\nSSL Certificate:")
        out, _ = run_cmd(ssh, r'powershell -Command "Get-ChildItem -Path Cert:\LocalMachine\WebHosting | Where-Object { $_.Subject -like \"*whatsapp*\" } | Select-Object Subject, NotAfter | Out-String"')
        print(out if out else "  No SSL certificate found for whatsapp.wreckingball.ai")

        # Check IIS sites
        print("\nIIS Sites:")
        out, _ = run_cmd(ssh, r'powershell -Command "Get-Website | Format-Table Name,State,@{Name=\"Bindings\";Expression={$_.bindings.Collection.bindingInformation}} -AutoSize | Out-String -Width 200"')
        print(out if out else "  Cannot query IIS sites")

        # Check frontend build config
        print("\nFrontend API Configuration:")
        out, _ = run_cmd(ssh, r'powershell -Command "Get-Content C:\inetpub\whatsappbridge-web\assets\*.js | Select-String -Pattern \"VITE_API_URL|http.*:5000|http.*:5001\" | Select-Object -First 3"')
        if out:
            for line in out.split('\n')[:3]:
                if line.strip():
                    print(f"  {line.strip()}")
        else:
            print("  Cannot read frontend config")

        print("\n" + "="*70)
        print("Check complete")
        print("="*70)

        ssh.close()

    except Exception as e:
        print(f"\nError: {e}")
        sys.exit(1)

if __name__ == "__main__":
    main()
