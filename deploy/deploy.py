#!/usr/bin/env python3
"""
WhatsApp Bridge — hardened deploy script. THE only supported deploy path
(see AGENTS.md). Used by humans and agents alike.

Why this exists: login broke twice (2026-08-01, 2026-08-04) because a frontend
rebuild produced a bundle without the API base URL, and a partial/ghost file
upload once served a 500.30. Nothing verified a deploy before it went live, and
nothing could undo one automatically. This script:

  1. Builds the backend (full `dotnet publish`) and the frontend, and REFUSES
     to upload if the built JS bundle does not contain the required API base
     URL (`build_frontend` / `verify_bundle_has_api_url`).
  2. Uploads to temp remote folders first, verifies every uploaded file's size
     against the local file, then does a server-side move into place — never
     serves a partially-uploaded file (see `sftp_upload_tree`, `remote_helpers.ps1`).
  3. Always replaces the FULL backend DLL set (delete-then-move-all), never a
     single-DLL swap — that is what caused the 2026-07-31 500.30 incident.
     Server-only config/db files are excluded from both the upload and the
     delete (`BACKEND_CONFIG_EXCLUDE`).
  4. Runs post-deploy smoke checks against the live public URL (version, a
     probe login, the live bundle asset, WhatsApp session continuity) and
     automatically rolls back from the pre-deploy backup if any check fails.

Usage:
    python deploy/deploy.py --check-only     # build + gate check only, no network — use this
                                              # to prove a broken bundle gets refused
    python deploy/deploy.py                  # full deploy, asks for confirmation
    python deploy/deploy.py --yes            # full deploy, no confirmation prompt (for agents)
    python deploy/deploy.py --skip-backend   # frontend-only deploy
    python deploy/deploy.py --skip-frontend  # backend-only deploy

Credentials (never hardcoded — set as environment variables before a real deploy):
    WHATSAPPBRIDGE_DEPLOY_SSH_PASSWORD   administrator@85.215.217.154
                                          vault: "Production Server (IIS)" / "Applications
                                          Production Server" (Prospergenics vault, project 6)
    WHATSAPPBRIDGE_PROBE_EMAIL           optional, defaults to info@martiendejong.nl
    WHATSAPPBRIDGE_PROBE_PASSWORD        vault: "WhatsApp Bridge frontend login (martiendejong)"
                                          (Prospergenics vault, project 4/"websites", id 52)
If not set and stdin is a real terminal, the script prompts (no echo). In a non-interactive
agent session, export them first.
"""

import argparse
import getpass
import os
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
BACKEND_PROJECT_DIR = REPO_ROOT / "Backend" / "WhatsAppBridge.API"
FRONTEND_DIR = REPO_ROOT / "Frontend"
ENV_PRODUCTION_FILE = FRONTEND_DIR / ".env.production"
REMOTE_HELPER_LOCAL_PATH = REPO_ROOT / "deploy" / "remote_helpers.ps1"

FRONTEND_URL = "https://whatsapp.wreckingball.ai"
API_URL = "https://api.whatsapp.wreckingball.ai"
PUBLIC_URL = FRONTEND_URL  # kept for smoke-check back-compat (version, probe login, bundle)
SSH_HOST = "85.215.217.154"
SSH_PORT = 22
SSH_USER = "administrator"

REMOTE_BACKEND_DIR = "C:\\inetpub\\whatsappbridge-api"
REMOTE_FRONTEND_DIR = "C:\\inetpub\\whatsappbridge-web"
REMOTE_BACKUP_ROOT = "C:\\inetpub\\_deploy_backups"
REMOTE_HELPER_PATH = "C:\\inetpub\\_deploy_helpers\\remote_helpers.ps1"

