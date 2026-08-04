# Agent Progress

## 2026-08-04 — task 869edf3na (round 2, PR #47 review fixes)
Done: addressed both CHANGES REQUESTED gaps from the commit-4c0373f review.
(1) `deploy/remote_helpers.ps1`'s `Invoke-Rollback` now excludes `$ExcludeNames`
(server-only files: `appsettings.Production.json`/`.Secrets.json`,
`whatsappbridge.db*`) from both the pre-restore delete and the `robocopy` restore
(`/XF`), same as `Invoke-SwapBackend` already did — a rollback can no longer
silently overwrite the live DB with the pre-deploy snapshot. `Invoke-Backup`'s
robocopy also now excludes them, so the backup never captures the DB in the
first place. (2) `deploy/deploy.py`'s deploy loop now tracks `live_state_changed`
(set once the backend or frontend has actually been swapped into place) — any
`DeployAborted` raised after that point (e.g. a frontend upload failure once the
backend is already live) now triggers the same rollback + smoke-recheck path
that only `SmokeCheckFailed` used to trigger. The rollback path re-raises as
`SmokeCheckFailed` regardless of the original exception type, so the top-level
"(rolled back)" exit message stays accurate.
Verified: `python -m py_compile deploy/deploy.py` clean; PowerShell
`Parser::ParseFile` on `remote_helpers.ps1` reports zero syntax errors; reran
`python deploy/deploy.py --check-only` against the repo's real state — refuses
(exit 1) with no `Frontend/.env.production`, refuses (exit 1) with a deliberately
wrong `VITE_API_URL=http://localhost:5001`, and passes with the correct value;
backend gate (`--skip-frontend`) still publishes 60 files/29 DLLs and passes.
Not re-exercised live against production (no real deploy/rollback run this
session) — same limitation as round 1.
Left: nothing for this task. A first real deploy should still be human-watched
per round 1's note.

## 2026-08-04 — task 869edf3na
Done: replaced the old `deploy/deploy.py` (a single-DLL app_offline copy loop) with a
hardened deploy script that is now THE only supported deploy path (documented in new
`AGENTS.md`). It refuses to build/upload unless `Frontend/.env.production` declares exactly
`VITE_API_URL=https://whatsapp.wreckingball.ai` (checked against the known-correct URL, not
just self-consistency — an earlier version of the gate only checked the bundle against
whatever the file said, which would have missed the 2026-03-08 `:5001` wrong-port bug),
and separately verifies the built JS actually contains it. Uploads go to a temp remote
folder first (size-verified per file via SFTP `stat`), then a `deploy/remote_helpers.ps1`
helper (uploaded once per deploy, invoked over SSH) does the server-side move into place —
backend as a full delete-then-move-all (never a single-DLL swap), frontend assets before
`index.html`. Post-deploy smoke checks (`/api/version`, a probe login, the live bundle
asset, WhatsApp session continuity vs. a pre-deploy snapshot) trigger an automatic rollback
from a timestamped pre-deploy backup if any fail.
Verified: `dotnet build -c Release` clean (0 warnings/errors). Ran `python deploy/deploy.py
--check-only` against the repo's actual current state (`Frontend/.env.production` doesn't
exist yet — PR #46 not merged) and confirmed it refuses with exit code 1 before any network
code runs; also tested a wrong-port value, an empty value, and the correct value (passes,
exit 0) to confirm the gate catches both known incident classes. Remote SSH/rollback logic
(`remote_helpers.ps1`) is PowerShell-parse-checked but not exercised against the live
server — no real deploy was run against production in this session.
Left: this session did not merge or touch PR #46 (`.env.production` commit) — separate task,
still open. Once merged, `deploy/deploy.py` (no flags) becomes usable end-to-end; until then
`--check-only` is what proves the gate. A first real deploy should be run and watched by a
human before agents rely on the rollback path unattended.

## 2026-08-04 — task 869edf485
Done: PR #48 — new OutboundGuardrailService gates sendMessage/sendMedia/forwardMessage:
allow-listed recipients (default: Martien only) always pass, anyone else only within quiet
hours (default 08:00-21:00 server local time). Blocked sends return 403 and persist to a new
BlockedOutboundMessages table (self-healed via CREATE TABLE IF NOT EXISTS, same pattern as
the Messages table), readable via GET /api/wa/blockedOutbound. Companion client-side fix in
scp-jengo/jengo-agi#59.
Verified: dotnet build clean; ran the app against a fresh throwaway SQLite DB with a seeded
user+API connection and live-curled sendMessage — non-allow-listed recipient outside quiet
hours blocked (403), same recipient allowed when quiet hours widened to cover current time,
allow-listed recipient always passes, unauthenticated request still 401, blocked attempt
correctly surfaced via GET /api/wa/blockedOutbound.
Left: nothing.

