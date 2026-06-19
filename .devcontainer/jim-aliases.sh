#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
# JIM Development Aliases
# This file is sourced automatically by .zshrc

# Unset GITHUB_TOKEN to allow gh CLI to use its own authentication with project scopes
unset GITHUB_TOKEN

# Help - list all jim aliases
alias jim='echo "JIM Development Aliases:

.NET Local Development:
  jim-compile        - dotnet build JIM.sln
  jim-test           - Run unit + workflow tests (excludes Explicit)
  jim-test-all       - Run ALL tests (incl. Explicit + Pester)
  jim-test-ps        - Run PowerShell Pester tests
  jim-clean          - dotnet clean && build
  jim-msbuild-purge  - Kill cached MSBuild worker nodes (reclaims RAM)
  jim-web            - Run JIM.Web locally (sources .env)
  jim-worker         - Run JIM.Worker locally (sources .env)
  jim-scheduler      - Run JIM.Scheduler locally (sources .env)

Database Management:
  jim-migrate        - dotnet ef database update
  jim-migration      - dotnet ef migrations add
  jim-db             - Start PostgreSQL
  jim-db-stop        - Stop PostgreSQL
  jim-db-logs        - View database logs
  jim-postgres-tune  - Auto-tune PostgreSQL for current devcontainer specs

Identity Provider (Keycloak):
  jim-keycloak       - Start Keycloak IdP (for local F5 debugging)
  jim-keycloak-stop  - Stop Keycloak IdP
  jim-keycloak-logs  - View Keycloak logs

Docker Stack Management (auto-kills local JIM processes):
  jim-stack          - Start Docker stack
  jim-stack-logs     - View Docker stack logs
  jim-stack-down     - Stop Docker stack
  jim-restart        - Recreate stack (re-reads .env, no rebuild)

Docker Builds (auto-kills local JIM processes, rebuild + start):
  jim-build          - Rebuild all services + start
  jim-build-light    - Start db + Keycloak, run JIM.Web natively
  jim-build-web      - Rebuild jim.web + start
  jim-build-worker   - Rebuild jim.worker + start
  jim-build-scheduler - Rebuild jim.scheduler + start
  Note: dev builds skip the OpenAPI doc generation Docker stage for speed.
        Run jim-openapi-generate to refresh src/JIM.Web/wwwroot/api/openapi/v1.json
        when API surface changes.

Reset:
  jim-reset          - Full reset (containers, images, volumes)
  jim-wipe           - Wipe JIM data (reset CSOs/MVOs/config, keep schema)
  jim-cleanup        - Free disk space (prune orphaned volumes and unused images)

Documentation:
  jim-docs           - Preview docs site at http://localhost:8000
  jim-docs-build     - Build static docs site to site/

Diagrams:
  jim-diagrams       - Export Structurizr C4 diagrams as SVG

OpenAPI:
  jim-openapi-generate - Generate static OpenAPI document (no DB/IdP required)

Planning:
  jim-prd            - Create a new PRD from template

Developer Setup:
  jim-unlock-signing  - Start in-container ssh-agent + load key (Zed: run once per container start)
  jim-setup-signing   - (Re)configure git commit signing for this environment
  jim-signing-status  - Show current commit signing state and readiness

Help:
  jim                - Show this help message
"'

# Developer setup
# Resolve the signing script path dynamically so the aliases work from any
# shell invocation location. Prefer the mounted workspace path; fall back to
# the home directory copy if someone has installed the aliases outside a
# devcontainer.
_jim_signing_script() {
  if [ -x "/workspaces/JIM/.devcontainer/configure-signing.sh" ]; then
    echo "/workspaces/JIM/.devcontainer/configure-signing.sh"
  elif [ -x "$HOME/.devcontainer/configure-signing.sh" ]; then
    echo "$HOME/.devcontainer/configure-signing.sh"
  else
    return 1
  fi
}
jim-setup-signing() {
  local script
  if ! script=$(_jim_signing_script); then
    echo "configure-signing.sh not found in expected locations" >&2
    return 1
  fi
  "$script"
}
jim-signing-status() {
  local script
  if ! script=$(_jim_signing_script); then
    echo "configure-signing.sh not found in expected locations" >&2
    return 1
  fi
  "$script" --status
}

# In-container ssh-agent for commit signing under launchers that do NOT forward
# the host SSH agent (Zed; see zed-industries/zed#47121). On those launchers the
# /ssh-agent bind from devcontainer.json is a dead placeholder (it only carries a
# real agent on macOS Docker Desktop), so we run an ssh-agent *inside* the
# container at a fixed socket and load the key once per container start with
# `jim-unlock-signing`. Every shell that sources this file then reuses that one
# agent. On VS Code (host agent forwarded) and Codespaces (gh-gpgsign), this is
# unnecessary; the auto-adopt below simply no-ops because the fixed socket is
# absent, leaving the forwarded SSH_AUTH_SOCK untouched.
JIM_SIGNING_AGENT_SOCK="$HOME/.ssh/agent.sock"