# Files that live ONLY on the server (secrets, the runtime DB) and must never be
# uploaded or deleted by a deploy. Keep this in sync with remote_helpers.ps1's
# $ExcludeNames default.
BACKEND_CONFIG_EXCLUDE = frozenset({
    "appsettings.Production.json",
    "appsettings.Secrets.json",
    "whatsappbridge.db",
    "whatsappbridge.db-shm",
    "whatsappbridge.db-wal",
})

# A real `dotnet publish` of this project produces ~60 files / ~33 DLLs. These floors
# only need to catch a broken/partial publish (e.g. a handful of files) — not track the
# exact count as dependencies are added.
MIN_EXPECTED_BACKEND_FILES = 30
MIN_EXPECTED_BACKEND_DLLS = 15

KEEP_BACKUPS = 5


class DeployAborted(Exception):
    """A pre-upload gate failed. Nothing on the server has been touched."""


class SmokeCheckFailed(Exception):
    """A post-deploy check failed. Triggers automatic rollback."""


def log(msg):
    print(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}", flush=True)


def run(cmd, cwd):
    log(f"$ {' '.join(cmd)}  (cwd={cwd})")
    result = subprocess.run(cmd, cwd=str(cwd), shell=(os.name == "nt"), capture_output=True, text=True)
    if result.stdout.strip():
        print(result.stdout)
    if result.stderr.strip():
        print(result.stderr, file=sys.stderr)
    if result.returncode != 0:
        raise DeployAborted(f"Command failed ({result.returncode}): {' '.join(cmd)}")
    return result


# ---------------------------------------------------------------------------
# Build + gate checks. Pure local work, no network access. A real deploy never
# reaches the network unless BOTH of these succeed.
# ---------------------------------------------------------------------------

def build_backend():
    log("[backend] dotnet publish (full, Release, no-self-contained)...")
    publish_dir = REPO_ROOT / "deploy" / "_publish" / "backend"
    if publish_dir.exists():
        shutil.rmtree(publish_dir)
    run(["dotnet", "publish", "-c", "Release", "-o", str(publish_dir), "--no-self-contained"], cwd=BACKEND_PROJECT_DIR)

    dll_count = len(list(publish_dir.glob("*.dll")))
    total_count = sum(1 for p in publish_dir.rglob("*") if p.is_file())
    log(f"[backend] publish produced {total_count} files, {dll_count} DLLs")
    if dll_count < MIN_EXPECTED_BACKEND_DLLS or total_count < MIN_EXPECTED_BACKEND_FILES:
        raise DeployAborted(
            f"Backend publish looks incomplete ({total_count} files, {dll_count} DLLs — expected "
            f">= {MIN_EXPECTED_BACKEND_FILES} files / {MIN_EXPECTED_BACKEND_DLLS} DLLs). Refusing to "
            f"deploy a partial DLL set — this is exactly how the 2026-07-31 500.30 incident happened."
        )
    entry_dll = publish_dir / "WhatsAppBridge.API.dll"
    if not entry_dll.exists():
        raise DeployAborted(f"Publish output is missing the entry assembly: {entry_dll}")
    return publish_dir


def _validate_env_production():
    """Validates Frontend/.env.production declares exactly the correct production API URL,
    and returns it. This is the gate that would have caught BOTH known incidents: a missing
    file (2026-08-01, 2026-08-04 — embeds an empty URL) AND a wrong-but-present value (the
    2026-03-08 incident, an extra ':5001' that bypassed IIS entirely). Checking the built
    bundle against whatever the file happens to say is not enough — it has to be checked
    against the known-correct URL, or a wrong value just passes its own self-consistency
    check."""
    if not ENV_PRODUCTION_FILE.exists():
        raise DeployAborted(
            f"{ENV_PRODUCTION_FILE} does not exist. A build right now would embed an EMPTY "
            f"API base URL — this is the exact bug that broke login on 2026-08-01 and "
            f"2026-08-04. Create it with VITE_API_URL={API_URL} before deploying."
        )
    value = None
    for line in ENV_PRODUCTION_FILE.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith("VITE_API_URL="):
            value = line.split("=", 1)[1].strip()
            break
    if value != API_URL:
        raise DeployAborted(
            f"{ENV_PRODUCTION_FILE} has VITE_API_URL={value!r}, expected {API_URL!r}. This is "
            f"the same class of bug that broke login before (e.g. the 2026-03-08 incident, where "
            f"an extra ':5001' bypassed IIS entirely). Fix the file before deploying."
        )
    return value


