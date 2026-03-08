#!/usr/bin/env python3
import paramiko

ssh = paramiko.SSHClient()
ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
ssh.connect('85.215.217.154', username='administrator', password='3WsXcFr$7YhNmKi*')

sftp = ssh.open_sftp()
sftp.get('/C:/Services/WhatsAppBridge/index.js', 'E:/projects/whatsappbridge/WhatsAppService/index-server.js')
sftp.close()
ssh.close()

print("Downloaded to: E:/projects/whatsappbridge/WhatsAppService/index-server.js")
print("Now you can edit it locally and I'll upload it back")
