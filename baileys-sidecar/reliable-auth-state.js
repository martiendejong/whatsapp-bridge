// Reliable multi-file auth state for the Baileys sidecar (task 4998).
//
// Replaces Baileys' useMultiFileAuthState, which in 6.7.x
//   - writes files non-atomically (a crash mid-write leaves a truncated creds.json),
//   - turns ANY read error into "fresh creds" (a transient EPERM/EBUSY silently wipes the session),
//   - has no error surfacing: a failing write is an unhandled rejection in a background event handler,
//     the connection keeps working from memory, and the next restart finds stale credentials.
//
// Guarantees here:
//   - Writes are atomic: temp file in the same directory, fsync, rename. A reader sees the old file or
//     the new file, never a partial one.
//   - Every write attempt has a timeout, so a hung filesystem call cannot block later writes (the
//     per-file queue moves on) and is counted as a failure.
//   - Failed writes are retried with backoff, then kept in memory (`pending`) and retried in the
//     background every `pendingRetryMs`. Reads consult `pending` first, so nothing is lost while the disk
//     is unhappy. Every failure is logged and reported through `onWriteError`.
//   - `getHealth()` exposes how long the oldest unresolved failure has been failing, so the sidecar can
//     raise a warning after 10 minutes.
//   - `flush()` waits for in-flight writes and gives pending writes one last try (used by shutdown).
//   - Nothing here deletes credentials except the explicit removal of a single key file that Baileys
//     asked to remove. A corrupt or orphaned auth folder is reported through `loadStatus` so the caller
//     can rename it (see archiveAuthDir) instead of overwriting it.

import { promises as nodeFs } from 'fs'
import { join, dirname, basename } from 'path'

export class AuthStateCorruptError extends Error {
  constructor(message) {
    super(message)
    this.name = 'AuthStateCorruptError'
  }
}

const fixFileName = (file) => file.replace(/\//g, '__').replace(/:/g, '-')

function withTimeout(promise, ms, what) {
  let timer
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`${what} timed out after ${ms} ms`)), ms)
  })
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer))
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

/**
 * @param {string} folder auth directory
 * @param {{BufferJSON: any, initAuthCreds: () => any, proto?: any}} baileys injected so this module is testable without Baileys
 * @param {object} [opts]
 */