# Auto-adopt a previously-unlocked in-container agent so new shells inherit it
# (overriding the dead /ssh-agent placeholder from containerEnv). This ONLY
# adopts an already-running agent that has a key loaded; it never starts one, so
# sourcing stays cheap and no stray agents are spawned.
if [ -S "$JIM_SIGNING_AGENT_SOCK" ] && SSH_AUTH_SOCK="$JIM_SIGNING_AGENT_SOCK" ssh-add -l >/dev/null 2>&1; then
  export SSH_AUTH_SOCK="$JIM_SIGNING_AGENT_SOCK"
fi

jim-unlock-signing() {
  # (Re)start a persistent agent only if the fixed socket has no reachable agent.
  # ssh-add -l exit codes: 0 = reachable with keys, 1 = reachable but empty,
  # 2 = cannot connect. Only restart on "cannot connect" (or a missing socket);
  # a reachable-but-empty agent is reused and just gets the key added below.
  SSH_AUTH_SOCK="$JIM_SIGNING_AGENT_SOCK" ssh-add -l >/dev/null 2>&1
  local rc=$?
  if [ "$rc" -eq 2 ] || [ ! -S "$JIM_SIGNING_AGENT_SOCK" ]; then
    rm -f "$JIM_SIGNING_AGENT_SOCK"
    eval "$(ssh-agent -a "$JIM_SIGNING_AGENT_SOCK")" >/dev/null
  fi
  export SSH_AUTH_SOCK="$JIM_SIGNING_AGENT_SOCK"

  # Load a signing key if the agent doesn't already hold one. Prompts for the
  # passphrase once per container start; subsequent commits reuse the agent.
  # Key discovery is name-agnostic so it works for any developer's key, not a
  # hardcoded filename:
  #   1. `ssh-add` with no arguments loads OpenSSH's standard default identities.
  #   2. If none of those exist (a non-default key name), fall back to the first
  #      private key file found in ~/.ssh (-type f skips the agent socket).
  if ! ssh-add -l >/dev/null 2>&1; then
    ssh-add 2>/dev/null
    if ! ssh-add -l >/dev/null 2>&1; then
      local key
      key=$(find "$HOME/.ssh" -maxdepth 1 -type f ! -name '*.pub' -exec grep -lE 'BEGIN [A-Z ]*PRIVATE KEY' {} + 2>/dev/null | head -1)
      if [ -z "$key" ] || ! ssh-add "$key"; then
        echo "jim-unlock-signing: no loadable SSH private key found in ~/.ssh" >&2
        return 1
      fi
    fi
  fi

  # Point git at the now-available agent (idempotent; configures global signing).
  jim-setup-signing
}

