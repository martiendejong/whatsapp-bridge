import { makeWASocket, useMultiFileAuthState, DisconnectReason, makeCacheableSignalKeyStore, fetchLatestBaileysVersion } from '@whiskeysockets/baileys'
import { Boom } from '@hapi/boom'
import pino from 'pino'
import { rmSync, existsSync } from 'fs'
import { join, dirname } from 'path'
import { fileURLToPath } from 'url'

const __dirname = dirname(fileURLToPath(import.meta.url))
const SESSION_DIR = join(__dirname, 'diag-session')

if (existsSync(SESSION_DIR)) rmSync(SESSION_DIR, { recursive: true })

const logger = pino({ level: 'debug' })

const { version, isLatest } = await fetchLatestBaileysVersion()
console.log(`[Diag] Using WA version ${version.join('.')} (isLatest: ${isLatest})`)

const { state, saveCreds } = await useMultiFileAuthState(SESSION_DIR)

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
  if (qr) console.log('[Diag] QR received!')
  if (connection === 'open') { console.log('[Diag] Connected!'); process.exit(0) }
  if (connection === 'close') {
    const err = lastDisconnect?.error
    const boom = new Boom(err)
    console.log('[Diag] Disconnected:', boom?.output?.statusCode, err?.message)
    console.log('[Diag] Full error:', JSON.stringify(err, null, 2))
    process.exit(1)
  }
})

setTimeout(() => { console.log('[Diag] Timeout'); process.exit(2) }, 30000)
