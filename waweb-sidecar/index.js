// WhatsApp Web (waweb) sidecar for WhatsAppBridge.API.
//
// Spawned by WaWebEngine.cs, one process per WhatsApp session. Drives the REAL WhatsApp
// Web client in a headless Chromium via whatsapp-web.js — the browser presents an
// authentic client fingerprint, which removes the protocol-fingerprint ban vector that
// plagues Dawa/Baileys. All communication is newline-delimited JSON over stdio, speaking
// the exact same protocol and command set as baileys-sidecar/index.js:
//   stdin  → { id, cmd, args }            (commands from C#)
//   stdout → { id, ok, result|error }     (command responses)
//   stdout → { event, data }              (unsolicited events: qr/connected/message/...)
// stderr is free-form logging (surfaced into the API log by the engine).
//
// Credentials live in {sessionDir}/waweb-auth/ (a persistent Chromium profile managed by
// LocalAuth). They are not compatible with Dawa's creds.json or Baileys' baileys-auth/ —
// a session switching engines pairs a new device (QR re-scan).
//
// JID convention: the bridge speaks Baileys-style jids (xxx@s.whatsapp.net), WhatsApp Web
// speaks xxx@c.us. Everything crossing the stdio boundary is normalized to bridge style on
// the way out and to WA-Web style on the way in; groups (@g.us) are identical in both.
//
// Chromium: puppeteer downloads its own Chromium on npm install (large!) unless
// PUPPETEER_SKIP_DOWNLOAD=1 is set; set WAWEB_CHROME_PATH (passed by WaWebEngine from
// WhatsApp:WaWeb:ChromePath) to use an installed Chrome/Chromium instead.

import wwebjs from 'whatsapp-web.js'
import { createInterface } from 'readline'
import { mkdirSync } from 'fs'
import { join } from 'path'

const { Client, LocalAuth, MessageMedia } = wwebjs

// ─── Args & state ────────────────────────────────────────────────────────────

const argIdx = process.argv.indexOf('--session-dir')
if (argIdx === -1 || !process.argv[argIdx + 1]) {
  process.stderr.write('Usage: node index.js --session-dir <dir>\n')
  process.exit(2)
}
const SESSION_DIR = process.argv[argIdx + 1]
const AUTH_DIR = join(SESSION_DIR, 'waweb-auth')
mkdirSync(AUTH_DIR, { recursive: true })

let client = null
let ready = false
let shuttingDown = false
let reconnectDelayMs = 3000

// ─── Output helpers ──────────────────────────────────────────────────────────

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n')
}
function emit(event, data) { send({ event, data }) }
function log(msg) { process.stderr.write(`${msg}\n`) }

// ─── JID normalization ───────────────────────────────────────────────────────
// Bridge style: 31641406266@s.whatsapp.net · WA-Web style: 31641406266@c.us
// Groups are xxx@g.us in both; unknown suffixes (@lid, @broadcast) pass through.

function toWaWebJid(v) {
  if (!v) return v
  const s = String(v)
  if (!s.includes('@')) return `${s.replace(/[^0-9]/g, '')}@c.us`
  return s.replace('@s.whatsapp.net', '@c.us')
}

function toBridgeJid(v) {
  if (!v) return v
  return String(v).replace('@c.us', '@s.whatsapp.net')
}

// Serialized ids in whatsapp-web.js internals may be strings or {_serialized} objects.
const serialized = (v) => (v == null ? null : typeof v === 'string' ? v : v._serialized || null)

// ─── Message mapping ─────────────────────────────────────────────────────────
// Produces the exact IncomingMessage shape baileys-sidecar emits (field names, numeric
// timestamps in seconds, base64 media fields, bridge-style jids).

const toNum = (v) => (v == null ? 0 : typeof v === 'number' ? v : Number(v.toString()))