# .NET local development
alias jim-compile='dotnet build JIM.sln'
alias jim-test='dotnet test JIM.sln'
# Kill cached MSBuild worker nodes that stack up across build/test iterations.
# `dotnet` spawns these with `/nodemode:1 /nodeReuse:true` and they hang around holding
# 150-220 MB each; `dotnet build-server shutdown` doesn't touch them. Safe to run at any
# time — does not affect running JIM.Web/Worker/Scheduler processes or the language server.
alias jim-msbuild-purge='count=$(pgrep -f "[M]SBuild.dll.*nodemode:1.*nodeReuse:true" 2>/dev/null | wc -l); if [ "$count" -gt 0 ]; then pkill -f "[M]SBuild.dll.*nodemode:1.*nodeReuse:true"; echo "Purged $count MSBuild worker node(s)."; else echo "No cached MSBuild worker nodes to purge."; fi'
jim-test-all() {
  local dotnet_log pester_log dotnet_rc pester_rc dotnet_summary pester_summary
  dotnet_log=$(mktemp)
  pester_log=$(mktemp)

  echo "=== Running .NET tests (including Explicit) ==="
  dotnet test JIM.sln --settings test/run-all.runsettings 2>&1 | tee "$dotnet_log"
  dotnet_rc=$?

  echo ""
  echo "=== Running Pester tests ==="
  pwsh -NoProfile -Command "Import-Module Pester; \$config = New-PesterConfiguration; \$config.Run.Path = './src/JIM.PowerShell/Tests'; \$config.Output.Verbosity = 'Detailed'; Invoke-Pester -Configuration \$config" 2>&1 | tee "$pester_log"
  pester_rc=$?

  dotnet_summary=$(grep -E "^(Passed!|Failed!)" "$dotnet_log")
  pester_summary=$(sed 's/\x1b\[[0-9;]*m//g' "$pester_log" | grep -E "^Tests completed|^Tests Passed")
  rm -f "$dotnet_log" "$pester_log"

  echo ""
  echo "========================================"
  echo "         TEST RESULTS SUMMARY"
  echo "========================================"
  echo ""
  echo ".NET Tests (dotnet test):"
  if [ -n "$dotnet_summary" ]; then
    echo "$dotnet_summary" | sed 's/^/  /'
  else
    echo "  No summary available (exit code: $dotnet_rc)"
  fi
  echo ""
  echo "Pester Tests:"
  if [ -n "$pester_summary" ]; then
    echo "$pester_summary" | sed 's/^/  /'
  else
    echo "  No summary available (exit code: $pester_rc)"
  fi
  echo ""
  if [ "$dotnet_rc" -eq 0 ] && [ "$pester_rc" -eq 0 ]; then
    echo "Overall: ALL TESTS PASSED"
  else
    echo "Overall: SOME TESTS FAILED"
    [ "$dotnet_rc" -ne 0 ] && echo "  - .NET tests failed (exit code: $dotnet_rc)"
    [ "$pester_rc" -ne 0 ] && echo "  - Pester tests failed (exit code: $pester_rc)"
  fi
  echo "========================================"

  [ "$dotnet_rc" -ne 0 ] || [ "$pester_rc" -ne 0 ] && return 1
  return 0
}
alias jim-test-ps='pwsh -NoProfile -Command "Import-Module Pester; \$config = New-PesterConfiguration; \$config.Run.Path = \"./src/JIM.PowerShell/Tests\"; \$config.Output.Verbosity = \"Detailed\"; Invoke-Pester -Configuration \$config"'
alias jim-clean='dotnet clean JIM.sln && dotnet build JIM.sln'

# Kill a specific locally-running JIM .NET project before restarting it
_jim_kill_project() {
  local project="$1"
  local pids
  pids=$(pgrep -f "dotnet.*JIM\.${project}" 2>/dev/null || true)
  if [ -n "$pids" ]; then
    echo "Stopping existing JIM.${project} (PIDs: $(echo $pids | tr '\n' ' '))..."
    echo "$pids" | xargs kill 2>/dev/null || true
    sleep 1
  fi
}

# Local run aliases - source .env and override DB hostname for local access
jim-web()       { _jim_kill_project Web       && (set -a && source .env && export JIM_DB_HOSTNAME=localhost && dotnet run --project src/JIM.Web); }
jim-worker()    { _jim_kill_project Worker    && (set -a && source .env && export JIM_DB_HOSTNAME=localhost && dotnet run --project src/JIM.Worker); }
jim-scheduler() { _jim_kill_project Scheduler && (set -a && source .env && export JIM_DB_HOSTNAME=localhost && dotnet run --project src/JIM.Scheduler); }

# Database management
alias jim-migrate='dotnet ef database update --project src/JIM.PostgresData'
alias jim-migration='dotnet ef migrations add --project src/JIM.PostgresData'

# ============================================================================
# Compose file helpers
# ============================================================================
# These build the -f chain dynamically, appending the auto-tuned local overlay
# when it exists (generated by .devcontainer/postgres-tune.sh).
#
# Layering order (later files win):
#   docker-compose.yml          — production/deployment defaults
#   docker-compose.override.yml — tracked dev overrides (ports, env, LANG)
#   docker-compose.local.yml    — gitignored, machine-specific DB tuning
#
# For standalone db (jim-db):
#   .devcontainer/db.yml        — tracked defaults
#   .devcontainer/db.local.yml  — gitignored, machine-specific DB tuning

_jim_compose() {
  local args=(-f docker-compose.yml -f docker-compose.override.yml)
  [ -f docker-compose.local.yml ] && args+=(-f docker-compose.local.yml)
  args+=(--profile with-db)
  echo "${args[@]}"
}

_jim_db_compose() {
  local args=(-f .devcontainer/db.yml)
  [ -f .devcontainer/db.local.yml ] && args+=(-f .devcontainer/db.local.yml)
  echo "${args[@]}"
}

# Standalone database (local development workflow)
jim-db() {
  _jim_heal_docker_creds
  docker compose $(_jim_db_compose) up -d
}
jim-db-stop() {
  docker compose $(_jim_db_compose) down
}
jim-db-logs() {
  docker compose $(_jim_db_compose) logs -f
}

