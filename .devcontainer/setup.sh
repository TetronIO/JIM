#!/bin/bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
set -e

echo "🚀 Setting up JIM development environment..."
echo ""

# Determine the workspace root directory
WORKDIR="${WORKDIR:-/workspaces/JIM}"
cd "$WORKDIR"

# Color codes for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

print_step() {
    echo -e "${BLUE}▶${NC} $1"
}

print_success() {
    echo -e "${GREEN}✓${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}⚠${NC} $1"
}

# Accumulator for post-setup action items that need the user's attention.
# Populated by individual steps (signing, gh auth, ...) when their own
# automated setup can't complete - usually because the step needs
# interactivity that postCreateCommand doesn't have (no TTY). Rendered as a
# single banner at the end of the script so the user sees one consolidated
# TODO list, not individual warnings scattered through the setup log.
PENDING_ACTIONS=""

# Self-heal stale Docker credential helper references. Delegated to a
# shared script (.devcontainer/heal-docker-creds.sh) so postStartCommand can
# run the same logic on every container start - the persistent home volume
# means a stale credsStore from a previous VS Code session survives rebuilds
# and re-bites users on the *second* time they open the project. See the
# script's header for the full rationale (BuildKit's exit-code intolerance,
# why the dev-containers-* shim is stripped unconditionally, etc.).
print_step "Checking Docker credential helper..."
bash "$WORKDIR/.devcontainer/heal-docker-creds.sh"
print_success "Docker credential helper check complete"

# Check Docker daemon health.
# The docker-in-docker feature starts its own dockerd inside this container at
# postCreate time. On some native Linux hosts (notably Fedora and Asahi Linux,
# where the iptable_nat kernel module isn't loaded by default), dockerd crashes
# during network init and leaves /var/run/docker.sock as a dead socket file.
# All docker-using commands (jim-db, jim-build, jim-stack, ...) then fail with
# "Cannot connect to the Docker daemon" until the user runs modprobe on the
# host and rebuilds the container. We detect that case here and surface the
# fix in the final summary banner so the user isn't left hunting through
# dockerd.log to figure out what went wrong. This is a no-op on Docker Desktop
# (macOS/Windows) and Codespaces, which preload the required kernel modules.
print_step "Checking Docker daemon health..."
if ! command -v docker >/dev/null 2>&1; then
    print_warning "docker CLI not found - skipping daemon health check"
elif docker info >/dev/null 2>&1; then
    print_success "Docker daemon is running"
else
    DOCKER_ERR_HINT=""
    if [ -r /tmp/dockerd.log ] && grep -qi "iptable" /tmp/dockerd.log 2>/dev/null; then
        DOCKER_ERR_HINT=" (host kernel iptable_nat module missing)"
    fi
    print_warning "Docker daemon is not responding${DOCKER_ERR_HINT}"
    PENDING_ACTIONS+="  ▶ Docker daemon inside the devcontainer is not running.
      This is almost always because the host kernel is missing the iptable_nat
      module. On the HOST (not inside this container), run:

        sudo modprobe iptable_nat
        echo iptable_nat | sudo tee /etc/modules-load.d/jim-devcontainer.conf

      Then rebuild the devcontainer (F1 -> Dev Containers: Rebuild Container).
      Until fixed, jim-db / jim-build / jim-stack will fail with
      \"Cannot connect to the Docker daemon\".
      See .devcontainer/README.md for background.

"
fi

# 1. Install .NET EF Core tools
print_step "Installing .NET Entity Framework Core tools..."
# Clean up any corrupted tool state first, then install fresh
# This handles cases where the tool cache becomes corrupted after container rebuilds
rm -rf ~/.dotnet/tools/dotnet-ef ~/.dotnet/tools/.store/dotnet-ef 2>/dev/null || true
# Use explicit version to avoid "Settings file not found" errors with latest package
if dotnet tool install --global dotnet-ef --version 10.0.3; then
    print_success "dotnet-ef 10.0.3 installed globally"
else
    print_warning "dotnet-ef installation failed - you may need to install manually: dotnet tool install --global dotnet-ef --version 10.0.3"
fi

# Add .NET tools to PATH
export PATH="$PATH:$HOME/.dotnet/tools"
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.zshrc
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.bashrc

# 2. Restore NuGet packages
print_step "Restoring NuGet packages..."
dotnet restore JIM.sln --verbosity quiet
print_success "NuGet packages restored"

# 3. Create .env file from .env.example (single source of truth)
print_step "Creating .env file..."

if [ -f .env ]; then
    print_warning ".env file already exists, skipping creation"
