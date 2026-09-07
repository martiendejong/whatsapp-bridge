// Baileys sidecar for WhatsAppBridge.API.
//
// Spawned by BaileysEngine.cs, one process per WhatsApp session. All communication is
// newline-delimited JSON over stdio:
//   stdin  → { id, cmd, args }            (commands from C#)
//   stdout → { id, ok, result|error }     (command responses)
//   stdout → { event, data }              (unsolicited events: qr/connected/message/...)
// stderr is free-form logging (surfaced into the API log by BaileysEngine).
//
// Credentials live in {sessionDir}/baileys-auth/ (multi-file auth state). They are not
// compatible with Dawa's creds.json — a session switching engines pairs a new device.

import {
  makeWASocket,
  useMultiFileAuthState,
  makeCacheableSignalKeyStore,
  fetchLatestBaileysVersion,
  DisconnectReason,
  getContentType,
} from '@whiskeysockets/baileys'
import { createInterface } from 'readline'
import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'fs'
import { join } from 'path'
import pino from 'pino'

// ─── Args & state ────────────────────────────────────────────────────────────

const argIdx = process.argv.indexOf('--session-dir')
if (argIdx === -1 || !process.argv[argIdx + 1]) {
  process.stderr.write('Usage: node index.js --session-dir <dir>\n')
  process.exit(2)
}
const SESSION_DIR = process.argv[argIdx + 1]
const AUTH_DIR = join(SESSION_DIR, 'baileys-auth')
const STORE_FILE = join(SESSION_DIR, 'baileys-store.json')
mkdirSync(AUTH_DIR, { recursive: true })

const logger = pino({ level: 'silent' })
let sock = null
let shuttingDown = false
let reconnectDelayMs = 3000

// Minimal chat/contact store fed from sync + live events (makeInMemoryStore is gone in
// recent Baileys releases, and we only need names/jids — messages are stored C#-side).
const chats = new Map()    // jid → { jid, name, archived, pinned }
const contacts = new Map() // jid → { jid, name }
let storeDirty = false

function loadStore() {
  try {
    if (!existsSync(STORE_FILE)) return
    const data = JSON.parse(readFileSync(STORE_FILE, 'utf8'))
    for (const c of data.chats || []) chats.set(c.jid, c)
    for (const c of data.contacts || []) contacts.set(c.jid, c)
  } catch (e) { log(`store load failed: ${e.message}`) }
}

function saveStore() {
  if (!storeDirty) return
  storeDirty = false
  try {
    writeFileSync(STORE_FILE, JSON.stringify({
      chats: [...chats.values()],
      contacts: [...contacts.values()],
    }))
  } catch (e) { log(`store save failed: ${e.message}`) }
}
setInterval(saveStore, 10_000).unref()

// ─── Output helpers ──────────────────────────────────────────────────────────

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n')
}
function emit(event, data) { send({ event, data }) }
function log(msg) { process.stderr.write(`${msg}\n`) }

// ─── Message mapping ─────────────────────────────────────────────────────────

const toNum = (v) => (v == null ? 0 : typeof v === 'number' ? v : Number(v.toString()))
const b64 = (v) => (v == null ? null : Buffer.from(v).toString('base64'))

const TYPE_MAP = {
  conversation: 'text',
  extendedTextMessage: 'text',
  imageMessage: 'image',
  videoMessage: 'video',
  audioMessage: 'audio',
  documentMessage: 'document',
  documentWithCaptionMessage: 'document',
  stickerMessage: 'sticker',
  reactionMessage: 'reaction',
  locationMessage: 'location',
  contactMessage: 'contact',
  protocolMessage: 'protocol',
}

function unwrap(message) {
  // Peel ephemeral / viewOnce / edited wrappers to reach the real content.
  let m = message
  for (let i = 0; i < 5 && m; i++) {
    const inner = m.ephemeralMessage?.message
      || m.viewOnceMessage?.message
      || m.viewOnceMessageV2?.message
      || m.viewOnceMessageV2Extension?.message
      || m.editedMessage?.message?.protocolMessage?.editedMessage
      || m.documentWithCaptionMessage?.message
    if (!inner) break
    m = inner
  }
  return m
}