# Standalone Keycloak IdP (local debugging workflow - use with jim-db + F5)
# Starts the bundled Keycloak from docker-compose.override.yml without the full stack.
jim-keycloak() {
  _jim_heal_docker_creds
  docker compose -f docker-compose.yml -f docker-compose.override.yml up -d jim.keycloak
  _jim_keycloak_bridge
}
jim-keycloak-stop() {
  pkill -f 'socat.*TCP:127.0.0.1:8180' 2>/dev/null || true
  docker compose -f docker-compose.yml -f docker-compose.override.yml stop jim.keycloak
  docker compose -f docker-compose.yml -f docker-compose.override.yml rm -f jim.keycloak
}
jim-keycloak-logs() {
  docker compose -f docker-compose.yml -f docker-compose.override.yml logs -f jim.keycloak
}

# Start a userspace port forwarder for Keycloak so VS Code can forward it.
# Docker-in-Docker proxy ports aren't forwarded by VS Code Dev Containers
# unless they were present at devcontainer build time. This socat bridge
# runs as the vscode user, which VS Code detects and forwards to the host.
_jim_keycloak_bridge() {
  pkill -f 'socat.*TCP:127.0.0.1:8180' 2>/dev/null || true
  if command -v socat &>/dev/null; then
    socat TCP-LISTEN:8181,fork,reuseaddr,bind=0.0.0.0 TCP:127.0.0.1:8180 &
  fi
}

# Wait for the Keycloak container to report healthy (used by jim-build-light).
# Polls docker health status rather than curling a port because Keycloak's
# health endpoint (port 9000) is not exposed to the host.
_jim_wait_keycloak() {
  echo "Waiting for Keycloak to be ready..."
  local attempts=0
  while [ $attempts -lt 60 ]; do
    local health_status
    health_status=$(docker inspect --format='{{.State.Health.Status}}' jim.keycloak 2>/dev/null || echo "not found")
    if [ "$health_status" = "healthy" ]; then
      echo "Keycloak is ready."
      return 0
    fi
    attempts=$((attempts + 1))
    sleep 2
  done
  echo "WARNING: Keycloak did not become healthy within 120 seconds. JIM.Web may fail to start."
  echo "Check logs with: jim-keycloak-logs"
  return 1
}

# Kill any locally-running JIM .NET processes (jim-web, jim-worker, jim-scheduler)
# so they don't hold ports that Docker containers need to bind
_jim_kill_local() {
  local pids
  pids=$(pgrep -f 'dotnet.*JIM\.(Web|Worker|Scheduler)' 2>/dev/null || true)
  if [ -n "$pids" ]; then
    echo "Stopping local JIM process(es) (PIDs: $(echo $pids | tr '\n' ' '))..."
    echo "$pids" | xargs kill 2>/dev/null || true
    sleep 1
  fi
}

# Self-heal stale Docker credential helper references at command time.
#
# The VS Code Dev Containers extension's per-session credsStore shim
# (docker-credential-dev-containers-<uuid>) is unreliable from BuildKit even
# when its helper binary is present: it proxies credential lookups to the host
# VS Code over a /tmp IPC, and when that peer is momentarily unreachable
# (extension reload, reconnect, a build firing before VS Code re-establishes
# it) the shim exits 255 and BuildKit aborts resolving even public images,
# failing with "error getting credentials - err: exit status 255". setup.sh
# and postStartCommand heal this at container create/start, but VS Code
# re-injects the credsStore on every reconnect, so we re-run the heal here,
# immediately before each docker build/up.
#
# Delegates to the canonical .devcontainer/heal-docker-creds.sh so heal logic
# lives in exactly one place. This was previously a separate inline copy that
# only stripped when the helper binary was *missing*; it drifted from the
# canonical script (which strips any dev-containers-* shim unconditionally),
# let the unreliable-but-present shim through, and broke jim-build. Do not
# reintroduce inline logic here - fix heal-docker-creds.sh instead.
_jim_heal_docker_creds() {
  local script
  if [ -f "$HOME/.devcontainer/heal-docker-creds.sh" ]; then
    script="$HOME/.devcontainer/heal-docker-creds.sh"
  elif [ -f "/workspaces/JIM/.devcontainer/heal-docker-creds.sh" ]; then
    script="/workspaces/JIM/.devcontainer/heal-docker-creds.sh"
  else
    return 0
  fi
  bash "$script"
}

# Clear any previous aliases before defining functions (zsh cannot redefine alias as function)
unalias jim-stack jim-stack-logs jim-stack-down jim-restart jim-build jim-build-light jim-build-web jim-build-worker jim-build-scheduler jim-cleanup jim-reset jim-db jim-db-stop jim-db-logs jim-keycloak jim-keycloak-stop jim-keycloak-logs 2>/dev/null || true

