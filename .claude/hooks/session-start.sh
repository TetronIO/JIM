#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

# --- 1. .NET SDK -----------------------------------------------------------
# Pinned to the same SDK as .devcontainer/Dockerfile, and for the same reason:
# src/JIM.Web/JIM.Web.csproj pins RuntimeFrameworkVersion to the runtime its
# production base image ships, and an SDK older than that cannot run what it
# builds. JIM.Web then fails at launch with "You must install or update .NET
# to run this application", which takes out every runtime-verification route
# the sandbox exists to provide (Start-SandboxStack.ps1, Generate-OpenApiDoc).
# Keep in step with .devcontainer/Dockerfile; engineering/DEPENDENCY_PINNING.md
# lists the pins that move together.
DOTNET_SDK_VERSION="10.0.400"

# Liveness is probed by asking what is installed, not by looking for the file.
# A sandbox container resumed from a previous session already has a dotnet on
# disk, so a presence test skips the install and silently keeps whatever SDK
# that session happened to fetch, which is how a bumped pin never arrives.
# (Same shape as the containerd socket-file check fixed in #1334.)
has_pinned_sdk() {
  local candidate="$1"
  command -v "$candidate" >/dev/null 2>&1 || return 1
  "$candidate" --list-sdks 2>/dev/null | awk '{print $1}' | grep -qxF "$DOTNET_SDK_VERSION"
}

if ! has_pinned_sdk dotnet && ! has_pinned_sdk "$HOME/.dotnet/dotnet"; then
  log "Installing .NET SDK ${DOTNET_SDK_VERSION}..."
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version "$DOTNET_SDK_VERSION" --install-dir "$HOME/.dotnet" >/dev/null
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
#
# containerd is started first, and dockerd is pointed at it. Left to supervise
# its own containerd, dockerd gives up with "failed to start containerd:
# timeout waiting for containerd to start" and shuts itself down; starting
# containerd separately and waiting for it to answer makes this reliable.
#
# Liveness is probed with `ctr version`, not with the presence of the socket
# file: the socket survives in the filesystem when a session's container is
# resumed, so a file-existence test skips the start and leaves dockerd dialling
# a dead socket ("connection refused"), which reads as Docker being unavailable
# for the whole session when it only needed containerd starting.
if ! docker info >/dev/null 2>&1; then
  log "Starting Docker daemon..."

  if ! ctr --address /run/containerd/containerd.sock version >/dev/null 2>&1; then
    rm -f /run/containerd/containerd.sock
    nohup containerd > /tmp/containerd.log 2>&1 &
    for _ in $(seq 1 30); do
      ctr --address /run/containerd/containerd.sock version >/dev/null 2>&1 && break
      sleep 1
    done
  fi

  nohup dockerd --containerd=/run/containerd/containerd.sock > /tmp/dockerd.log 2>&1 &
  for _ in $(seq 1 60); do
    docker info >/dev/null 2>&1 && break
    sleep 1
  done
  if docker info >/dev/null 2>&1; then
    log "Docker daemon is up."
  else
    log "WARNING: Docker daemon failed to start; see /tmp/dockerd.log and /tmp/containerd.log. Container-based verification will be unavailable."
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