const TYPE_MAP = {
  chat: 'text',
  image: 'image',
  video: 'video',
  audio: 'audio',
  ptt: 'audio',
  document: 'document',
  sticker: 'sticker',
  location: 'location',
  vcard: 'contact',
  multi_vcard: 'contact',
  revoked: 'protocol',
  e2e_notification: 'protocol',
  protocol: 'protocol',
}

function mapMessage(msg) {
  const d = msg._data || {} // raw WA Web payload: media/quote metadata lives here
  const remoteJid = toBridgeJid(msg.fromMe ? msg.to : msg.from)
  const isGroup = typeof remoteJid === 'string' && remoteJid.endsWith('@g.us')
  const participant = isGroup && msg.author ? toBridgeJid(serialized(msg.author) || msg.author) : null
  const isRevoked = msg.type === 'revoked'
  const hasMedia = !!msg.hasMedia

  const quotedTypeRaw = d.quotedMsg?.type
  const quotedType = quotedTypeRaw ? (TYPE_MAP[quotedTypeRaw] || 'unknown') : null

  return {
    id: msg.id?.id || '',
    from: msg.fromMe ? 'me' : (participant || remoteJid || ''),
    remoteJid: remoteJid || '',
    participant,
    type: TYPE_MAP[msg.type] || 'unknown',
    text: msg.body || null, // for media messages body carries the caption
    fromMe: !!msg.fromMe,
    timestamp: toNum(msg.timestamp), // seconds, same unit as Baileys' messageTimestamp
    pushName: d.notifyName || null,
    isRevoked,
    // Media envelope fields (same names as Baileys). WA Web exposes the encrypted blob
    // metadata on the raw payload; mediaKey/encFilehash are already base64 strings there.
    mediaUrl: hasMedia ? (d.deprecatedMms3Url || d.directPath || null) : null,
    mimeType: d.mimetype || null,
    fileName: d.filename || null,
    fileSize: d.size != null ? toNum(d.size) : null,
    duration: msg.duration != null ? toNum(msg.duration) : (d.duration != null ? toNum(d.duration) : null),
    width: d.width || null,
    height: d.height || null,
    mediaKey: typeof d.mediaKey === 'string' ? d.mediaKey : null,
    mediaSha256Enc: typeof d.encFilehash === 'string' ? d.encFilehash : null,
    reactionEmoji: null, // reactions arrive via the message_reaction event, mapped below
    reactionTargetId: isRevoked ? (d.protocolMessageKey?.id || null) : null,
    quotedMessageId: d.quotedStanzaID || null,
    quotedFrom: toBridgeJid(serialized(d.quotedParticipant)) || null,
    quotedText: d.quotedMsg?.body || null,
    quotedType,
  }
}

function mapReaction(reaction) {
  // whatsapp-web.js surfaces reactions as a dedicated event, not a message type.
  // Shape it like the reaction IncomingMessage baileys-sidecar produces.
  const remoteJid = toBridgeJid(reaction.id?.remote ? serialized(reaction.id.remote) || reaction.id.remote : null)
  const sender = toBridgeJid(serialized(reaction.senderId) || reaction.senderId)
  const fromMe = !!reaction.id?.fromMe
  return {
    id: reaction.id?.id || '',
    from: fromMe ? 'me' : (sender || remoteJid || ''),
    remoteJid: remoteJid || '',
    participant: remoteJid && remoteJid.endsWith('@g.us') ? sender : null,
    type: 'reaction',
    text: null,
    fromMe,
    timestamp: toNum(reaction.timestamp),
    pushName: null,
    isRevoked: false,
    mediaUrl: null, mimeType: null, fileName: null, fileSize: null,
    duration: null, width: null, height: null, mediaKey: null, mediaSha256Enc: null,
    reactionEmoji: reaction.reaction || '',
    reactionTargetId: reaction.msgId?.id || null,
    quotedMessageId: null, quotedFrom: null, quotedText: null, quotedType: null,
  }
}

const ACK_MAP = { 1: 'Sent', 2: 'Delivered', 3: 'Read', 4: 'Played' }

// ─── Client lifecycle ────────────────────────────────────────────────────────

