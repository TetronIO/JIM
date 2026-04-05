# Plan: Slick Quick Start for Admin Deployment

**Status:** Done; implemented in `537d29ac`

## Context

The README currently tells admins to `git clone` the repo to deploy JIM. This is suboptimal:
- Many server environments don't have git installed
- Admins don't need the full source code; just compose files and config
- Research of 9 comparable projects (Gitea, Plausible, Immich, Coolify, n8n, OpenMetadata, Outline, Traefik, Portainer) shows **none** ship `build:` stanzas in admin-facing compose files, and **none** generate compose files dynamically

**Goal:** Ship a pre-authored production compose override, an interactive setup script, and manual curl commands; following industry conventions.

---

## Deliverables

### 1. Create `docker-compose.production.yml` (pre-authored override)

**Location:** `docker-compose.production.yml` (repo root)

This file nullifies the `build:` stanzas and sets production restart policy. It uses the existing `${DOCKER_REGISTRY}` and `${JIM_VERSION}` variable interpolation already in `docker-compose.yml`, so no hardcoded image tags are needed.

```yaml
# Production override for JIM
# Removes build contexts (uses pre-built images from GHCR) and sets production restart policy.
#
# Usage:
#   docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d
#
# Requires DOCKER_REGISTRY and JIM_VERSION in .env, e.g.:
#   DOCKER_REGISTRY=ghcr.io/tetronio/
#   JIM_VERSION=0.3.0

services:
  jim.web:
    build: !reset null
    restart: unless-stopped

  jim.worker:
    build: !reset null
    restart: unless-stopped

  jim.scheduler:
    build: !reset null
    restart: unless-stopped

  jim.database:
    restart: unless-stopped
```

This is essentially identical to what `Build-ReleaseBundle.ps1` already generates, but:
- Pre-authored in the repo (not generated)
- Uses env var interpolation instead of hardcoded version tags
- Attached to releases as a standalone asset

### 2. Create `setup.sh`: Interactive installer script

**Location:** `setup.sh` (repo root)

**Flow:**
1. **Banner**: JIM ASCII art + description:
   ```
        ██╗██╗███╗   ███╗
        ██║██║████╗ ████║
        ██║██║██╔████╔██║
   ██   ██║██║██║╚██╔╝██║
   ╚█████╔╝██║██║ ╚═╝ ██║
    ╚════╝ ╚═╝╚═╝     ╚═╝
   Junctional Identity Manager
   ```
2. **Prerequisites check**: Docker, Docker Compose v2, curl
3. **Auto-detect latest release**: Query `https://api.github.com/repos/TetronIO/JIM/releases/latest`
4. **Create install directory**: `./jim/` (configurable via `JIM_INSTALL_DIR`)
5. **Download pre-authored files** from release assets:
   - `docker-compose.yml`
   - `docker-compose.production.yml`
   - `.env.example` → saved as `.env`
6. **Interactive configuration** (skipped if env vars pre-set):
   - **Database topology**: Bundled PostgreSQL or external?
     - Bundled: auto-generate secure DB password via `openssl rand -base64 24`
     - External: prompt for hostname, DB name, username, password
   - **SSO/OIDC**: Walk through required variables with descriptions + examples
   - Update `.env` in place
   - Set `DOCKER_REGISTRY=ghcr.io/tetronio/` and `JIM_VERSION=<detected>`
7. **Launch**: Ask whether to start JIM, run appropriate `docker compose` command
8. **Summary**: Access URL, management commands, link to SSO guide

**Non-interactive mode:** If all required env vars are pre-set (`JIM_SSO_AUTHORITY`, `JIM_SSO_CLIENT_ID`, etc.) + `JIM_SETUP_DB_MODE=bundled|external` + `JIM_SETUP_AUTO_START=true`, the script runs without prompts.

**Design:** bash 3.2+ compatible, Linux + macOS, colour with fallback, idempotent (detects existing install). Downloads only pre-authored files; no dynamic generation.

### 3. Update release workflow to attach standalone assets

**File:** `.github/workflows/release.yml`: `create-release` job

Add `docker-compose.yml`, `docker-compose.production.yml`, and `.env.example` as individual release assets alongside the existing bundle tarball and checksums. This enables clean download URLs:
```
https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.yml
```

Change to the `gh release create` command:
```bash
gh release create "v${VERSION}" \
  --title "JIM v${VERSION}" \
  --notes-file ./release-notes.md \
  ./artifacts/jim-release-${VERSION}.tar.gz \
  ./artifacts/checksums.sha256 \
  ./docker-compose.yml \
  ./docker-compose.production.yml \
  ./.env.example
```

### 4. Update README.md Quick Start

Replace the "For Admins (Deploy)" section with three options:

**Option 1; Automated setup (recommended):**
```bash
curl -fsSL https://raw.githubusercontent.com/TetronIO/JIM/main/setup.sh | bash
```
Plus the "inspect first" variant.

**Option 2; Manual setup:**
```bash
VERSION=$(curl -s https://api.github.com/repos/TetronIO/JIM/releases/latest | grep -o '"tag_name": "v[^"]*"' | cut -d'"' -f4 | sed 's/^v//')
mkdir jim && cd jim
curl -fsSL -o docker-compose.yml "https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.yml"
curl -fsSL -o docker-compose.production.yml "https://github.com/TetronIO/JIM/releases/latest/download/docker-compose.production.yml"
curl -fsSL -o .env "https://github.com/TetronIO/JIM/releases/latest/download/.env.example"
# Edit .env with SSO settings and set DOCKER_REGISTRY + JIM_VERSION
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d
```

**Option 3; Air-gapped deployment:**
Download release bundle from GitHub Releases, follow included `INSTALL.md`.

### 5. Update CHANGELOG.md

Add entry under `## [Unreleased]` / **Added**.

---

## Files to Create/Modify

| File | Action | Status |
|------|--------|--------|
| `docker-compose.production.yml` | **Create**: Pre-authored production override | Done |
| `setup.sh` | **Create**: Interactive installer (~426 lines) | Done |
| `.github/workflows/release.yml` | **Edit**: Attach standalone assets to release | Done |
| `README.md` | **Edit**: Replace admin Quick Start section | Done |
| `CHANGELOG.md` | **Edit**: Add Unreleased entry | Done |
| `docs/RELEASE_PROCESS.md` | **Edit**: Update verify step, fix stale env var names | Done |

## Follow-up Items

- [x] **PowerShell module banner:** Show the JIM ASCII art banner in the PowerShell module after successful authentication (e.g., after `Connect-JIM`). Gives a consistent branded feel across the setup script and PS module.
- [x] **Deployment guide:** Create a comprehensive deployment guide (`docs/DEPLOYMENT_GUIDE.md`) covering both online (connected) and offline (air-gapped) scenarios. Should include prerequisites, network requirements, topology options (bundled vs external DB), TLS/reverse proxy configuration, upgrades, and backup/restore. The README Quick Start gives the 5-minute path; the deployment guide gives the full production-ready walkthrough.

## Verification

- [x] `bash -n setup.sh`: syntax check
- [x] `shellcheck setup.sh`: lint (if available)
- [x] Review `docker-compose.production.yml` with `docker compose config` to verify merge
- [x] Verify README markdown renders correctly
- [x] No .NET code changes → no build/test required
- [x] Interactive testing; both bundled and external DB paths, special characters in passwords
