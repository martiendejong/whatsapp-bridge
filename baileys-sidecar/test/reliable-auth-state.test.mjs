import { test } from 'node:test'
import assert from 'node:assert/strict'
import { promises as realFs } from 'node:fs'
import { mkdtemp, readFile, readdir, writeFile, mkdir } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  useReliableAuthState, AuthStateCorruptError, archiveAuthDir, evaluateAuthHealth,
} from '../reliable-auth-state.js'

// Minimal stand-ins for the Baileys helpers (the module takes them by injection).
const BufferJSON = {
  replacer: (_k, v) => (v && v.type === 'Buffer' && Array.isArray(v.data) ? { type: 'Buffer', data: Buffer.from(v.data).toString('base64') } : v),
  reviver: (_k, v) => (v && v.type === 'Buffer' && typeof v.data === 'string' ? Buffer.from(v.data, 'base64') : v),
}
const baileys = { BufferJSON, initAuthCreds: () => ({ fresh: true, registered: false }), proto: undefined }

const scratch = () => mkdtemp(join(tmpdir(), 'auth-test-'))
const fast = { retryDelaysMs: [1, 1], writeTimeoutMs: 200, pendingRetryMs: 3_600_000 }

/** fs wrapper whose behaviour can be switched per test: rename can fail, open can hang. */
function flakyFs() {
  const ctl = { renameFailures: 0, hangOpen: false, renames: 0 }
  const fs = {
    ...realFs,
    rename: async (a, b) => {
      ctl.renames++
      if (ctl.renameFailures > 0) { ctl.renameFailures--; throw Object.assign(new Error('EPERM: operation not permitted'), { code: 'EPERM' }) }
      return realFs.rename(a, b)
    },
    open: (...args) => (ctl.hangOpen ? new Promise(() => {}) : realFs.open(...args)),
  }
  return { fs, ctl }
}

test('writes are atomic: no temp files left, content round-trips (Buffers included)', async () => {
  const dir = await scratch()
  const auth = await useReliableAuthState(dir, baileys, { ...fast })
  auth.state.creds.secret = Buffer.from('hello')
  await auth.saveCreds()
  auth.close()

  assert.deepEqual((await readdir(dir)).sort(), ['creds.json'])
  const back = JSON.parse(await readFile(join(dir, 'creds.json'), 'utf8'), BufferJSON.reviver)
  assert.equal(back.secret.toString(), 'hello')
})

test('transient rename failure is retried and succeeds without being reported', async () => {
  const dir = await scratch()
  const { fs, ctl } = flakyFs()
  const errors = []
  const auth = await useReliableAuthState(dir, baileys, { ...fast, fs, onWriteError: (f, e) => errors.push(f) })
  ctl.renameFailures = 2
  await auth.saveCreds()
  auth.close()

  assert.equal(errors.length, 0)
  assert.equal(auth.getHealth().pendingCount, 0)
  assert.ok(JSON.parse(await readFile(join(dir, 'creds.json'), 'utf8')).fresh)
})

test('persistent failure is surfaced, kept in memory, readable, and flushed once the disk recovers', async () => {
  const dir = await scratch()
  const { fs, ctl } = flakyFs()
  const errors = []
  const auth = await useReliableAuthState(dir, baileys, { ...fast, fs, onWriteError: (f, e) => errors.push([f, e.code]) })

  ctl.renameFailures = 1000
  await auth.state.keys.set({ 'pre-key': { 7: { k: 'v' } } })

  assert.deepEqual(errors, [['pre-key-7.json', 'EPERM']])
  const health = auth.getHealth()
  assert.equal(health.pendingCount, 1)
  assert.ok(health.oldestFailureAt > 0)
  assert.match(health.lastError, /pre-key-7/)
  // Not on disk, but a read still sees it (nothing is lost while the disk is unhappy).
  assert.deepEqual(await auth.state.keys.get('pre-key', ['7']), { 7: { k: 'v' } })

  ctl.renameFailures = 0
  const { pending } = await auth.flush(2000)
  auth.close()
  assert.equal(pending, 0)
  assert.deepEqual(JSON.parse(await readFile(join(dir, 'pre-key-7.json'), 'utf8')), { k: 'v' })
})

