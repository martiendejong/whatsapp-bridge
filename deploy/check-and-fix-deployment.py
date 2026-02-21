#!/usr/bin/env python3
"""Check DNS propagation and fix deployment issues"""

import paramiko
import socket
import time

SSH_HOST = "85.215.217.154"
SSH_USER = "administrator"
SSH_PASSWORD = "3WsXcFr$7YhNmKi*"
DOMAIN = "whatsapp.wreckingball.ai"

def check_dns():
    """Check if DNS has propagated"""
    print("\n" + "="*70)
    print("DNS Propagation Check")
    print("="*70)

    try:
        ip = socket.gethostbyname(DOMAIN)
        print(f"[OK] DNS resolved: {DOMAIN} -> {ip}")

        if ip == SSH_HOST:
            print(f"[OK] DNS points to correct server: {SSH_HOST}")
            return True
        else:
            print(f"[FAIL] DNS points to {ip}, expected {SSH_HOST}")
            return False
    except socket.gaierror:
        print(f"[FAIL] DNS not yet propagated for {DOMAIN}")
        return False

def check_server_status(ssh):
    """Check what's actually installed on the server"""
    print("\n" + "="*70)
    print("Server Status Check")
    print("="*70)

    issues = []

    # Check if project files exist
    print("\n[1/6] Checking project files...")
    stdin, stdout, stderr = ssh.exec_command('dir C:\\whatsappbridge /B', get_pty=True)
    output = stdout.read().decode('utf-8', errors='ignore')

    if 'Backend' in output and 'Frontend' in output and 'WhatsAppService' in output:
        print("  [OK] Project files present")
    else:
        print("  [FAIL] Project files missing or incomplete")
        issues.append("project_files")

    # Check .NET
    print("\n[2/6] Checking .NET...")
    stdin, stdout, stderr = ssh.exec_command('dotnet --version', get_pty=True)
    output = stdout.read().decode('utf-8', errors='ignore')

    if '8.0' in output or '9.0' in output:
        print(f"  [OK] .NET installed")
    else:
        print("  [FAIL] .NET not found")
        issues.append("dotnet")

    # Check Node.js
    print("\n[3/6] Checking Node.js...")
    stdin, stdout, stderr = ssh.exec_command('node --version', get_pty=True)
    output = stdout.read().decode('utf-8', errors='ignore')

    if 'v' in output:
        print(f"  [OK] Node.js installed")
    else:
        print("  [FAIL] Node.js not found")
        issues.append("nodejs")

    # Check IIS
    print("\n[4/6] Checking IIS...")
    stdin, stdout, stderr = ssh.exec_command('powershell Get-WindowsFeature Web-Server | Select-Object Installed', get_pty=True)
    output = stdout.read().decode('utf-8', errors='ignore')

    if 'True' in output:
        print("  [OK] IIS installed")
    else:
        print("  [FAIL] IIS not installed")
        issues.append("iis")

    # Check WhatsApp Service
    print("\n[5/6] Checking WhatsApp Service...")
    stdin, stdout, stderr = ssh.exec_command('sc query WhatsAppBridgeService', get_pty=True)
    output = stdout.read().decode('utf-8', errors='ignore')

    if 'RUNNING' in output:
        print("  [OK] WhatsApp Service is RUNNING")
    elif 'STOPPED' in output:
        print("  [WARN]  WhatsApp Service is STOPPED (needs to be started)")
        issues.append("whatsapp_service_stopped")
    else:
        print("  [FAIL] WhatsApp Service not installed")
        issues.append("whatsapp_service")

    # Check IIS Sites
    print("\n[6/6] Checking IIS Sites...")
    stdin, stdout, stderr = ssh.exec_command('powershell Import-Module WebAdministration; Get-Website | Where-Object { $_.Name -like "*WhatsApp*" } | Format-List Name,State,Bindings', get_pty=True)
    output = stdout.read().decode('utf-8', errors='ignore')

    if 'WhatsAppBridge' in output:
        print("  [OK] IIS sites configured")
        if 'Started' in output:
            print("  [OK] Sites are running")
        else:
            print("  [WARN]  Sites are stopped")
            issues.append("iis_sites_stopped")
    else:
        print("  [FAIL] IIS sites not configured")
        issues.append("iis_sites")

    return issues