def verify_bundle_has_api_url(dist_dir, required_url):
    if not required_url:
        raise DeployAborted("No required API base URL to gate against — refusing to deploy.")

    js_files = [p for p in dist_dir.rglob("*.js") if p.is_file()]
    if not js_files:
        raise DeployAborted(f"No .js files found under {dist_dir} — build did not produce a usable bundle.")

    for f in js_files:
        try:
            text = f.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        if required_url in text:
            log(f"[frontend] OK: API base URL found in {f.relative_to(dist_dir)}")
            return

    raise DeployAborted(
        f"REFUSING TO DEPLOY: none of the {len(js_files)} built JS file(s) contain the required API "
        f"base URL ({required_url!r}). This is the exact bug that broke login on 2026-08-01 and "
        f"2026-08-04 — a rebuild without Frontend/.env.production (or with the wrong VITE_API_URL) "
        f"silently produces a bundle that calls same-origin '/api' and fails. Fix "
        f"Frontend/.env.production and rebuild before deploying."
    )


def build_frontend():
    required_url = _validate_env_production()
    log(f"[frontend] required API base URL in bundle: {required_url!r}")

    if not (FRONTEND_DIR / "node_modules").exists():
        log("[frontend] node_modules missing, running npm install...")
        run(["npm", "install"], cwd=FRONTEND_DIR)

    log("[frontend] npm run build (tsc && vite build)...")
    run(["npm", "run", "build"], cwd=FRONTEND_DIR)

    dist_dir = FRONTEND_DIR / "dist"
    if not dist_dir.exists():
        raise DeployAborted(f"Frontend build did not produce {dist_dir}")

    verify_bundle_has_api_url(dist_dir, required_url)
    return dist_dir, required_url


# ---------------------------------------------------------------------------
# Remote (SSH/SFTP) helpers. Only imported/used for a real deploy, never for
# --check-only, so the gate above works even without paramiko/requests installed.
# ---------------------------------------------------------------------------

def _get_secret(env_var, prompt_label):
    val = os.environ.get(env_var)
    if val:
        return val
    if sys.stdin.isatty():
        return getpass.getpass(f"{prompt_label}: ")
    raise DeployAborted(
        f"{env_var} is not set and there is no interactive terminal to prompt for it. "
        f"Export it from vault before running a real deploy (see the module docstring / AGENTS.md)."
    )


def connect_ssh(password):
    import paramiko
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(SSH_HOST, port=SSH_PORT, username=SSH_USER, password=password, timeout=30)
    return client


def ssh_exec(ssh, command, description=None):
    if description:
        log(f"[remote] {description}")
    stdin, stdout, stderr = ssh.exec_command(command, get_pty=True)
    exit_code = stdout.channel.recv_exit_status()
    out = stdout.read().decode("utf-8", errors="ignore")
    err = stderr.read().decode("utf-8", errors="ignore")
    if out.strip():
        print(out)
    if exit_code != 0:
        raise RuntimeError(f"Remote command failed (exit {exit_code}): {command}\n{err}")
    return out


def upload_helper(sftp):
    _ensure_remote_dir(sftp, "\\".join(REMOTE_HELPER_PATH.split("\\")[:-1]))
    sftp.put(str(REMOTE_HELPER_LOCAL_PATH), REMOTE_HELPER_PATH)