function mapMessage(waMsg) {
  const key = waMsg.key || {}
  const msg = unwrap(waMsg.message || {})
  const contentType = getContentType(msg) || 'conversation'
  const media = msg.imageMessage || msg.videoMessage || msg.audioMessage
    || msg.documentMessage || msg.stickerMessage
  const ctx = msg.extendedTextMessage?.contextInfo || media?.contextInfo
  const reaction = msg.reactionMessage
  const revoke = msg.protocolMessage?.type === 0 // proto REVOKE

  const text = msg.conversation
    || msg.extendedTextMessage?.text
    || media?.caption
    || null

  const quoted = ctx?.quotedMessage ? unwrap(ctx.quotedMessage) : null
  const quotedType = quoted ? (TYPE_MAP[getContentType(quoted)] || 'unknown') : null

  return {
    id: key.id || '',
    from: key.fromMe ? 'me' : (key.participant || key.remoteJid || ''),
    remoteJid: key.remoteJid || '',
    participant: key.participant || null,
    type: TYPE_MAP[contentType] || 'unknown',
    text,
    fromMe: !!key.fromMe,
    timestamp: toNum(waMsg.messageTimestamp),
    pushName: waMsg.pushName || null,
    isRevoked: revoke,
    mediaUrl: media?.url || null,
    mimeType: media?.mimetype || null,
    fileName: msg.documentMessage?.fileName || null,
    fileSize: media?.fileLength != null ? toNum(media.fileLength) : null,
    duration: media?.seconds != null ? toNum(media.seconds) : null,
    width: media?.width || null,
    height: media?.height || null,
    mediaKey: b64(media?.mediaKey),
    mediaSha256Enc: b64(media?.fileEncSha256),
    reactionEmoji: reaction ? (reaction.text || '') : null,
    reactionTargetId: reaction?.key?.id || (revoke ? msg.protocolMessage?.key?.id : null) || null,
    quotedMessageId: ctx?.stanzaId || null,
    quotedFrom: ctx?.participant || null,
    quotedText: quoted?.conversation || quoted?.extendedTextMessage?.text || null,
    quotedType,
  }
}

const STATUS_MAP = { 2: 'Sent', 3: 'Delivered', 4: 'Read', 5: 'Played' }

function upsertChat(jid, patch) {
  if (!jid || jid === 'status@broadcast') return
  const existing = chats.get(jid) || { jid, name: '', archived: false, pinned: false }
  chats.set(jid, { ...existing, ...patch })
  storeDirty = true
}

// ─── Socket lifecycle ────────────────────────────────────────────────────────

async function startSocket() {
  const { state, saveCreds } = await useMultiFileAuthState(AUTH_DIR)
  const { version } = await fetchLatestBaileysVersion()

  sock = makeWASocket({
    version,
    auth: {
      creds: state.creds,
      keys: makeCacheableSignalKeyStore(state.keys, logger),
    },
    logger,
    printQRInTerminal: false,
    browser: ['Ubuntu', 'Chrome', '20.0.04'],
    syncFullHistory: false,
    markOnlineOnConnect: false,
  })

  sock.ev.on('creds.update', saveCreds)

  sock.ev.on('connection.update', (update) => {
    const { connection, lastDisconnect, qr } = update
    if (qr) emit('qr', { qr })

    if (connection === 'open') {
      reconnectDelayMs = 3000
      emit('connected', { jid: sock.user?.id || null })
      log(`connected as ${sock.user?.id}`)
    }

    if (connection === 'close') {
      const code = lastDisconnect?.error?.output?.statusCode
      const loggedOut = code === DisconnectReason.loggedOut
      log(`connection closed (code ${code}, loggedOut ${loggedOut})`)
      emit('disconnected', { code: code || null, loggedOut })
      if (shuttingDown) return
      if (loggedOut) {
        // Creds are invalid — stay alive so the C# side can decide, but do not loop.
        return
      }
      setTimeout(() => startSocket().catch(e => log(`reconnect failed: ${e.message}`)), reconnectDelayMs)
      reconnectDelayMs = Math.min(reconnectDelayMs * 2, 60_000)
    }
  })

  sock.ev.on('messages.upsert', ({ messages, type }) => {
    for (const m of messages) {
      if (!m.message) continue
      const mapped = mapMessage(m)
      if (mapped.remoteJid === 'status@broadcast') continue
      upsertChat(mapped.remoteJid, m.pushName && !mapped.fromMe ? { name: m.pushName } : {})
      emit(type === 'notify' ? 'message' : 'history_message', mapped)
    }
  })

  sock.ev.on('messages.update', (updates) => {
    for (const u of updates) {
      const status = STATUS_MAP[u.update?.status]
      if (status && u.key?.id) {
        emit('message_status', { messageId: u.key.id, jid: u.key.remoteJid || '', status })
      }
    }
  })

  sock.ev.on('messaging-history.set', ({ chats: histChats, contacts: histContacts, messages }) => {
    for (const c of histChats || []) {
      upsertChat(c.id, {
        name: c.name || chats.get(c.id)?.name || '',
        archived: !!c.archived,
        pinned: !!(c.pinned && toNum(c.pinned) > 0),
      })
    }
    for (const c of histContacts || []) {
      if (!c.id) continue
      contacts.set(c.id, { jid: c.id, name: c.name || c.notify || '' })
      storeDirty = true
    }
    let emitted = 0
    for (const m of messages || []) {
      if (!m.message) continue
      const mapped = mapMessage(m)
      if (mapped.remoteJid === 'status@broadcast') continue
      emit('history_message', mapped)
      emitted++
    }
    emit('history_sync_completed', { count: emitted })
    saveStore()
  })

  sock.ev.on('chats.upsert', (list) => {
    for (const c of list) upsertChat(c.id, { name: c.name || chats.get(c.id)?.name || '' })
  })
  sock.ev.on('chats.update', (list) => {
    for (const c of list) {
      const patch = {}
      if (c.name) patch.name = c.name
      if (c.archived != null) patch.archived = !!c.archived
      if (c.pinned != null) patch.pinned = !!(c.pinned && toNum(c.pinned) > 0)
      upsertChat(c.id, patch)
    }
  })
  sock.ev.on('contacts.upsert', (list) => {
    for (const c of list) {
      if (!c.id) continue
      contacts.set(c.id, { jid: c.id, name: c.name || c.notify || contacts.get(c.id)?.name || '' })
      storeDirty = true
    }
  })

  sock.ev.on('presence.update', ({ id, presences }) => {
    for (const [jid, p] of Object.entries(presences || {})) {
      emit('presence', { jid, status: p.lastKnownPresence || 'unknown' })
    }
  })
}