def fix_issues(ssh, issues):
    """Fix identified issues"""
    if not issues:
        print("\n[OK] No issues found - deployment is complete!")
        return True

    print("\n" + "="*70)
    print(f"Fixing {len(issues)} issue(s)...")
    print("="*70)

    # If major components missing, run full installation
    if any(x in issues for x in ['project_files', 'whatsapp_service', 'iis_sites']):
        print("\n[*] Running complete installation script...")
        print("    This will take 15-20 minutes...")

        stdin, stdout, stderr = ssh.exec_command(
            'powershell -ExecutionPolicy Bypass -File C:\\whatsappbridge\\deploy\\INSTALL_ON_SERVER.ps1 -Domain whatsapp.wreckingball.ai',
            get_pty=True
        )

        # Print output
        for line in stdout:
            line_str = line.rstrip()
            if line_str and '?25' not in line_str and '\x1b[' not in line_str[:15]:
                print(f"    {line_str}")

        return True

    # Fix minor issues
    if 'whatsapp_service_stopped' in issues:
        print("\n[*] Starting WhatsApp Service...")
        ssh.exec_command('sc start WhatsAppBridgeService')
        print("  [OK] Service started")

    if 'iis_sites_stopped' in issues:
        print("\n[*] Starting IIS sites...")
        ssh.exec_command('powershell Import-Module WebAdministration; Get-Website | Where-Object { $_.Name -like "*WhatsApp*" } | Start-Website')
        print("  [OK] Sites started")

    return True

def verify_deployment(ssh):
    """Final verification"""
    print("\n" + "="*70)
    print("Final Verification")
    print("="*70)

    # Check ports
    print("\n[*] Checking listening ports...")
    stdin, stdout, stderr = ssh.exec_command('netstat -an | findstr "LISTENING" | findstr ":80 :3000 :5000"', get_pty=True)
    output = stdout.read().decode('utf-8', errors='ignore')

    ports_ok = True
    for port in ['80', '3000', '5000']:
        if f':{port}' in output:
            print(f"  [OK] Port {port} is listening")
        else:
            print(f"  [FAIL] Port {port} not listening")
            ports_ok = False

    return ports_ok

def main():
    print("="*70)
    print("WhatsApp Bridge - Deployment Check & Fix")
    print("="*70)

    # Check DNS
    dns_ok = check_dns()

    try:
        # Connect to server
        print("\n[*] Connecting to server...")
        ssh = paramiko.SSHClient()
        ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        ssh.connect(SSH_HOST, username=SSH_USER, password=SSH_PASSWORD)
        print(f"  [OK] Connected to {SSH_HOST}")

        # Check status
        issues = check_server_status(ssh)

        # Fix issues if any
        if issues:
            print(f"\n[WARN]  Found {len(issues)} issue(s) to fix")
            fix_issues(ssh, issues)

            # Verify again after fixes
            print("\n[*] Re-checking after fixes...")
            time.sleep(5)
            issues = check_server_status(ssh)

        # Final verification
        ports_ok = verify_deployment(ssh)

        # Summary
        print("\n" + "="*70)
        print("Deployment Status Summary")
        print("="*70)
        print(f"\nDNS Propagation: {'[OK] Ready' if dns_ok else '[FAIL] Not yet propagated'}")
        print(f"Server Status:   {'[OK] All services running' if not issues else '[WARN]  Issues found'}")
        print(f"Ports:           {'[OK] All listening' if ports_ok else '[FAIL] Some ports not responding'}")

        if dns_ok and not issues and ports_ok:
            print("\n🎉 DEPLOYMENT COMPLETE!")
            print(f"\nYour application is live at:")
            print(f"  http://{DOMAIN}")
            print(f"  http://{DOMAIN}:5000/swagger")
            print(f"\nNext step: Install SSL certificate")
            print(f"  cd E:\\projects\\whatsappbridge\\deploy")
            print(f"  setup-ssl.cmd")
        elif not dns_ok:
            print(f"\n[WAIT] Waiting for DNS propagation...")
            print(f"   Run this script again in 5-10 minutes")
        else:
            print(f"\n[WARN]  Some issues remain - check output above")

        ssh.close()

    except Exception as e:
        print(f"\n[FAIL] Error: {e}")
        import traceback
        traceback.print_exc()

if __name__ == "__main__":
    main()