def run_helper(ssh, action, **params):
    args = [f"-Action {action}"]
    for key, value in params.items():
        if isinstance(value, (list, tuple, frozenset, set)):
            joined = ",".join(f'\\"{item}\\"' for item in value)
            args.append(f"-{key} @({joined})")
        else:
            args.append(f'-{key} "{value}"')
    command = (
        f'powershell -NoProfile -ExecutionPolicy Bypass -File "{REMOTE_HELPER_PATH}" ' + " ".join(args)
    )
    return ssh_exec(ssh, command, description=f"remote_helpers.ps1 -Action {action}")


def _ensure_remote_dir(sftp, remote_dir):
    remote_dir = remote_dir.rstrip("\\/")
    segments = remote_dir.split("\\")
    current = segments[0]
    for seg in segments[1:]:
        current = current + "\\" + seg
        try:
            sftp.mkdir(current)
        except OSError:
            pass  # already exists — a real failure surfaces later when we sftp.put into it


def sftp_upload_tree(sftp, local_dir: Path, remote_dir: str, exclude_names=frozenset()):
    """Uploads every file under local_dir into a (temp) remote_dir, verifying each
    uploaded file's size against the local size before moving on. Never touches the
    live directory — callers move the temp dir into place afterward."""
    remote_dir = remote_dir.rstrip("\\/")
    files = [p for p in sorted(local_dir.rglob("*")) if p.is_file() and p.name not in exclude_names]

    needed_dirs = set()
    for f in files:
        rel_parts = f.relative_to(local_dir).parts
        needed_dirs.add(remote_dir if len(rel_parts) == 1 else remote_dir + "\\" + "\\".join(rel_parts[:-1]))
    for d in sorted(needed_dirs, key=len):
        _ensure_remote_dir(sftp, d)

    uploaded = []
    for f in files:
        rel_parts = f.relative_to(local_dir).parts
        remote_path = remote_dir + "\\" + "\\".join(rel_parts)
        sftp.put(str(f), remote_path)
        remote_size = sftp.stat(remote_path).st_size
        local_size = f.stat().st_size
        if remote_size != local_size:
            raise DeployAborted(
                f"Upload verification failed for {'/'.join(rel_parts)}: local {local_size} bytes, "
                f"remote {remote_size} bytes. The live site has not been touched — safe to retry."
            )
        uploaded.append(remote_path)
    log(f"[upload] {len(uploaded)} file(s) uploaded to {remote_dir}, all sizes verified")
    return uploaded


# ---------------------------------------------------------------------------
# Post-deploy smoke checks.
# ---------------------------------------------------------------------------

def smoke_check_version(base_url):
    import requests
    r = requests.get(f"{base_url}/api/version", timeout=15)
    if r.status_code != 200:
        raise SmokeCheckFailed(f"GET /api/version returned {r.status_code}, expected 200")
    log(f"[smoke] /api/version OK: {r.json()}")


def smoke_check_login(base_url, email, password):
    import requests
    r = requests.post(f"{base_url}/api/auth/login", json={"email": email, "password": password}, timeout=15)
    if r.status_code != 200:
        raise SmokeCheckFailed(f"POST /api/auth/login returned {r.status_code}, expected 200")
    token = r.json().get("token")
    log(f"[smoke] /api/auth/login OK{'' if token else ' (2FA pending, no token)'}")
    return token


def smoke_check_bundle_asset(base_url, dist_dir, required_url):
    import requests
    js_files = sorted(p for p in dist_dir.rglob("*.js") if p.is_file())
    asset = next((f for f in js_files if f.name.startswith("index")), js_files[0])
    rel = str(asset.relative_to(dist_dir)).replace("\\", "/")

    r = requests.get(f"{base_url}/{rel}", timeout=15)
    if r.status_code != 200:
        raise SmokeCheckFailed(f"GET /{rel} (live bundle asset) returned {r.status_code}, expected 200")
    if required_url not in r.text:
        raise SmokeCheckFailed(f"Live bundle asset /{rel} does not contain the required API URL {required_url!r}")
    log(f"[smoke] live bundle asset /{rel} OK, contains API URL")


