#!/usr/bin/env python3
import paramiko

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect('85.215.217.154', username='administrator', password='3WsXcFr$7YhNmKi*')

stdin, stdout, stderr = ssh.exec_command('type C:\\Services\\WhatsAppBridge\\logs\\service.log')
print(stdout.read().decode('utf-8', errors='ignore').strip()[-3000:])

ssh.close()