elif [ -n "$DOTENV_BASE64" ]; then
    # Restore from base64-encoded Codespaces secret
    echo "$DOTENV_BASE64" | base64 -d > .env
    print_success ".env restored from GitHub Codespaces secret (DOTENV_BASE64)"
else
    # Copy from .env.example template
    cp .env.example .env

    # Override with any GitHub Codespaces secrets that are set
    for var in JIM_DB_PASSWORD JIM_SSO_AUTHORITY JIM_SSO_CLIENT_ID JIM_SSO_SECRET \
               JIM_SSO_API_SCOPE JIM_SSO_VALID_ISSUERS JIM_SSO_CLAIM_TYPE \
               JIM_SSO_MV_ATTRIBUTE JIM_SSO_INITIAL_ADMIN JIM_LOG_LEVEL JIM_LOG_PATH \
               JIM_LOG_REQUESTS JIM_BENCH_API_URL JIM_BENCH_API_KEY; do
        if [ -n "${!var}" ]; then
            sed -i "s|^${var}=.*|${var}=${!var}|" .env
            print_success "  Applied Codespaces secret: ${var}"
        fi
    done

    print_success ".env created from .env.example"
    print_success "SSO pre-configured for bundled Keycloak (admin/admin, user/user)"
fi

# Surface JIM-Bench metrics streaming as a pending action when the key is
# missing or blank, so users notice that integration test runs from this
# devcontainer won't appear on the bench dashboard until it's set. The URL
# can sit at the default; only the key is treated as required.
if [ -f .env ] && ! grep -q '^JIM_BENCH_API_KEY=..*$' .env 2>/dev/null; then
    PENDING_ACTIONS+="  ▶ Integration test metrics streaming is disabled (JIM_BENCH_API_KEY
      is empty in .env). Without it, integration test runs from this
      devcontainer will not appear on the JIM-Bench Grafana dashboard.
      To enable: paste the JIM-Bench ingestion API key after
      JIM_BENCH_API_KEY= in .env (no quotes).

"
fi

# 4. Auto-tune PostgreSQL for devcontainer specs
print_step "Auto-tuning PostgreSQL for devcontainer resources..."
if [ -f "$WORKDIR/.devcontainer/postgres-tune.sh" ]; then
    if "$WORKDIR/.devcontainer/postgres-tune.sh"; then
        print_success "PostgreSQL tuned automatically"
    else
        print_warning "PostgreSQL auto-tuning failed - using base configuration"
    fi
else
    print_warning "postgres-tune.sh not found - skipping auto-tuning"
fi

# 5. Install PowerShell Pester module for testing (socat is in the Dockerfile)
print_step "Installing PowerShell Pester module..."
if pwsh -NoProfile -Command 'Set-PSRepository PSGallery -InstallationPolicy Trusted; Install-Module -Name Pester -MinimumVersion 5.0 -Force -Scope CurrentUser' 2>/dev/null; then
    print_success "Pester module installed"
else
    print_warning "Pester installation failed - you can install manually: Install-Module -Name Pester -MinimumVersion 5.0 -Force"
fi

# 5a. Make the nested pwsh binary executable for non-root users.
# The `pwsh` shim at /usr/bin/pwsh works for all users, but PowerShell's own Start-Process
# resolves to the real binary at /usr/share/powershell/.store/.../pwsh, which ships with
# 0744 permissions (only root can exec). That blocks any PowerShell script from spawning
# a pwsh subprocess (e.g. the integration runner's docker-stats capture), with a confusing
# "Permission denied" error. A single chmod +x on the real binary fixes it permanently.
print_step "Fixing nested pwsh binary permissions..."
nested_pwsh=$(find /usr/share/powershell/.store -name 'pwsh' -type f 2>/dev/null | head -1)
if [ -n "$nested_pwsh" ] && [ ! -x "$nested_pwsh" ]; then
    if sudo chmod +x "$nested_pwsh" 2>/dev/null; then
        print_success "Made $nested_pwsh executable for all users"
    else
        print_warning "Could not chmod $nested_pwsh; pwsh subprocess spawning may fail"
    fi
else
    print_success "Nested pwsh binary already executable (or not found)"
fi

# 6. Install MkDocs Material for documentation preview
print_step "Installing MkDocs Material..."
if pip install "mkdocs>=1.6,<2" "mkdocs-material>=9.7,<10" "mkdocs-glightbox>=0.4,<1" --break-system-packages --quiet 2>/dev/null; then
    print_success "MkDocs Material installed (mkdocs serve on port 8000)"