def get_connected_session_ids(base_url, email, password):
    import requests
    token = smoke_check_login(base_url, email, password)
    if not token:
        return set(), token
    r = requests.get(f"{base_url}/api/whatsapp/sessions", headers={"Authorization": f"Bearer {token}"}, timeout=15)
    if r.status_code != 200:
        raise SmokeCheckFailed(f"GET /api/whatsapp/sessions returned {r.status_code}, expected 200")
    sessions = r.json()
    connected = {s["sessionId"] for s in sessions if str(s.get("status", "")).lower() in ("connected", "open")}
    return connected, token


# ---------------------------------------------------------------------------
# Orchestration.
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check-only", action="store_true",
                         help="Run the build + gate checks locally and exit. Never touches the server.")
    parser.add_argument("--yes", action="store_true", help="Skip the confirmation prompt.")
    parser.add_argument("--skip-backend", action="store_true")
    parser.add_argument("--skip-frontend", action="store_true")
    args = parser.parse_args()

    log("WhatsApp Bridge deploy — gates: API-URL bundle check, size-verified uploads, "
        "full DLL-set swap, post-deploy smoke + auto-rollback")

    backend_publish_dir = None
    frontend_dist_dir = None
    required_url = None

    if not args.skip_backend:
        backend_publish_dir = build_backend()
    if not args.skip_frontend:
        frontend_dist_dir, required_url = build_frontend()

    log("Build + gate checks passed.")

    if args.check_only:
        log("--check-only set — stopping here, nothing was uploaded.")
        return 0

    if not args.yes:
        answer = input(f"About to deploy to {PUBLIC_URL} ({SSH_HOST}). Continue? [y/N] ").strip().lower()
        if answer != "y":
            log("Aborted by user.")
            return 1

    ssh_password = _get_secret(
        "WHATSAPPBRIDGE_DEPLOY_SSH_PASSWORD",
        "SSH password for administrator@85.215.217.154 (vault: 'Production Server (IIS)')",
    )
    probe_email = os.environ.get("WHATSAPPBRIDGE_PROBE_EMAIL", "info@martiendejong.nl")
    probe_password = _get_secret(
        "WHATSAPPBRIDGE_PROBE_PASSWORD",
        "Probe login password (vault: 'WhatsApp Bridge frontend login (martiendejong)')",
    )

    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    backup_dir = f"{REMOTE_BACKUP_ROOT}\\{timestamp}"

    ssh = connect_ssh(ssh_password)
    try:
        sftp = ssh.open_sftp()
        try:
            log("[pre-deploy] checking which WhatsApp sessions are currently connected...")
            pre_connected, _ = get_connected_session_ids(API_URL, probe_email, probe_password)
            log(f"[pre-deploy] {len(pre_connected)} session(s) currently connected")

            upload_helper(sftp)

            log("[1/5] Backing up current live state...")
            run_helper(ssh, "Backup", BackupDir=backup_dir,
                       BackendLiveDir=REMOTE_BACKEND_DIR, FrontendLiveDir=REMOTE_FRONTEND_DIR)

            backend_temp = f"{REMOTE_BACKEND_DIR}__upload_{timestamp}"
            frontend_assets_temp = f"{REMOTE_FRONTEND_DIR}__upload_assets_{timestamp}"
            frontend_index_temp_dir = f"{REMOTE_FRONTEND_DIR}__upload_index_{timestamp}"
            frontend_index_temp = f"{frontend_index_temp_dir}\\index.html"

            # Once anything has actually been swapped into the live directories, any later
            # failure (including a frontend upload DeployAborted, not just a SmokeCheckFailed)
            # must trigger a rollback — otherwise an unverified live swap is left standing.
            # Before any swap has happened, a DeployAborted is still safe to just propagate:
            # nothing live has changed yet.
            live_state_changed = False
            try:
                if backend_publish_dir:
                    log("[2/5] Uploading backend (full publish set) to a temp folder...")
                    sftp_upload_tree(sftp, backend_publish_dir, backend_temp, exclude_names=BACKEND_CONFIG_EXCLUDE)
                    log("[2/5] Swapping backend into place (full-set replace, never a single-DLL swap)...")
                    run_helper(ssh, "SwapBackend", BackendLiveDir=REMOTE_BACKEND_DIR, BackendTempDir=backend_temp)
                    live_state_changed = True
                else:
                    log("[2/5] --skip-backend set, skipping backend deploy.")

                if frontend_dist_dir:
                    log("[3/5] Uploading frontend assets (everything except index.html) to a temp folder...")
                    sftp_upload_tree(sftp, frontend_dist_dir, frontend_assets_temp, exclude_names={"index.html"})

                    index_local = frontend_dist_dir / "index.html"
                    if not index_local.exists():
                        raise DeployAborted("Frontend build has no index.html")
                    _ensure_remote_dir(sftp, frontend_index_temp_dir)
                    sftp.put(str(index_local), frontend_index_temp)
                    if sftp.stat(frontend_index_temp).st_size != index_local.stat().st_size:
                        raise DeployAborted("index.html upload size mismatch — refusing to swap it into place")

                    log("[3/5] Swapping frontend into place (assets first, index.html last)...")
                    run_helper(ssh, "SwapFrontend", FrontendLiveDir=REMOTE_FRONTEND_DIR,
                               FrontendAssetsTempDir=frontend_assets_temp, FrontendIndexTempFile=frontend_index_temp)
                    live_state_changed = True
                else:
                    log("[3/5] --skip-frontend set, skipping frontend deploy.")

                log(f"[4/5] Running post-deploy smoke checks against {API_URL} (API) and {FRONTEND_URL} (frontend)...")
                smoke_check_version(API_URL)
                if frontend_dist_dir:
                    smoke_check_bundle_asset(FRONTEND_URL, frontend_dist_dir, required_url)
                post_connected, _ = get_connected_session_ids(API_URL, probe_email, probe_password)
                lost = pre_connected - post_connected
                if lost:
                    raise SmokeCheckFailed(
                        f"{len(lost)} previously-connected WhatsApp session(s) are no longer "
                        f"connected after deploy: {sorted(lost)}"
                    )
                log(f"[4/5] Session continuity OK ({len(post_connected)} connected)")
            except (DeployAborted, SmokeCheckFailed) as e:
                if not live_state_changed:
                    # Nothing live has changed yet (failure was in a pre-swap upload/gate
                    # step) — safe to just propagate, no rollback needed.
                    raise
                log(f"[5/5] DEPLOY STEP FAILED after a live swap: {e}")
                log(f"[5/5] Rolling back to pre-deploy backup at {backup_dir} ...")
                run_helper(ssh, "Rollback", BackupDir=backup_dir,
                           BackendLiveDir=REMOTE_BACKEND_DIR, FrontendLiveDir=REMOTE_FRONTEND_DIR)
                time.sleep(5)
                smoke_check_version(API_URL)
                log(f"[5/5] Rollback complete. Live site restored from {backup_dir}.")
                # Re-surface as SmokeCheckFailed regardless of the original exception type: the
                # top-level handler's "(rolled back)" message is only accurate for this class,
                # and a rollback has now genuinely happened here either way.
                raise SmokeCheckFailed(str(e)) from e

            log("[5/5] All smoke checks passed.")
            run_helper(ssh, "Prune", BackupRoot=REMOTE_BACKUP_ROOT, KeepBackups=str(KEEP_BACKUPS))
            log(f"Deployment succeeded. Pre-deploy backup retained at {backup_dir}.")
            return 0
        finally:
            sftp.close()
    finally:
        ssh.close()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except DeployAborted as e:
        log(f"REFUSED: {e}")
        sys.exit(1)
    except SmokeCheckFailed as e:
        log(f"FAILED (rolled back): {e}")
        sys.exit(1)