// ─── Commands ────────────────────────────────────────────────────────────────

const jidify = (v) => (v.includes('@') ? v : `${v.replace(/[^0-9]/g, '')}@s.whatsapp.net`)

function requireSock() {
  if (!sock) throw new Error('socket not initialized')
  return sock
}

const commands = {
  async sendMessage({ to, text }) {
    const jid = jidify(to)
    const result = await requireSock().sendMessage(jid, { text })
    return { messageId: result?.key?.id || '', jid }
  },

  async sendReply({ to, text, quotedMsgId, quotedFromJid }) {
    const jid = jidify(to)
    // Fabricated quoted stub: enough for WhatsApp to render the reply context without
    // needing the original message object (only key ids travel over the wire anyway).
    const quoted = {
      key: { remoteJid: jid, id: quotedMsgId, fromMe: false, participant: quotedFromJid || undefined },
      message: { conversation: '' },
    }
    const result = await requireSock().sendMessage(jid, { text }, { quoted })
    return { messageId: result?.key?.id || '', jid }
  },

  async sendMedia({ to, mediaType, mimeType, caption, fileName, dataBase64 }) {
    const jid = jidify(to)
    const buffer = Buffer.from(dataBase64, 'base64')
    let content
    switch ((mediaType || '').toLowerCase()) {
      case 'image': content = { image: buffer, caption: caption || undefined, mimetype: mimeType }; break
      case 'video': content = { video: buffer, caption: caption || undefined, mimetype: mimeType }; break
      case 'audio': content = { audio: buffer, mimetype: mimeType, ptt: (mimeType || '').includes('ogg') }; break
      case 'sticker': content = { sticker: buffer, mimetype: mimeType }; break
      default: content = { document: buffer, mimetype: mimeType, fileName: fileName || 'file', caption: caption || undefined }
    }
    const result = await requireSock().sendMessage(jid, content)
    return { messageId: result?.key?.id || '', jid }
  },

  async sendReaction({ jid, messageId, fromMe, emoji }) {
    const remoteJid = jidify(jid)
    await requireSock().sendMessage(remoteJid, {
      react: { text: emoji || '', key: { remoteJid, id: messageId, fromMe: !!fromMe } },
    })
    return {}
  },

  async sendTyping({ jid, isTyping }) {
    await requireSock().sendPresenceUpdate(isTyping ? 'composing' : 'paused', jidify(jid))
    return {}
  },

  async sendUserPresence({ isOnline }) {
    await requireSock().sendPresenceUpdate(isOnline ? 'available' : 'unavailable')
    return {}
  },

  async revokeMessage({ jid, messageId, fromMe }) {
    const remoteJid = jidify(jid)
    await requireSock().sendMessage(remoteJid, {
      delete: { remoteJid, id: messageId, fromMe: fromMe !== false },
    })
    return {}
  },

  async sendReadReceipt({ jid, messageId }) {
    await requireSock().readMessages([{ remoteJid: jidify(jid), id: messageId, participant: undefined }])
    return {}
  },

  async getContacts() {
    return [...contacts.values()]
  },

  async getChats() {
    return [...chats.values()]
  },

  async getProfilePicture({ jid }) {
    try { return await requireSock().profilePictureUrl(jidify(jid), 'image') }
    catch { return null }
  },

  async subscribePresence({ jid }) {
    await requireSock().presenceSubscribe(jidify(jid))
    return {}
  },

  async resolveLid({ lidJid }) {
    try {
      const mapping = requireSock().signalRepository?.lidMapping
      if (mapping?.getPNForLID) return (await mapping.getPNForLID(lidJid)) || null
    } catch { /* not supported in this Baileys version */ }
    return null
  },

  async fetchMessageHistory({ jid, count }) {
    // On-demand history arrives asynchronously via history_message events.
    await commands.requestHistorySync({ jid, count })
    return []
  },

  async requestHistorySync({ jid, count, oldestMsgId, oldestFromMe, oldestTimestampMs }) {
    const s = requireSock()
    if (typeof s.fetchMessageHistory !== 'function')
      throw new Error('fetchMessageHistory not supported by this Baileys version')
    const key = {
      remoteJid: jidify(jid),
      id: oldestMsgId || '',
      fromMe: !!oldestFromMe,
    }
    const ts = oldestTimestampMs ? Math.floor(oldestTimestampMs / 1000) : Math.floor(Date.now() / 1000)
    await s.fetchMessageHistory(count || 50, key, ts)
    return {}
  },

  async getGroupJids() {
    try {
      const groups = await requireSock().groupFetchAllParticipating()
      for (const jid of Object.keys(groups)) upsertChat(jid, { name: groups[jid].subject || '' })
      return Object.keys(groups)
    } catch {
      return [...chats.keys()].filter(j => j.endsWith('@g.us'))
    }
  },

  async getGroupMetadata({ jid }) {
    const meta = await requireSock().groupMetadata(jid)
    if (!meta) return null
    return {
      jid: meta.id,
      subject: meta.subject || '',
      creator: meta.owner || '',
      creationTimestamp: toNum(meta.creation),
      participants: (meta.participants || []).map(p => ({
        jid: p.id,
        lidJid: p.lid || '',
        type: p.admin === 'superadmin' ? 'superadmin' : p.admin === 'admin' ? 'admin' : 'member',
      })),
    }
  },

  async createGroup({ subject, participants }) {
    const result = await requireSock().groupCreate(subject, (participants || []).map(jidify))
    return result?.id || null
  },

  async leaveGroup({ jid }) {
    await requireSock().groupLeave(jid)
    return {}
  },

  async groupParticipantsUpdate({ jid, participants, action }) {
    const result = await requireSock().groupParticipantsUpdate(jid, (participants || []).map(jidify), action)
    const map = {}
    for (const r of result || []) map[r.jid || r.id || ''] = String(r.status ?? '')
    return map
  },

  async getGroupInviteLink({ jid }) {
    const code = await requireSock().groupInviteCode(jid)
    return code ? `https://chat.whatsapp.com/${code}` : null
  },

  async updateGroupSubject({ jid, subject }) {
    await requireSock().groupUpdateSubject(jid, subject)
    return {}
  },

  async shutdown() {
    shuttingDown = true
    saveStore()
    try { sock?.end?.(undefined) } catch { }
    setTimeout(() => process.exit(0), 200)
    return {}
  },
}