# Docker stack management
jim-stack() {
  _jim_heal_docker_creds
  _jim_kill_local
  docker compose $(_jim_compose) up -d
  _jim_keycloak_bridge
}
jim-stack-logs() {
  local lines="${1:-100}"
  docker compose $(_jim_compose) logs -f --tail="$lines"
}
jim-stack-down() {
  docker compose $(_jim_compose) down
  docker compose -f test/integration/docker/docker-compose.integration-tests.yml --profile scenario2 --profile scenario8 down --remove-orphans 2>/dev/null || true
  docker rm -f samba-ad-primary samba-ad-source samba-ad-target 2>/dev/null || true
}
jim-restart() {
  _jim_heal_docker_creds
  _jim_kill_local
  docker compose $(_jim_compose) down && docker compose $(_jim_compose) up -d --force-recreate
  _jim_keycloak_bridge
}

# Docker builds (rebuild and start services)
# VERSION_SUFFIX is generated at build time so each Docker build gets a unique dev version.
# OPENAPI_STAGE=publish skips the expensive openapi-gen Dockerfile stage in
# local dev builds (saves several minutes per build). Run jim-openapi-generate
# to refresh the static OpenAPI doc on disk when the API surface changes.
_jim_version_suffix() {
  echo "dev.$(date -u +%Y%m%d).$((10#$(date -u +%H)*60+10#$(date -u +%M)))"
}
jim-build() {
  _jim_heal_docker_creds
  _jim_kill_local
  VERSION_SUFFIX="$(_jim_version_suffix)" OPENAPI_STAGE=publish docker compose $(_jim_compose) up -d --build
  _jim_keycloak_bridge
}
jim-build-web() {
  _jim_heal_docker_creds
  _jim_kill_local
  local vs="$(_jim_version_suffix)"
  VERSION_SUFFIX="$vs" OPENAPI_STAGE=publish docker compose $(_jim_compose) build jim.web && VERSION_SUFFIX="$vs" OPENAPI_STAGE=publish docker compose $(_jim_compose) up -d jim.web
}
jim-build-worker() {
  _jim_heal_docker_creds
  _jim_kill_local
  local vs="$(_jim_version_suffix)"
  VERSION_SUFFIX="$vs" OPENAPI_STAGE=publish docker compose $(_jim_compose) build jim.worker && VERSION_SUFFIX="$vs" OPENAPI_STAGE=publish docker compose $(_jim_compose) up -d jim.worker
}
jim-build-scheduler() {
  _jim_heal_docker_creds
  _jim_kill_local
  local vs="$(_jim_version_suffix)"
  VERSION_SUFFIX="$vs" OPENAPI_STAGE=publish docker compose $(_jim_compose) build jim.scheduler && VERSION_SUFFIX="$vs" OPENAPI_STAGE=publish docker compose $(_jim_compose) up -d jim.scheduler
}
jim-build-light() {
  _jim_heal_docker_creds
  _jim_kill_local
  docker compose $(_jim_compose) up -d jim.database jim.keycloak
  _jim_keycloak_bridge
  _jim_wait_keycloak
  jim-web
}

# Cleanup orphaned Docker resources to free disk space
jim-cleanup() {
  echo "Disk usage before cleanup:"
  df -h / | tail -1
  echo ""
  echo "Docker usage before cleanup:"
  docker system df
  echo ""
  echo "Removing orphaned volumes..."
  docker volume prune -f
  echo ""
  echo "Removing unused images..."
  docker image prune -a -f
  echo ""
  echo "Disk usage after cleanup:"
  df -h / | tail -1
}

# Prune unused images while preserving Samba AD and OpenLDAP snapshot/build images.
# NOTE: docker image prune --filter "label!=X" with multiple filters is broken —
# it deletes labelled images despite the exclusion. Work around this by collecting
# the IDs of images to preserve, pruning everything, then checking nothing was lost.
_jim_prune_images_preserving_snapshots() {
  local preserve_ids
  preserve_ids=$(docker images --filter "label=jim.samba.snapshot-hash" --filter "dangling=false" -q 2>/dev/null; \
                 docker images --filter "label=jim.samba.build-hash" --filter "dangling=false" -q 2>/dev/null; \
                 docker images --filter "label=jim.openldap.snapshot-hash" --filter "dangling=false" -q 2>/dev/null; \
                 docker images --filter "label=jim.openldap.build-hash" --filter "dangling=false" -q 2>/dev/null)
  preserve_ids=$(echo "$preserve_ids" | sort -u | grep -v '^$')

  if [ -z "$preserve_ids" ]; then
    docker image prune -af 2>/dev/null || true
    return
  fi

  # Get all unused image IDs, subtract the ones to preserve, and remove the rest
  local all_ids remove_ids
  all_ids=$(docker images -a -q 2>/dev/null | sort -u)
  remove_ids=$(comm -23 <(echo "$all_ids") <(echo "$preserve_ids"))

  if [ -n "$remove_ids" ]; then
    echo "$remove_ids" | xargs -r docker rmi -f 2>/dev/null || true
  fi

  # Clean up any remaining dangling images (no label to preserve)
  docker image prune -f 2>/dev/null || true
}

