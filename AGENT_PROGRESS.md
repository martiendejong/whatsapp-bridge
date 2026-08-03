# Agent Progress

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