function startClient() {
  client = new Client({
    authStrategy: new LocalAuth({ dataPath: AUTH_DIR, clientId: 'bridge' }),
    puppeteer: {
      headless: true,
      executablePath: process.env.WAWEB_CHROME_PATH || undefined,
      args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage'],
    },
  })

  client.on('qr', (qr) => emit('qr', { qr }))

  client.on('ready', () => {
    ready = true
    reconnectDelayMs = 3000
    const jid = toBridgeJid(client.info?.wid?._serialized || null)
    emit('connected', { jid })
    log(`connected as ${jid}`)
  })

  client.on('auth_failure', (message) => {
    ready = false
    log(`auth failure: ${message}`)
    // Saved browser session is invalid — stay alive so the C# side can decide, do not loop.
    emit('disconnected', { code: null, loggedOut: true })
  })

  client.on('disconnected', (reason) => {
    ready = false
    const loggedOut = reason === 'LOGOUT'
    log(`disconnected (reason ${reason}, loggedOut ${loggedOut})`)
    emit('disconnected', { code: null, loggedOut })
    if (shuttingDown || loggedOut) return
    const delay = reconnectDelayMs
    reconnectDelayMs = Math.min(reconnectDelayMs * 2, 60_000)
    setTimeout(() => {
      client.destroy().catch(() => { }).finally(() => {
        try { startClient() } catch (e) { log(`reconnect failed: ${e.message}`) }
      })
    }, delay)
  })

  // message_create fires for both incoming and own (fromMe) messages — same coverage as
  // Baileys' messages.upsert with type 'notify'.
  client.on('message_create', (msg) => {
    try {
      const mapped = mapMessage(msg)
      if (mapped.remoteJid === 'status@broadcast') return
      emit('message', mapped)
    } catch (e) { log(`message mapping failed: ${e.message}`) }
  })

  client.on('message_revoke_everyone', (after, _before) => {
    try {
      if (!after) return
      const mapped = mapMessage(after)
      if (mapped.remoteJid === 'status@broadcast') return
      mapped.isRevoked = true
      emit('message', mapped)
    } catch (e) { log(`revoke mapping failed: ${e.message}`) }
  })

  client.on('message_reaction', (reaction) => {
    try {
      const mapped = mapReaction(reaction)
      if (mapped.remoteJid === 'status@broadcast') return
      emit('message', mapped)
    } catch (e) { log(`reaction mapping failed: ${e.message}`) }
  })

  client.on('message_ack', (msg, ack) => {
    const status = ACK_MAP[ack]
    if (status && msg.id?.id) {
      emit('message_status', {
        messageId: msg.id.id,
        jid: toBridgeJid(msg.fromMe ? msg.to : msg.from) || '',
        status,
      })
    }
  })

  client.initialize().catch((e) => {
    log(`initialize failed: ${e?.stack || e}`)
    emit('disconnected', { code: null, loggedOut: false, startupError: String(e?.message || e) })
  })
}

// ─── Commands ────────────────────────────────────────────────────────────────

function requireClient() {
  if (!client) throw new Error('client not initialized')
  return client
}

// whatsapp-web.js addresses messages by serialized id: "{fromMe}_{chatId}_{msgId}"
// (group messages from others carry a 4th "_{participant}" segment). The bridge only
// carries the raw msgId, so probe both fromMe variants and fall back to scanning the
// chat's recent messages (covers the group-participant case).
async function findMessage(chatId, messageId, fromMeHint = null) {
  const c = requireClient()
  const order = fromMeHint == null ? [false, true] : [!!fromMeHint, !fromMeHint]
  for (const fromMe of order) {
    try {
      const m = await c.getMessageById(`${fromMe}_${chatId}_${messageId}`)
      if (m) return m
    } catch { /* try the other variant */ }
  }
  try {
    const chat = await c.getChatById(chatId)
    const recent = await chat.fetchMessages({ limit: 100 })
    return (recent || []).find(m => m.id?.id === messageId) || null
  } catch { return null }
}