# Reset (preserves Samba AD and OpenLDAP snapshot images; they take a long time to build)
jim-reset() {
  # Stop any natively-run JIM.Web/Worker/Scheduler processes so they don't squat on host ports (e.g. 5200)
  local native_pids
  native_pids=$(pgrep -f '/JIM\.(Web|Worker|Scheduler)$' 2>/dev/null || true)
  if [ -n "$native_pids" ]; then
    echo "Stopping native JIM processes: $(echo $native_pids | tr '\n' ' ')"
    echo "$native_pids" | xargs -r kill 2>/dev/null || true
    sleep 1
    # Force-kill any survivors
    native_pids=$(pgrep -f '/JIM\.(Web|Worker|Scheduler)$' 2>/dev/null || true)
    [ -n "$native_pids" ] && echo "$native_pids" | xargs -r kill -9 2>/dev/null || true
  fi

  docker compose $(_jim_compose) down --volumes
  docker compose -f test/integration/docker/docker-compose.integration-tests.yml --profile scenario2 --profile scenario8 down --volumes --remove-orphans 2>/dev/null || true
  docker rm -f samba-ad-primary samba-ad-source samba-ad-target sqlserver-hris-a oracle-hris-b postgres-target openldap-test mysql-test 2>/dev/null || true
  _jim_prune_images_preserving_snapshots
  docker volume ls --format "{{.Name}}" | grep jim-integration | xargs -r docker volume rm 2>/dev/null || true
  docker volume rm -f jim-db-volume jim-logs-volume 2>/dev/null || true
  echo "JIM reset complete. Containers, images, and volumes removed (Samba AD & OpenLDAP snapshots preserved). Run jim-build to rebuild."
}

# Documentation preview (MkDocs Material)
alias jim-docs='mkdocs serve --dev-addr 0.0.0.0:8000'
alias jim-docs-build='mkdocs build'