## 2026-08-04 — task 869edf3k4
Done: root-caused "0 isHistory rows after fresh re-pair" via log evidence and a live
on-demand test. The 2026-08-03 QR re-pair completed at 21:35:38 UTC; the app pool was
recycled ~7 min later (21:42, coincident with an unrelated deploy) before WhatsApp ever
sent a HISTORY_SYNC_NOTIFICATION — confirmed via log analysis: the 7 self/peer control
messages received in that window all decrypted and parsed successfully but none matched
`ProtocolMessage.Type == TYPE_HISTORY_SYNC_NOTIFICATION` (the unconditional
`_logger.LogInformation("HistorySync: received ...")` in `ProcessHistorySyncAsync` never
fired). Live-tested the #42 on-demand path end-to-end via `POST /api/wa/requestHistory`
against the Martien chat (existing anchor `6451E50C1D428021`): request construction and
delivery are healthy (anchor resolved from SQLite, encrypted+sent to both own devices),
but the phone never returned a `PeerDataOperationResponseMessage` within two 35s windows
— confirmed no code-side bug in the send path; this needs the phone to actually respond,
which is outside agent control (matches 869ecy6kp's own note: "patience for full device
sync (hours)").
Fixed along the way: (1) the "skip other protocol messages" logs on both decrypt paths
(`NoiseProcessor.cs`) were `LogDebug`, invisible under prod's `"Default": "Information"`
level — this is exactly what made "did a HISTORY_SYNC_NOTIFICATION arrive?" unanswerable
from the deployed logs; bumped to `LogInformation`. (2) `HistorySyncNotification.SyncType`
constants had RECENT/FULL and ON_DEMAND/NON_BLOCKING_DATA swapped vs WhatsApp's real enum
(cosmetic — only feeds a log label — but actively misleading); fixed and added a
regression self-test. (3) Removed `HandleHistorySyncAsync`, a fully separate, never-called
CDN-download+decrypt+parse implementation that duplicated the live `ProcessHistorySyncAsync`
path — confirmed dead via `grep`, this is the "does a code path bypass StoreMessage" check
the task asked for; it doesn't (the dead path was never reachable), but it was pure
confusion risk during debugging.
Verified: `dotnet build` clean on Backend/Dawa/DawaTest (0 warnings/errors from this
change). `dotnet DawaTest.dll --selftest`: 9/9 pass (8 pre-existing + 1 new SyncType
regression test). Root cause + on-demand test were verified live against production
(85.215.217.154 / api.whatsapp.wreckingball.ai) via direct log reads and a real
`requestHistory` call — not simulated.
Left: the actual delivery (isHistory=1 rows appearing, Frank's chat showing up) requires
either the phone to respond to a future on-demand request or a fresh re-pair that survives
long enough for WhatsApp's INITIAL_BOOTSTRAP push to arrive — neither is something this
session can force. The always-on `HistoryMessageReceived += StoreMessage(isHistory:true)`
listener (`WhatsAppBridgeService.cs:78,156`) is correctly wired and will persist whatever
arrives, whenever it arrives, with no code change needed on that side.

## 2026-07-24 — task 869e8wk1v
Done: added deploy-time version tracking — repo-root `VERSION` file, `<Version>` in
`WhatsAppBridge.API.csproj`, a `GET /api/version` endpoint, and `deploy/bump-version.ps1`
(bumps VERSION + csproj, commits, tags `vX.Y.Z`, pushes) wired into `deploy-all.ps1` step 1/5.
Verified: `dotnet build -c Release` clean (0 warnings/errors beyond pre-existing Dawa XML-doc
warnings); ran the built DLL locally and confirmed `GET /api/version` returns
`{"version":"1.0.0.0","buildTimeUtc":"..."}`.
Left: nothing for this task. The messy `/deploy` folder has many one-off troubleshooting
scripts from past manual VPS deploys — `deploy-all.ps1` may not be the actual script last
used to deploy to production; worth confirming with Martien which deploy path is live.

## 2026-08-03 — task 869ecw8du
Done: chat list now shows `[foto]`/`[audio]`/etc. instead of a blank line when a media
message has no body (store/chats gained `lastType`). Thread renders a media chip instead
of the dead encrypted mmg.whatsapp.net link — clicking it calls a new
`GET store/messages/media` endpoint that decrypts the CDN blob server-side via Dawa's
existing `DownloadMediaAsync`. MediaKey/MimeType are now captured at ingest and persisted
(self-healing `ALTER TABLE ADD COLUMN`, PR #16's already-merged CDN work). Rows from
before this change have no key, so they render a disabled "media niet beschikbaar" label
instead of a button (`mediaAvailable: false`). Text-only rendering untouched.
Verified: backend `dotnet build` clean (0 warnings/errors); frontend `tsc --noEmit` +
`npm run build` clean. Ran the app against a simulated pre-existing DB (old Messages
schema, no MediaKey/MimeType) and confirmed the self-heal ALTER TABLEs applied without
error and the legacy row survived untouched. Ran the app against a fresh DB, registered
a user, seeded one text + one legacy media (no key) + one new media (with key) message,
and hit the live endpoints: `store/chats` returned the correct `lastType` per chat,
`store/messages` returned `mediaAvailable: false` for the legacy row and `true` for the
new one, and `store/messages/media` returned 404 for the unavailable row, a graceful 502
(not a crash) for the new row's unreachable fake CDN URL, and 404 for an unknown message.
Left: no browser/Playwright tool was available in this session, so the actual chip
rendering in the page was not visually confirmed — only the API contract it renders
from, plus a clean typecheck/build.

## 2026-08-03 — task 869ecw8e4
Done: investigated "messages sent to Frank never reached the bridge" — found zero POST
requests to any WhatsApp send endpoint in jengo-agi's HttpClient logs across two full days
spanning the report window, and zero rows for Frank's number in the durable `Messages`
table (confirmed live via `getChats`/`getMessages` against the deployed bridge). The
dashboard's `/messages` page also has no "start new conversation" affordance — a first
message to a contact must go through the send API directly, and none did. Concluded the
message was sent from Martien's own personal phone (a separate WhatsApp account the bridge
structurally cannot see), not a code bug. Documented this in README.md's Troubleshooting
section so the same confusion doesn't recur.
Verified: not independently testable (no code path changed) — conclusion is based on a live
read-back of the deployed bridge's `getChats`/`getMessages` API and jengo-agi's own request
logs, not a build/test run.
Left: nothing for this task.

## 2026-08-03 — task 869ecw8dq
Done: fixed the malformed `<ack>` stanza in `SendAckAsync` (was missing the `class`
attribute WhatsApp uses to identify which stanza is being acked — confirmed against the
real `@whiskeysockets/baileys` `sendMessageAck` source; this is why WhatsApp never stopped
redelivering ANY message, decryptable or not — confirmed live: even the successfully
decrypted `:90@lid` image kept reappearing with a climbing `offline` counter). Added
identity-mismatch detection in `DecryptWhisperMessage`: a session now stores the identity
key it was established under, and a MAC FAIL is checked against our CURRENT identity — if
they differ (a re-pair happened since), the session is dropped so the next delivery falls
through to a retry receipt instead of failing forever. Also capped the per-message backlog
ACK log noise (5 full log lines, then debug-only) so one broken session can't keep
inflating the 188MB signal-debug.log for days.
Verified: `dotnet build` clean on Dawa/API/DawaTest (0 errors/warnings beyond pre-existing
XML-doc warnings). Added 2 new self-tests to `DawaTest/Program.cs` (run via
`dotnet DawaTest.dll --selftest`): stale-identity MAC FAIL correctly drops the session
(InvalidOperationException, session gone); ordinary same-identity MAC FAIL correctly
leaves the session intact (CryptographicException, session kept) — proves the fix doesn't
over-drop on harmless/transient failures. 4/4 self-tests pass.
Left: end-to-end confirmation (a real text from Martien's phone landing in /messages, and
the two stuck message ids ACE87B93.../AC75A7A7... draining from the offline queue) needs
this deployed to 85.215.217.154 and a live test message — that's a human/deploy step, not
something this session can trigger.

## 2026-08-03 — task 869ecw8ed
Done: watchdog (`tools/wa-watchdog/watchdog.ps1`) no longer alerts on 401 events from a
dead/legacy session while the real session is connected, and its own health check now
uses the long-lived `waToken` ApiConnection (X-Api-Token/X-Email) instead of the rotating
frontend password. PR #39.
Verified: PowerShell syntax parse clean; standalone logic test of the 401-gating decision
(4 cases) all pass; live end-to-end run on production (85.215.217.154) — new auth succeeds,
no regressions, no false alert.
Left: deployed straight to production (this script has no CI/CD, it's a manual-copy
scheduled task) alongside opening the PR — `bridgePassword` removed from prod config.json
since it's no longer read. Nothing else remains; genuine session-death alerting could not
be tested live (would require an actual WA logout) but the gating logic itself is unit-tested.

## 2026-08-03 — task 869ecy6kp
Done: fixed the root cause of "requestHistory returned success but delivered nothing" —
`WhatsAppBridgeService.RequestOnDemandHistoryAsync` already computed the backfill anchor
(oldest stored message id/fromMe/timestamp) but then silently discarded it, calling
`RequestOnDemandHistorySyncAsync(jid, count, ct)` with no anchor at all; the phone then had
no reference point for "older than what?" and just re-sent the newest messages instead of
paging back. Threaded the anchor through the full call chain (`WhatsAppBridgeService` →
`WhatsAppClient` → `NoiseProcessor.RequestOnDemandHistorySyncAsync` →
`SendPeerDataOperationRequestAsync`) and added the missing `oldestMsgTimestampMs` proto
field (field 5 of `historySyncOnDemandRequest`) that the encoder never had at all. Also
fixed the retry-resend path (`_lastPdoRequest`), which hardcoded `null/false/0` for the
anchor on every resend. Sourced the anchor from the durable SQLite `Messages` table (falling
back to the in-memory cache) instead of only the in-memory store, since that store is empty
right after an app-pool restart even though history already exists in SQLite, and spans
every SessionId the user has re-paired under. Wired a "Oudere berichten laden" button into
the `/messages` page (Messages.tsx) so the endpoint is actually reachable per-chat, not just
via raw `curl`. Confirmed via code read that "media in history: store type + mediaUrl like
live messages" was already shipped symmetrically by task 869ecbkv7 — no change needed there.
Verified: `dotnet build` clean on Backend/Dawa/DawaTest (0 errors). Added 4 new self-tests
to `DawaTest/Program.cs` (`dotnet DawaTest.dll --selftest`): anchor fields are present in
the serialized proto bytes; the `historySyncOnDemandRequest` sub-message round-trips with
fields 2/3/5 all encoded; a no-anchor request correctly omits those fields (regression guard
for the existing "first sync" behavior). 8/8 self-tests pass (4 new + 4 pre-existing).
`npm run build` (tsc + vite) clean on Frontend.
Left: the task's own DoD requires a QR re-pair by Martien (a physical phone action this
session cannot perform) before "multiple chats with recent history visible on /messages,
survives restart" can be verified live — that verification step is explicitly called out
as needing "patience for full device sync (hours)" per the 2026-07-06 lesson referenced in
the task. Code changes are ready for review independent of that step.

## 2026-08-03 — task 869edbxpx
Done: PR #43 (own durable messages by UserId, self-heal + backfill, all read endpoints
updated) was already deployed and live-verified when this session's dispatch fetched a
0-comment task history — the local `master` checkout was 2 commits behind at search time,
which hid #43 from every "existing PR" check. Independently re-implemented the same fix
before discovering #43 mid-session, opened a duplicate PR (#44), then found #43 via the
mandatory pre-report re-verification and reconciled: closed #44 (it also missed the
`GetStoredMessageMedia` endpoint that #43 correctly covered — same stale-checkout gap),
pushed one small addition to #43 (encoded the one-time 6-orphaned-row backfill as
idempotent self-heal SQL instead of a manual command that only lived in a ClickUp comment),
and merged #43 as 88e23e3.
Verified: `dotnet build` clean; reproduced the incident in a throwaway SQLite DB and
confirmed the old SessionId-only filter surfaces 2 messages while the new UserId filter
surfaces all 8; #43's own CI failures (discover-tests, static-analysis) are pre-existing
repo-wide misconfiguration (no test projects, no root solution file) unrelated to this diff.
Left: nothing. Always `git fetch && git merge --ff-only origin/<base>` before trusting a
grep/gh-pr-list search for "does a PR already exist" — a stale local checkout silently
narrows every one of those searches.
