import { makeWASocket, useMultiFileAuthState, DisconnectReason, makeCacheableSignalKeyStore, fetchLatestBaileysVersion } from '@whiskeysockets/baileys'
import { Boom } from '@hapi/boom'
import qrcode from 'qrcode'
import { createServer } from 'http'
import { readFileSync, rmSync, existsSync } from 'fs'
import { join, dirname } from 'path'
import { fileURLToPath } from 'url'
import pino from 'pino'

const __dirname = dirname(fileURLToPath(import.meta.url))
const PORT = 9292
const SESSION_DIR = join(__dirname, 'baileys-session')

// --fresh flag wipes saved session
if (process.argv.includes('--fresh') && existsSync(SESSION_DIR)) {
  rmSync(SESSION_DIR, { recursive: true })
  console.log('[Baileys] Cleared saved session.')
}

let latestQr = ''
let latestQrDataUrl = ''
let status = 'connecting'

// Minimal logger — suppress internal Baileys noise
const logger = pino({ level: 'silent' })

async function startBaileys() {
  const { state, saveCreds } = await useMultiFileAuthState(SESSION_DIR)
  const { version } = await fetchLatestBaileysVersion()

  const sock = makeWASocket({
    version,
    auth: {
      creds: state.creds,
      keys: makeCacheableSignalKeyStore(state.keys, logger),
    },
    logger,
    printQRInTerminal: false,
    browser: ['Ubuntu', 'Chrome', '20.0.04'],
    syncFullHistory: true,
  })

  sock.ev.on('creds.update', saveCreds)

  sock.ev.on('connection.update', async (update) => {
    const { connection, lastDisconnect, qr } = update

    if (qr) {
      latestQr = qr
      try {
        latestQrDataUrl = await qrcode.toDataURL(qr, { width: 256, margin: 1 })
      } catch {}
      status = 'qr_ready'
      console.log('[Baileys] QR ready — open http://localhost:' + PORT)
    }

    if (connection === 'open') {
      status = 'connected'
      console.log('[Baileys] Connected!')
    }

    if (connection === 'close') {
      const reason = new Boom(lastDisconnect?.error)?.output?.statusCode
      const shouldReconnect = reason !== DisconnectReason.loggedOut

      if (status === 'connected') status = 'disconnected'
      else if (!latestQr) status = 'failed'

      console.log('[Baileys] Disconnected, reason:', reason, 'reconnect:', shouldReconnect)

      if (shouldReconnect) {
        setTimeout(startBaileys, 3000)
      }
    }
  })
}

// HTTP server — serve the QR page + /qr endpoint
const server = createServer((req, res) => {
  if (req.url === '/qr') {
    res.writeHead(200, { 'Content-Type': 'application/json' })
    res.end(JSON.stringify({ qr: latestQr, qrDataUrl: latestQrDataUrl, status }))
    return
  }

  // Main page
  res.writeHead(200, { 'Content-Type': 'text/html' })
  res.end(`<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Baileys QR Test</title>
  <style>
    body { font-family: sans-serif; text-align: center; padding: 40px; background: #f5f5f5; }
    #card { display: inline-block; background: white; padding: 32px 48px; border-radius: 12px;
            box-shadow: 0 2px 12px rgba(0,0,0,.12); margin-top: 24px; }
    #status { font-size: 18px; margin-bottom: 16px; color: #555; }
    #ok { font-size: 64px; color: #22c55e; display: none; }
    #error { color: #dc2626; display: none; }
    h2 { color: #25d366; }
  </style>
</head>
<body>
  <h2>Baileys QR Test (port ${PORT})</h2>
  <div id="card">
    <div id="status">Connecting...</div>
    <div id="qr"></div>
    <div id="ok">&#10003; Connected!</div>
    <div id="error">Connection failed. Try again in a few minutes.</div>
  </div>
  <script>
    let currentQr = null
    async function poll() {
      try {
        const r = await fetch('/qr')
        const d = await r.json()
        document.getElementById('status').textContent = d.status
        if (d.status === 'connected') {
          document.getElementById('qr').style.display = 'none'
          document.getElementById('ok').style.display = 'block'
          return
        }
        if (d.status === 'failed') {
          document.getElementById('qr').style.display = 'none'
          document.getElementById('error').style.display = 'block'
          return
        }
        if (d.qr && d.qr !== currentQr) {
          currentQr = d.qr
          document.getElementById('qr').innerHTML = d.qrDataUrl
            ? '<img src="' + d.qrDataUrl + '" />'
            : '(QR data available but image failed to render)'
        }
      } catch (e) {
        document.getElementById('status').textContent = 'Error: ' + e.message
      }
      setTimeout(poll, 2000)
    }
    poll()
  </script>
</body>
</html>`)
})

server.listen(PORT, () => {
  console.log(`[Baileys] HTTP server listening on http://localhost:${PORT}`)
})

startBaileys().catch(console.error)