# Structurizr diagram export
jim-diagrams() {
  local repo_root structurizr_dir container_name port
  repo_root="$(git rev-parse --show-toplevel 2>/dev/null || echo '/workspaces/JIM')"
  structurizr_dir="${repo_root}/engineering/diagrams/structurizr"
  container_name="jim-structurizr-export"
  port=8085

  # Resolve a launchable Chromium for the SVG export. Chrome for Testing (what
  # Puppeteer downloads) has no Linux arm64 build, so on arm64 hosts the bundled
  # Chrome cannot launch ("rosetta error: failed to open elf ... ld-linux-x86-64.so.2").
  # Fall back to a native Chromium (Playwright's, or a system one) and hand it to
  # Puppeteer via PUPPETEER_EXECUTABLE_PATH. On x86_64 the bundled Chrome is used unchanged.
  if [ -z "${PUPPETEER_EXECUTABLE_PATH:-}" ]; then
    case "$(uname -m)" in
      aarch64 | arm64)
        local native_chrome candidate
        native_chrome="$(find "${HOME}/.cache/ms-playwright" -type f -path '*/chrome-linux/chrome' 2>/dev/null | head -n1)"
        if [ -z "${native_chrome}" ]; then
          for candidate in chromium chromium-browser google-chrome-stable google-chrome; do
            if command -v "${candidate}" > /dev/null 2>&1; then
              native_chrome="$(command -v "${candidate}")"
              break
            fi
          done
        fi
        if [ -z "${native_chrome}" ]; then
          echo "ERROR: No launchable Chromium found for diagram export."
          echo "       Chrome for Testing has no Linux arm64 build, so Puppeteer's bundled"
          echo "       Chrome cannot run on this host. Install one with"
          echo "       'npx playwright install chromium' (or apt), or set"
          echo "       PUPPETEER_EXECUTABLE_PATH to a working browser, then retry."
          return 1
        fi
        export PUPPETEER_EXECUTABLE_PATH="${native_chrome}"
        echo "arm64 host detected: using native Chromium for export: ${PUPPETEER_EXECUTABLE_PATH}"
        ;;
    esac
  fi

  # Verify Puppeteer/Chrome are available (installed by devcontainer setup)
  if [ ! -d "${structurizr_dir}/node_modules" ]; then
    echo "Installing Puppeteer dependencies..."
    (cd "${structurizr_dir}" && npm install --silent && npx puppeteer browsers install chrome 2>/dev/null)
  fi

  # Remove any stale container
  docker rm -f "${container_name}" 2>/dev/null

  # Start Structurizr Local (adrs mount resolves the symlink inside the container)
  # Note: structurizr/lite is deprecated; using structurizr/structurizr with 'local' command
  echo "Starting Structurizr Local on port ${port}..."
  docker run -d --name "${container_name}" \
    -p "${port}:8080" \
    -v "${structurizr_dir}:/usr/local/structurizr" \
    -v "${repo_root}/engineering/adrs:/usr/local/structurizr/adrs" \
    structurizr/structurizr local > /dev/null

  # Wait for Structurizr Local to be ready
  echo "Waiting for Structurizr Local to start..."
  local attempts=0
  while [ $attempts -lt 30 ]; do
    if curl -sf "http://localhost:${port}/workspace/1/diagrams" > /dev/null 2>&1; then
      break
    fi
    attempts=$((attempts + 1))
    sleep 2
  done

  if [ $attempts -ge 30 ]; then
    echo "ERROR: Structurizr Local failed to start within 60 seconds."
    docker rm -f "${container_name}" > /dev/null 2>&1
    return 1
  fi

  echo "Structurizr Local is ready."

  # Remove old images (light, dark, and legacy root-level)
  rm -f "${repo_root}/docs/diagrams/images"/jim-structurizr-1-*.svg
  rm -f "${repo_root}/docs/diagrams/images/light"/jim-structurizr-1-*.svg
  rm -f "${repo_root}/docs/diagrams/images/dark"/jim-structurizr-1-*.svg

  # Export diagrams (light + dark)
  node "${structurizr_dir}/export-diagrams.js" \
    "http://localhost:${port}/workspace/1/diagrams" \
    "${repo_root}/docs/diagrams/images"
  local export_rc=$?

  # Cleanup
  echo "Stopping Structurizr Local..."
  docker rm -f "${container_name}" > /dev/null 2>&1

  if [ $export_rc -eq 0 ]; then
    echo ""
    echo "Diagrams exported:"
    echo "  Light mode (docs/diagrams/images/light/):"
    ls -1 "${repo_root}/docs/diagrams/images/light"/jim-structurizr-1-*.svg 2>/dev/null | sed 's|.*/||' | sed 's/^/    /'
    echo "  Dark mode (docs/diagrams/images/dark/):"
    ls -1 "${repo_root}/docs/diagrams/images/dark"/jim-structurizr-1-*.svg 2>/dev/null | sed 's|.*/||' | sed 's/^/    /'
  else
    echo "ERROR: Diagram export failed."
    return 1
  fi
}

# Create a new PRD from template
jim-prd() {
  local repo_root prd_dir template raw_name converted_name target_file
  repo_root="$(git rev-parse --show-toplevel 2>/dev/null || echo '/workspaces/JIM')"
  prd_dir="${repo_root}/engineering/prd"
  template="${prd_dir}/PRD_TEMPLATE.md"

  if [ ! -f "${template}" ]; then
    echo "ERROR: PRD template not found at ${template}"
    return 1
  fi

  # Prompt for feature name
  printf "Feature/task name: "
  read -r raw_name

  if [ -z "${raw_name}" ]; then
    echo "ERROR: No name provided."
    return 1
  fi

  # Convert to UPPER_SNAKE_CASE:
  # 1. Trim leading/trailing whitespace
  # 2. Replace non-alphanumeric characters (hyphens, spaces, etc.) with underscores
  # 3. Collapse multiple underscores into one
  # 4. Strip leading/trailing underscores
  # 5. Convert to uppercase
  converted_name=$(echo "${raw_name}" \
    | sed 's/^[[:space:]]*//;s/[[:space:]]*$//' \
    | sed 's/[^a-zA-Z0-9]/_/g' \
    | sed 's/__*/_/g' \
    | sed 's/^_//;s/_$//' \
    | tr '[:lower:]' '[:upper:]')

  if [ -z "${converted_name}" ]; then
    echo "ERROR: Name converted to empty string."
    return 1
  fi

  target_file="${prd_dir}/PRD_${converted_name}.md"

  if [ -f "${target_file}" ]; then
    echo "ERROR: ${target_file} already exists."
    return 1
  fi

  # Copy template, replace the title placeholder and set today's date
  sed -e "s/^# \[Feature Name\]/# ${converted_name//_/ }/" \
      -e "s/YYYY-MM-DD/$(date -u +%Y-%m-%d)/" "${template}" > "${target_file}"

  echo "Created: engineering/prd/PRD_${converted_name}.md"
  echo ""
  echo "Next steps:"
  echo "  1. Fill in the required sections in the PRD"
  echo "  2. Create a GitHub issue linking to it"
  echo "  3. Ask Claude to generate an implementation plan from the PRD"
}