else
    print_warning "MkDocs Material installation failed - you can install manually: pip install \"mkdocs>=1.6,<2\" \"mkdocs-material>=9.7,<10\" \"mkdocs-glightbox>=0.4,<1\" --break-system-packages"
fi

# 7. Install Puppeteer and Chrome for diagram export (jim-diagrams)
print_step "Installing diagram export dependencies (Puppeteer + Chrome)..."
STRUCTURIZR_DIR="$WORKDIR/engineering/diagrams/structurizr"
if [ -f "$STRUCTURIZR_DIR/package.json" ]; then
    if (cd "$STRUCTURIZR_DIR" && npm install --silent 2>/dev/null && npx puppeteer browsers install chrome 2>/dev/null); then
        print_success "Puppeteer and Chrome installed for diagram export"
    else
        print_warning "Diagram export dependencies failed - you can install manually: cd engineering/diagrams/structurizr && npm install && npx puppeteer browsers install chrome"
    fi
else
    print_warning "Structurizr package.json not found - skipping diagram export setup"
fi

# 8. Install Playwright browser for the Playwright MCP server (in-IDE UI validation)
print_step "Installing Playwright MCP browser (Chromium)..."
# The Playwright MCP server (.mcp.json) drives a real Chromium to validate UI changes from the IDE.
# The browser binary is not baked into the image, so install it here. The version below is pinned so the
# downloaded Chromium build is deterministic; keep it aligned with the build referenced by the server in
# .mcp.json (its --executable-path, or its pinned @playwright/mcp version). Installing via the package's own
# bundled playwright-core guarantees the revision matches. Idempotent (skips the download when already
# present) and non-fatal (never blocks container creation).
PLAYWRIGHT_MCP_VERSION="0.0.75"
if npm install -g "@playwright/mcp@${PLAYWRIGHT_MCP_VERSION}" --silent 2>/dev/null \
    && PLAYWRIGHT_CORE_CLI="$(find "$(npm root -g)/@playwright/mcp" -path '*/playwright-core/cli.js' 2>/dev/null | head -1)" \
    && [ -n "$PLAYWRIGHT_CORE_CLI" ] \
    && node "$PLAYWRIGHT_CORE_CLI" install chromium >/dev/null 2>&1; then
    print_success "Playwright MCP browser installed (Chromium)"
else
    print_warning "Playwright MCP browser install failed - UI validation via the Playwright MCP will be unavailable until installed manually (see .devcontainer/README.md)"
fi

# 9. Build the solution
print_step "Building JIM solution..."
if dotnet build JIM.sln --verbosity quiet --no-restore; then
    print_success "Solution built successfully"
else
    print_warning "Build had warnings or errors. Run 'dotnet build JIM.sln' to see details."
fi

# 10. Configure Git commit signing
# Delegates to .devcontainer/configure-signing.sh which handles Codespaces
# (via gh-gpgsign) and local devcontainers (via forwarded SSH agent) and
# prints a prominent warning if neither is available. Returns non-zero if
# signing cannot be configured; we do not abort setup on that, because the
# container should still be usable for non-commit work (browsing, running
# services, etc.). The pre-commit hook in .githooks/ will catch unsigned
# commit attempts at commit time with the same warning.
print_step "Configuring git commit signing..."
SIGNING_SCRIPT="$WORKDIR/.devcontainer/configure-signing.sh"
if [ -x "$SIGNING_SCRIPT" ]; then
    if "$SIGNING_SCRIPT"; then
        print_success "Git commit signing configured"
    else
        # configure-signing.sh has already printed a detailed warning banner
        # with recovery steps. We don't print another warning inline (would be
        # duplicate noise), but we DO record an action in PENDING_ACTIONS so
        # the TODO is repeated in the final summary - in case the detailed
        # banner scrolled off screen while the rest of setup ran.
        PENDING_ACTIONS+="  ▶ Git commit signing is not configured.
      Re-run: .devcontainer/configure-signing.sh
      Until fixed, the pre-commit hook in .githooks/ will reject commits.

"
    fi
else
    print_warning "$SIGNING_SCRIPT not found or not executable - commit signing not configured"
    PENDING_ACTIONS+="  ▶ Git commit signing script is missing or not executable:
      $SIGNING_SCRIPT
      Until fixed, the pre-commit hook in .githooks/ will reject commits.

"
fi

