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