const NOT_SUPPORTED = 'not supported on waweb engine'

const commands = {
  async sendMessage({ to, text }) {
    const chatId = toWaWebJid(to)
    const m = await requireClient().sendMessage(chatId, text)
    return { messageId: m?.id?.id || '', jid: toBridgeJid(chatId) }
  },

  async sendReply({ to, text, quotedMsgId, quotedFromJid }) {
    const chatId = toWaWebJid(to)
    const quoted = await findMessage(chatId, quotedMsgId,
      quotedFromJid && toWaWebJid(quotedFromJid) === chatId ? false : null)
    const options = quoted ? { quotedMessageId: quoted.id._serialized } : {}
    if (!quoted) log(`sendReply: quoted message ${quotedMsgId} not found — sending without quote`)
    const m = await requireClient().sendMessage(chatId, text, options)
    return { messageId: m?.id?.id || '', jid: toBridgeJid(chatId) }
  },

  async sendMedia({ to, mediaType, mimeType, caption, fileName, dataBase64 }) {
    const chatId = toWaWebJid(to)
    const media = new MessageMedia(mimeType || 'application/octet-stream', dataBase64, fileName || undefined)
    const options = { caption: caption || undefined }
    switch ((mediaType || '').toLowerCase()) {
      case 'image': case 'video': break
      case 'audio': options.sendAudioAsVoice = (mimeType || '').includes('ogg'); break
      case 'sticker': options.sendMediaAsSticker = true; break
      default: options.sendMediaAsDocument = true
    }
    const m = await requireClient().sendMessage(chatId, media, options)
    return { messageId: m?.id?.id || '', jid: toBridgeJid(chatId) }
  },

  async sendReaction({ jid, messageId, fromMe, emoji }) {
    const chatId = toWaWebJid(jid)
    const m = await findMessage(chatId, messageId, fromMe)
    if (!m) throw new Error(`message ${messageId} not found in ${chatId}`)
    await m.react(emoji || '')
    return {}
  },

  async sendTyping({ jid, isTyping }) {
    const chat = await requireClient().getChatById(toWaWebJid(jid))
    if (isTyping) await chat.sendStateTyping()
    else await chat.clearState()
    return {}
  },

  async sendUserPresence({ isOnline }) {
    const c = requireClient()
    if (isOnline) await c.sendPresenceAvailable()
    else await c.sendPresenceUnavailable()
    return {}
  },

  async revokeMessage({ jid, messageId, fromMe }) {
    const chatId = toWaWebJid(jid)
    const m = await findMessage(chatId, messageId, fromMe !== false)
    if (!m) throw new Error(`message ${messageId} not found in ${chatId}`)
    await m.delete(true) // true = delete for everyone (revoke)
    return {}
  },

  async sendReadReceipt({ jid }) {
    const chat = await requireClient().getChatById(toWaWebJid(jid))
    await chat.sendSeen()
    return {}
  },

  async getContacts() {
    const list = await requireClient().getContacts()
    return list
      .filter(c => c.id?.server === 'c.us' || c.id?.server === 'lid')
      .map(c => ({ jid: toBridgeJid(c.id._serialized), name: c.name || c.pushname || '' }))
  },

  async getChats() {
    const list = await requireClient().getChats()
    return list.map(c => ({
      jid: toBridgeJid(c.id._serialized),
      name: c.name || '',
      archived: !!c.archived,
      pinned: !!c.pinned,
    }))
  },

  async getProfilePicture({ jid }) {
    try { return (await requireClient().getProfilePicUrl(toWaWebJid(jid))) || null }
    catch { return null }
  },

  async subscribePresence() {
    // WA Web has no explicit presence subscription; wweb.js exposes none.
    throw new Error(NOT_SUPPORTED)
  },

  async resolveLid() {
    // No LID→phone-number mapping API in whatsapp-web.js. Same graceful null as the
    // Baileys sidecar returns on versions without lidMapping support.
    return null
  },

  async fetchMessageHistory({ jid, count }) {
    // Unlike Baileys (async via history_message events), WA Web loads chat history
    // directly — return the mapped messages synchronously.
    const chat = await requireClient().getChatById(toWaWebJid(jid))
    const messages = await chat.fetchMessages({ limit: count || 50 })
    return (messages || []).map(mapMessage)
  },

  async requestHistorySync() {
    // On-demand deep history sync beyond fetchMessages has no WA Web equivalent.
    throw new Error(`${NOT_SUPPORTED} — use fetchMessageHistory (chat.fetchMessages) instead`)
  },

  async getGroupJids() {
    const list = await requireClient().getChats()
    return list.filter(c => c.isGroup).map(c => toBridgeJid(c.id._serialized))
  },

  async getGroupMetadata({ jid }) {
    const chat = await requireClient().getChatById(toWaWebJid(jid))
    if (!chat || !chat.isGroup) return null
    const meta = chat.groupMetadata || {}
    const participants = chat.participants || meta.participants || []
    return {
      jid: toBridgeJid(chat.id._serialized),
      subject: chat.name || meta.subject || '',
      creator: toBridgeJid(serialized(meta.owner)) || '',
      creationTimestamp: toNum(meta.creation),
      participants: participants.map(p => ({
        jid: toBridgeJid(serialized(p.id) || p.id),
        lidJid: '',
        type: p.isSuperAdmin ? 'superadmin' : p.isAdmin ? 'admin' : 'member',
      })),
    }
  },

  async createGroup({ subject, participants }) {
    const r = await requireClient().createGroup(subject, (participants || []).map(toWaWebJid))
    const gid = typeof r === 'string' ? r : serialized(r?.gid)
    return gid ? toBridgeJid(gid) : null
  },

  async leaveGroup({ jid }) {
    const chat = await requireClient().getChatById(toWaWebJid(jid))
    await chat.leave()
    return {}
  },

  async groupParticipantsUpdate({ jid, participants, action }) {
    const chat = await requireClient().getChatById(toWaWebJid(jid))
    const ids = (participants || []).map(toWaWebJid)
    let result
    switch (action) {
      case 'add': result = await chat.addParticipants(ids); break
      case 'remove': result = await chat.removeParticipants(ids); break
      case 'promote': result = await chat.promoteParticipants(ids); break
      case 'demote': result = await chat.demoteParticipants(ids); break
      default: throw new Error(`unknown action: ${action}`)
    }
    const map = {}
    if (result && typeof result === 'object') {
      for (const [key, info] of Object.entries(result)) {
        if (!key.includes('@')) continue // skip aggregate fields like { status: 200 }
        map[toBridgeJid(key)] = String(info?.code ?? info?.message ?? info ?? '')
      }
    }
    if (Object.keys(map).length === 0)
      for (const id of ids) map[toBridgeJid(id)] = String(result?.status ?? 'ok')
    return map
  },

  async getGroupInviteLink({ jid }) {
    const chat = await requireClient().getChatById(toWaWebJid(jid))
    const code = await chat.getInviteCode()
    return code ? `https://chat.whatsapp.com/${code}` : null
  },

  async updateGroupSubject({ jid, subject }) {
    const chat = await requireClient().getChatById(toWaWebJid(jid))
    await chat.setSubject(subject)
    return {}
  },

  async shutdown() {
    shuttingDown = true
    gracefulExit()
    return {}
  },
}

function gracefulExit() {
  shuttingDown = true
  const bail = setTimeout(() => process.exit(0), 3000)
  bail.unref?.()
  Promise.resolve(client?.destroy?.()).catch(() => { }).finally(() => process.exit(0))
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
  // Parent closed stdin (or died) — exit so we never orphan a headless Chromium.
  gracefulExit()
})

process.on('uncaughtException', (e) => log(`uncaught: ${e?.stack || e}`))
process.on('unhandledRejection', (e) => log(`unhandled rejection: ${e?.stack || e}`))

startClient()