# Auto-tune PostgreSQL for devcontainer specs
jim-postgres-tune() {
  local repo_root script_path
  repo_root="$(git rev-parse --show-toplevel 2>/dev/null || echo '/workspaces/JIM')"
  script_path="${repo_root}/.devcontainer/postgres-tune.sh"

  if [ ! -f "${script_path}" ]; then
    echo "ERROR: postgres-tune.sh not found at ${script_path}"
    return 1
  fi

  "${script_path}" "$@"
}

# Generate static OpenAPI document (no DB or IdP required)
jim-openapi-generate() {
  local repo_root
  repo_root="$(git rev-parse --show-toplevel 2>/dev/null || echo '/workspaces/JIM')"
  pwsh -File "${repo_root}/scripts/Generate-OpenApiDoc.ps1" "$@"
}

# Wipe JIM data (reset to initial state without destroying database)
# Note: Preserves MetaverseObjects with Administrator role assignments
alias jim-wipe='echo "Wiping JIM data..." && docker compose -f .devcontainer/db.yml exec -T jim.database psql -U ${JIM_DATABASE_USERNAME:-jim} -d ${JIM_DATABASE_NAME:-jim} -c "BEGIN; CREATE TEMP TABLE admin_mvos AS SELECT \"StaticMembersId\" as \"Id\" FROM \"MetaverseObjectRole\" WHERE \"RolesId\" = (SELECT \"Id\" FROM \"Roles\" WHERE \"Name\" = '\''Administrator'\''); DELETE FROM \"MetaverseObjectChangeAttributeValues\" WHERE \"MetaverseObjectChangeAttributeId\" IN (SELECT moca.\"Id\" FROM \"MetaverseObjectChangeAttributes\" moca JOIN \"MetaverseObjectChanges\" moc ON moca.\"MetaverseObjectChangeId\" = moc.\"Id\" WHERE moc.\"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos)); DELETE FROM \"MetaverseObjectChangeAttributes\" WHERE \"MetaverseObjectChangeId\" IN (SELECT \"Id\" FROM \"MetaverseObjectChanges\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos)); DELETE FROM \"MetaverseObjectChanges\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos); DELETE FROM \"ConnectedSystemObjectChangeAttributeValues\"; DELETE FROM \"ConnectedSystemObjectChangeAttributes\"; DELETE FROM \"ConnectedSystemObjectChanges\"; DELETE FROM \"PendingExportAttributeValueChanges\"; DELETE FROM \"PendingExports\"; DELETE FROM \"DeferredReferences\"; DELETE FROM \"ActivityRunProfileExecutionItems\"; DELETE FROM \"Activities\"; DELETE FROM \"WorkerTasks\"; DELETE FROM \"MetaverseObjectAttributeValues\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos) AND \"ReferenceValueId\" IS NULL; DELETE FROM \"MetaverseObjectAttributeValues\" WHERE \"MetaverseObjectId\" NOT IN (SELECT \"Id\" FROM admin_mvos); DELETE FROM \"MetaverseObjects\" WHERE \"Id\" NOT IN (SELECT \"Id\" FROM admin_mvos); DELETE FROM \"ConnectedSystemObjectAttributeValues\"; DELETE FROM \"ConnectedSystemObjects\"; DELETE FROM \"SyncRuleMappingSourceParamValues\"; DELETE FROM \"SyncRuleMappingSources\"; DELETE FROM \"SyncRuleMappings\"; DELETE FROM \"SyncRuleScopingCriteria\"; DELETE FROM \"SyncRuleScopingCriteriaGroups\"; DELETE FROM \"ObjectMatchingRuleSourceParamValues\"; DELETE FROM \"ObjectMatchingRuleSources\"; DELETE FROM \"ObjectMatchingRules\"; DELETE FROM \"SyncRules\"; DELETE FROM \"ConnectedSystemRunProfiles\"; DELETE FROM \"ConnectedSystemSettingValues\"; DELETE FROM \"ConnectedSystemAttributes\"; DELETE FROM \"ConnectedSystemObjectTypes\"; DELETE FROM \"ConnectedSystemContainers\"; DELETE FROM \"ConnectedSystemPartitions\"; DELETE FROM \"ConnectedSystems\"; COMMIT;" > /dev/null 2>&1 && docker compose -f .devcontainer/db.yml exec -T jim.database psql -U ${JIM_DATABASE_USERNAME:-jim} -d ${JIM_DATABASE_NAME:-jim} -c "VACUUM ANALYZE;" > /dev/null 2>&1 && echo "✓ JIM data wiped successfully (preserved admin users)"'