# Install repo-local git hooks. Once configured, git uses .githooks/pre-commit
# to refuse unsigned commit attempts with a clear error. This catches cases
# where signing was configured then broke (e.g., SSH agent died, key unloaded)
# before a silent unsigned commit slips in.
print_step "Installing repo-local git hooks..."
if git -C "$WORKDIR" config --local core.hooksPath .githooks; then
    print_success "Git hooks path set to .githooks/ (pre-commit signing check active)"
else
    print_warning "Failed to set core.hooksPath; pre-commit checks will not run"
fi

# 11. Create useful shell aliases
print_step "Creating shell aliases..."

# Add source line to .zshrc if not already present
if ! grep -q "source.*jim-aliases.sh" ~/.zshrc; then
    echo "" >> ~/.zshrc
    echo "# Source JIM development aliases" >> ~/.zshrc
    echo "if [ -f \"\$HOME/.devcontainer/jim-aliases.sh\" ]; then" >> ~/.zshrc
    echo "    source \"\$HOME/.devcontainer/jim-aliases.sh\"" >> ~/.zshrc
    echo "elif [ -f \"/workspaces/JIM/.devcontainer/jim-aliases.sh\" ]; then" >> ~/.zshrc
    echo "    source \"/workspaces/JIM/.devcontainer/jim-aliases.sh\"" >> ~/.zshrc
    echo "fi" >> ~/.zshrc
    print_success "Shell aliases configured (restart terminal or run: source ~/.zshrc)"
else
    print_success "Shell aliases already configured"
fi

# Also add to .bashrc for bash users
if ! grep -q "source.*jim-aliases.sh" ~/.bashrc; then
    echo "" >> ~/.bashrc
    echo "# Source JIM development aliases" >> ~/.bashrc
    echo "if [ -f \"\$HOME/.devcontainer/jim-aliases.sh\" ]; then" >> ~/.bashrc
    echo "    source \"\$HOME/.devcontainer/jim-aliases.sh\"" >> ~/.bashrc
    echo "elif [ -f \"/workspaces/JIM/.devcontainer/jim-aliases.sh\" ]; then" >> ~/.bashrc
    echo "    source \"/workspaces/JIM/.devcontainer/jim-aliases.sh\"" >> ~/.bashrc
    echo "fi" >> ~/.bashrc
fi

# 12. Install Claude Code CLI
print_step "Installing Claude Code CLI..."
if command -v npm >/dev/null 2>&1; then
    if npm install -g @anthropic-ai/claude-code --silent 2>/dev/null; then
        print_success "Claude Code CLI installed (run: claude)"
    else
        print_warning "Claude Code CLI install failed - run manually: npm install -g @anthropic-ai/claude-code"
    fi
else
    print_warning "npm not found - skipping Claude Code CLI install"
fi

# 13. Check gh CLI authentication
# The gh CLI is used for PR/issue operations and other GitHub API calls.
# It is NOT involved in git push/pull (SSH remote handles that) or commit
# signing (SSH agent forward handles that), so a missing gh token is not
# blocking for day-to-day dev - but it is blocking for gh pr create,
# gh issue list, gh api, etc.
#
# On GitHub Codespaces this check passes automatically because Codespaces
# injects GITHUB_TOKEN into the environment, which gh auth status honours.
# On local devcontainers (Docker Desktop, Fedora, etc.) we can't prompt
# interactively here - postCreateCommand runs without a TTY - so we record
# an action for the final summary and let the user run the login command
# themselves in the VS Code integrated terminal.
print_step "Checking gh CLI authentication..."
if ! command -v gh >/dev/null 2>&1; then
    print_warning "gh CLI not found - skipping auth check"
elif gh auth status >/dev/null 2>&1; then
    print_success "gh CLI is authenticated"
else
    print_warning "gh CLI is not authenticated - gh commands will need login"
    PENDING_ACTIONS+="  ▶ gh CLI is not authenticated. Run in the integrated terminal:
      gh auth login --hostname github.com --git-protocol ssh --web
      (Needed for: gh pr create, gh issue list, gh api, etc.
       Not needed for git push/pull/commit - SSH remote handles those.)

"
fi