test('a hung write times out and does not block later writes to the same file', async () => {
  const dir = await scratch()
  const { fs, ctl } = flakyFs()
  const auth = await useReliableAuthState(dir, baileys, { ...fast, retryDelaysMs: [], fs })

  ctl.hangOpen = true
  const t0 = Date.now()
  await auth.saveCreds() // hangs inside fs.open -> timeout -> pending
  assert.ok(Date.now() - t0 < 2000, 'must not wait forever')
  assert.equal(auth.getHealth().pendingCount, 1)

  ctl.hangOpen = false
  auth.state.creds.second = true
  await auth.saveCreds() // newer write goes through
  auth.close()
  assert.equal(auth.getHealth().pendingCount, 0)
  assert.equal(JSON.parse(await readFile(join(dir, 'creds.json'), 'utf8')).second, true)
})

test('removing a key never gets undone by an older pending write', async () => {
  const dir = await scratch()
  const { fs, ctl } = flakyFs()
  const auth = await useReliableAuthState(dir, baileys, { ...fast, fs })
  await auth.state.keys.set({ session: { a: { x: 1 } } })
  ctl.renameFailures = 1000
  await auth.state.keys.set({ session: { a: { x: 2 } } }) // pending
  ctl.renameFailures = 0
  await auth.state.keys.set({ session: { a: null } })     // removal
  auth.close()

  assert.equal(auth.getHealth().pendingCount, 0)
  assert.deepEqual(await auth.state.keys.get('session', ['a']), { a: null })
  assert.ok(!(await readdir(dir)).includes('session-a.json'))
})

test('corrupt creds.json is reported, not overwritten', async () => {
  const dir = await scratch()
  await writeFile(join(dir, 'creds.json'), '{"truncated": ')
  await assert.rejects(() => useReliableAuthState(dir, baileys, { ...fast }), AuthStateCorruptError)
  assert.equal(await readFile(join(dir, 'creds.json'), 'utf8'), '{"truncated": ')
})

test('loadStatus: fresh, resumed, orphaned', async () => {
  const fresh = await useReliableAuthState(await scratch(), baileys, { ...fast })
  assert.equal(fresh.loadStatus, 'fresh'); fresh.close()

  const d1 = await scratch()
  await writeFile(join(d1, 'creds.json'), JSON.stringify({ registered: true }))
  const resumed = await useReliableAuthState(d1, baileys, { ...fast })
  assert.equal(resumed.loadStatus, 'resumed')
  assert.equal(resumed.state.creds.registered, true); resumed.close()

  const d2 = await scratch()
  await writeFile(join(d2, 'session-1.json'), '{}')
  const orphan = await useReliableAuthState(d2, baileys, { ...fast })
  assert.equal(orphan.loadStatus, 'orphaned'); orphan.close()
})

test('evaluateAuthHealth: warns after the threshold, once per threshold, and reports recovery', () => {
  const T = 10 * 60_000
  const failing = (since) => ({ pendingCount: 1, oldestFailureAt: since, lastError: 'x', lastOkAt: null })
  assert.equal(evaluateAuthHealth(failing(0), 9 * 60_000, { thresholdMs: T, lastWarnedAt: null }).warn, false)
  assert.equal(evaluateAuthHealth(failing(0), T, { thresholdMs: T, lastWarnedAt: null }).warn, true)
  assert.equal(evaluateAuthHealth(failing(0), T + 60_000, { thresholdMs: T, lastWarnedAt: T }).warn, false)
  assert.equal(evaluateAuthHealth(failing(0), 2 * T, { thresholdMs: T, lastWarnedAt: T }).warn, true)
  const ok = { pendingCount: 0, oldestFailureAt: null, lastError: null, lastOkAt: 1 }
  assert.deepEqual(evaluateAuthHealth(ok, 3 * T, { thresholdMs: T, lastWarnedAt: T }), { warn: false, recovered: true, failingForMs: 0 })
  assert.equal(evaluateAuthHealth(ok, 3 * T, { thresholdMs: T, lastWarnedAt: null }).recovered, false)
})

test('archiveAuthDir moves the folder aside (files kept) and leaves an empty one', async () => {
  const root = await scratch()
  const authDir = join(root, 'baileys-auth')
  await mkdir(authDir)
  await writeFile(join(authDir, 'creds.json'), '{"old":true}')

  const archived = await archiveAuthDir(authDir, 'logged-out')

  assert.match(archived, /baileys-auth\.logged-out-/)
  assert.equal(await readFile(join(archived, 'creds.json'), 'utf8'), '{"old":true}')
  assert.deepEqual(await readdir(authDir), [])
})
