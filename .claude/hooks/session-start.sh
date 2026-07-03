#!/bin/bash
# SessionStart hook for Claude Code on the web (cloud sandbox) sessions.
#
# Prepares the sandbox so agents can build, test and runtime-verify JIM:
#   1. .NET 10 SDK (native builds and tests)
#   2. PowerShell (module tests and cmdlet verification)
#   3. Docker daemon (database + Keycloak containers)
#   4. .env from .env.example (dev credentials for the local stack)
#   5. Pre-pulled database/Keycloak images and restored NuGet packages
#      (both land in the cached container state, so later sessions are fast)
#
# See engineering/SANDBOX_RUNTIME_VERIFICATION.md for how agents use this.
#
# Bash rather than PowerShell: like .devcontainer/setup.sh, this runs during
# environment bootstrap, before PowerShell is guaranteed to exist.
set -euo pipefail

# Local development environments (devcontainer) are already provisioned.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

log() { echo "[session-start] $*"; }

# --- 1. .NET 10 SDK -------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1 && [ ! -x "$HOME/.dotnet/dotnet" ]; then
  log "Installing .NET 10 SDK..."
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet" >/dev/null
fi
if [ -n "${CLAUDE_ENV_FILE:-}" ] && [ -x "$HOME/.dotnet/dotnet" ]; then
  {
    echo "export DOTNET_ROOT=\"$HOME/.dotnet\""
    echo "export PATH=\"$HOME/.dotnet:\$PATH\""
  } >> "$CLAUDE_ENV_FILE"
fi

# --- 2. PowerShell ---------------------------------------------------------
if ! command -v pwsh >/dev/null 2>&1; then
  log "Installing PowerShell..."
  # shellcheck disable=SC1091
  . /etc/os-release
  curl -sSL "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb
  dpkg -i /tmp/packages-microsoft-prod.deb >/dev/null
  apt-get update -qq
  apt-get install -y -qq powershell >/dev/null
  rm -f /tmp/packages-microsoft-prod.deb
fi

# --- 3. Docker daemon ------------------------------------------------------
# Image layers persist in /var/lib/docker (cached), but the daemon itself
# must be started in every session.
if ! docker info >/dev/null 2>&1; then
  log "Starting Docker daemon..."
  nohup dockerd > /tmp/dockerd.log 2>&1 &
  for _ in $(seq 1 30); do
    docker info >/dev/null 2>&1 && break
    sleep 1
  done
  if docker info >/dev/null 2>&1; then
    log "Docker daemon is up."
  else
    log "WARNING: Docker daemon failed to start; see /tmp/dockerd.log. Container-based verification will be unavailable."
  fi
fi

# --- 4. .env for the local stack ------------------------------------------
cd "${CLAUDE_PROJECT_DIR:-$(pwd)}"
if [ ! -f .env ] && [ -f .env.example ]; then
  log "Creating .env from .env.example (dev credentials)..."
  sed 's/your_secure_password_here/password/' .env.example > .env
fi

# --- 5. Warm caches (images + NuGet) ---------------------------------------
if docker info >/dev/null 2>&1; then
  log "Pulling database and Keycloak images (no-op when cached)..."
  docker compose -f docker-compose.yml -f docker-compose.override.yml --profile with-db pull --quiet jim.database jim.keycloak || \
    log "WARNING: image pull failed; stack start will pull on demand."
fi
if [ -x "$HOME/.dotnet/dotnet" ] && [ -f JIM.sln ]; then
  log "Restoring NuGet packages (no-op when cached)..."
  "$HOME/.dotnet/dotnet" restore JIM.sln --verbosity quiet || \
    log "WARNING: dotnet restore failed; builds will restore on demand."
fi

log "Sandbox ready."