// ─── stdin loop ──────────────────────────────────────────────────────────────

const rl = createInterface({ input: process.stdin, terminal: false })
rl.on('line', async (line) => {
  line = line.trim()
  if (!line) return
  let req
  try { req = JSON.parse(line) } catch { log(`bad stdin line: ${line.slice(0, 200)}`); return }
  const { id, cmd, args } = req
  const handler = commands[cmd]
  if (!handler) {
    send({ id, ok: false, error: `unknown command: ${cmd}` })
    return
  }
  try {
    const result = await handler(args || {})
    send({ id, ok: true, result })
  } catch (e) {
    send({ id, ok: false, error: e?.message || String(e) })
  }
})
rl.on('close', () => {
  // Parent closed stdin (or died) — exit so we never orphan a connected socket.
  shuttingDown = true
  saveStore()
  try { sock?.end?.(undefined) } catch { }
  process.exit(0)
})

process.on('uncaughtException', (e) => log(`uncaught: ${e?.stack || e}`))
process.on('unhandledRejection', (e) => log(`unhandled rejection: ${e?.stack || e}`))

loadStore()
startSocket().catch((e) => {
  log(`startup failed: ${e?.stack || e}`)
  emit('disconnected', { code: null, loggedOut: false, startupError: String(e?.message || e) })
})