export async function useReliableAuthState(folder, baileys, opts = {}) {
  const { BufferJSON, initAuthCreds, proto } = baileys
  const {
    fs = nodeFs,
    log = () => {},
    onWriteError = () => {},
    now = Date.now,
    writeTimeoutMs = 15_000,
    retryDelaysMs = [100, 300, 1000, 3000],
    pendingRetryMs = 30_000,
  } = opts

  await fs.mkdir(folder, { recursive: true })

  const chains = new Map()   // file → tail of the per-file write queue
  const latestSeq = new Map() // file → sequence number of the newest requested write/removal
  const pending = new Map()  // file → { json|null (null = removal), since, lastError }
  let seq = 0
  let tmpCounter = 0
  let lastOkAt = null
  let lastError = null
  let closed = false

  const pathOf = (file) => join(folder, fixFileName(file))

  // One write attempt: tmp file → fsync → rename. Skips the rename if a newer write was queued meanwhile
  // (a timed-out attempt that finally completes must never overwrite newer data).
  async function writeAtomic(file, json, mySeq) {
    const target = pathOf(file)
    const tmp = `${target}.${process.pid}.${++tmpCounter}.tmp`
    let fh
    try {
      fh = await fs.open(tmp, 'w')
      await fh.writeFile(json)
      await fh.sync()
      await fh.close()
      fh = null
      if (latestSeq.get(file) !== mySeq) {
        await fs.unlink(tmp).catch(() => {})
        return
      }
      await fs.rename(tmp, target)
    } catch (e) {
      if (fh) await fh.close().catch(() => {})
      await fs.unlink(tmp).catch(() => {})
      throw e
    }
  }

  async function removeFile(file) {
    try { await fs.unlink(pathOf(file)) } catch (e) { if (e?.code !== 'ENOENT') throw e }
  }

  function recordFailure(file, json, err) {
    lastError = `${file}: ${err?.message || err}`
    const existing = pending.get(file)
    pending.set(file, { json, since: existing?.since ?? now(), lastError })
    log(`auth write failed for ${file}: ${err?.message || err} (kept in memory, will retry)`)
    try { onWriteError(file, err) } catch { /* reporting must not break writes */ }
  }

  // Never rejects: failures are recorded, logged and reported.
  async function attempt(file, json, mySeq, delays) {
    let lastErr
    for (let i = 0; i <= delays.length; i++) {
      try {
        await withTimeout(
          json === null ? removeFile(file) : writeAtomic(file, json, mySeq),
          writeTimeoutMs,
          `write ${file}`,
        )
        if (latestSeq.get(file) === mySeq) pending.delete(file)
        lastOkAt = now()
        return true
      } catch (e) {
        lastErr = e
        if (i < delays.length) await sleep(delays[i])
      }
    }
    if (latestSeq.get(file) === mySeq) recordFailure(file, json, lastErr)
    return false
  }

  function enqueue(file, fn) {
    const prev = chains.get(file) ?? Promise.resolve()
    const next = prev.then(fn, fn)
    chains.set(file, next)
    next.finally(() => { if (chains.get(file) === next) chains.delete(file) })
    return next
  }

  function writeData(file, value) {
    const json = JSON.stringify(value, BufferJSON.replacer) // snapshot now: later mutations must not leak in
    const mySeq = ++seq
    latestSeq.set(file, mySeq)
    return enqueue(file, () => attempt(file, json, mySeq, retryDelaysMs))
  }

  function removeData(file) {
    const mySeq = ++seq
    latestSeq.set(file, mySeq)
    pending.delete(file) // a queued write for a removed key must not resurrect it
    return enqueue(file, () => attempt(file, null, mySeq, retryDelaysMs))
  }

  async function readData(file, { strict = false } = {}) {
    const p = pending.get(file)
    if (p) return p.json === null ? null : JSON.parse(p.json, BufferJSON.reviver)
    let raw
    try {
      raw = await fs.readFile(pathOf(file), { encoding: 'utf-8' })
    } catch (e) {
      if (e?.code === 'ENOENT') return null
      if (strict) throw new AuthStateCorruptError(`cannot read ${file}: ${e?.message || e}`)
      log(`auth read failed for ${file}: ${e?.message || e}`)
      return null
    }
    try {
      return JSON.parse(raw, BufferJSON.reviver)
    } catch (e) {
      if (strict) throw new AuthStateCorruptError(`${file} is not valid JSON: ${e?.message || e}`)
      log(`auth file ${file} is not valid JSON: ${e?.message || e}`)
      return null
    }
  }

  // ── Initial load ───────────────────────────────────────────────────────────
  let loadStatus = 'resumed'
  let creds = await readData('creds.json', { strict: true }) // throws AuthStateCorruptError
  if (!creds) {
    const entries = await fs.readdir(folder).catch(() => [])
    const hasKeyFiles = entries.some((n) => n.endsWith('.json') && n !== 'creds.json')
    loadStatus = hasKeyFiles ? 'orphaned' : 'fresh'
    creds = initAuthCreds()
  }

  const state = {
    creds,
    keys: {
      get: async (type, ids) => {
        const data = {}
        await Promise.all(ids.map(async (id) => {
          let value = await readData(`${type}-${id}.json`)
          if (type === 'app-state-sync-key' && value && proto?.Message?.AppStateSyncKeyData) {
            value = proto.Message.AppStateSyncKeyData.fromObject(value)
          }
          data[id] = value
        }))
        return data
      },
      set: async (data) => {
        const tasks = []
        for (const category in data) {
          for (const id in data[category]) {
            const value = data[category][id]
            const file = `${category}-${id}.json`
            tasks.push(value ? writeData(file, value) : removeData(file))
          }
        }
        await Promise.all(tasks)
      },
    },
  }

  async function retryPending() {
    if (closed) return
    await Promise.all([...pending.entries()].map(([file, p]) => {
      // Runs after anything already queued for this file; skipped if a newer write resolved the entry meanwhile.
      return enqueue(file, async () => {
        if (pending.get(file) !== p) return true
        return attempt(file, p.json, latestSeq.get(file), [])
      })
    }))
  }
  const timer = setInterval(() => { retryPending().catch(() => {}) }, pendingRetryMs)
  timer.unref?.()

  return {
    state,
    loadStatus,
    saveCreds: () => writeData('creds.json', state.creds),
    /** Wait for in-flight writes, then give pending ones a last try. Resolves (never rejects) within timeoutMs. */
    async flush(timeoutMs = 5000) {
      const work = (async () => {
        await Promise.allSettled([...chains.values()])
        await retryPending()
        await Promise.allSettled([...chains.values()])
      })()
      await Promise.race([work, sleep(timeoutMs)])
      return { pending: pending.size }
    },
    getHealth() {
      let oldest = null
      for (const p of pending.values()) if (oldest === null || p.since < oldest) oldest = p.since
      return { pendingCount: pending.size, oldestFailureAt: oldest, lastError, lastOkAt }
    },
    close() { closed = true; clearInterval(timer) },
  }
}

/**
 * Pure decision for the health ticker: warn when a write has been failing/hanging for longer than
 * `thresholdMs`, at most once per `thresholdMs`. `recovered` is true when a warning was out and the
 * backlog is now empty.
 */
export function evaluateAuthHealth(health, nowMs, { thresholdMs, lastWarnedAt }) {
  if (health.pendingCount > 0 && health.oldestFailureAt !== null) {
    const failingForMs = nowMs - health.oldestFailureAt
    const due = failingForMs >= thresholdMs && (lastWarnedAt === null || nowMs - lastWarnedAt >= thresholdMs)
    return { warn: due, recovered: false, failingForMs }
  }
  return { warn: false, recovered: lastWarnedAt !== null, failingForMs: 0 }
}

/**
 * Moves the auth folder aside (never deletes it) so a fresh QR pairing can start with an empty one.
 * Returns the archive path. Throws if the rename fails — the caller must then NOT recreate the folder.
 */
export async function archiveAuthDir(authDir, reason, { fs = nodeFs, now = Date.now } = {}) {
  const stamp = new Date(now()).toISOString().replace(/[:.]/g, '-')
  const archived = join(dirname(authDir), `${basename(authDir)}.${reason}-${stamp}`)
  await fs.rename(authDir, archived)
  await fs.mkdir(authDir, { recursive: true })
  return archived
}