# 14. Mirror host SSH directory into the container's writable ~/.ssh
# The host's ~/.ssh is bind-mounted read-only at /host-ssh (see devcontainer.json
# "mounts"). We can't ssh straight from there because:
#   * the files are owned by the host user's UID (which may not be 1000 on
#     macOS / Docker Desktop), and key files are typically 0600, so the
#     vscode user often can't read them directly;
#   * ssh refuses to load a config or key from a directory that isn't strictly
#     owned by the current user.
# Mirror the directory into ~/.ssh on every container create with vscode as
# the owner and 0600/0644 permissions ssh expects. The mount stays read-only,
# so we can never modify the host's keys from inside the container.
# Nothing project-specific is named here - whatever aliases / keys you have on
# the host work in the container, nothing more.
#
# cp -rL dereferences symlinks so the actual file contents land in ~/.ssh.
# Only the host's ~/.ssh is bind-mounted, so a symlink that points outside
# (e.g. into a dotfiles repo at ~/.dotfiles/keys/) will fail to resolve and
# cp will report it - better than silently leaving a dangling link that
# ssh would then refuse to use. Workaround if you hit this: move the
# real key file into ~/.ssh, or replace the symlink with a copy.
if [ -d /host-ssh ] && [ -n "$(ls -A /host-ssh 2>/dev/null)" ]; then
    print_step "Mirroring host SSH directory into container..."
    mkdir -p ~/.ssh
    chmod 700 ~/.ssh
    if cp -rL /host-ssh/. ~/.ssh/ 2>&1; then
        find ~/.ssh -type f -exec chmod 0600 {} \;
        find ~/.ssh -type f -name '*.pub' -exec chmod 0644 {} \;
        [ -f ~/.ssh/known_hosts ] && chmod 0644 ~/.ssh/known_hosts
        print_success "Host SSH config mirrored to ~/.ssh"
    else
        print_warning "Some entries in /host-ssh could not be copied (often a symlink pointing outside ~/.ssh - move the real file into ~/.ssh on the host)."
    fi
else
    print_step "No host SSH config to mirror (/host-ssh empty or absent)"
fi

# 15. Display useful information
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${GREEN}✓ JIM Development Environment Ready!${NC}"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "Quick Start Commands:"
echo "  jim                 - List all available jim aliases"
echo "  jim-compile         - Build the entire solution"
echo "  jim-test            - Run all tests"
echo "  jim-web             - Run JIM.Web locally (sources .env)"
echo "  jim-db              - Start PostgreSQL"
echo ""
echo "Docker Stack Management (auto-kills local JIM processes):"
echo "  jim-stack           - Start Docker stack"
echo "  jim-stack-logs      - View Docker stack logs"
echo "  jim-stack-down      - Stop Docker stack"
echo ""
echo "Docker Builds (auto-kills local JIM processes, rebuild + start):"
echo "  jim-build           - Rebuild all services + start"
echo "  jim-build-light     - Start db + Keycloak, run JIM.Web natively"
echo "  jim-build-web       - Rebuild jim.web + start"
echo "  jim-build-worker    - Rebuild jim.worker + start"
echo "  jim-build-scheduler - Rebuild jim.scheduler + start"
echo ""
echo "Reset:"
echo "  jim-reset           - Full reset (containers, images, volumes)"
echo ""
echo "Database Commands:"
echo "  jim-migrate         - Apply pending migrations"
echo "  jim-migration [N]   - Create a new migration"
echo "  jim-db-logs         - View database logs"
echo "  jim-db              - Start PostgreSQL"
echo ""
echo "Available Services:"
echo "  PostgreSQL:        localhost:5432"
echo "  Keycloak IdP:      http://localhost:8181  (admin / admin)"
echo ""
echo "  When running locally (F5):"
echo "    JIM Web:         https://localhost:7000"
echo ""
echo "  When running Docker stack:"
echo "    JIM Web:         http://localhost:5200"
echo ""
echo "📖 Documentation:"
echo "  jim-docs            - Preview docs site at http://localhost:8000"
echo "  Developer Guide:   docs/developer/"
echo "  Quick Reference:   CLAUDE.md"
echo ""
echo "🚀 To start developing (choose one):"
echo ""
echo "  Option 1 - Local Debug (Recommended):"
echo "    1. Run: jim-build-light"
echo "    2. Press F5 in VS Code"
echo "    3. Sign in with: admin / admin"
echo ""
echo "  Option 2 - Docker Stack:"
echo "    1. Run: jim-stack"
echo "    2. Open: http://localhost:5200"
echo "    3. Sign in with: admin / admin"
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

# Render any pending action items as the final thing on screen, so they
# stay near the top of the user's scrollback after the help banner above
# has filled the terminal. Deliberately uses yellow (warning) rather than
# red (error) because the container is still usable - these are follow-up
# steps, not blockers.
if [ -n "$PENDING_ACTIONS" ]; then
    echo ""
    echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo -e "${YELLOW}⚠ Action required before the environment is fully ready${NC}"
    echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo ""
    echo -e "$PENDING_ACTIONS"
fi
