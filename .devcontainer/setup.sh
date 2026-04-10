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
               JIM_LOG_REQUESTS; do
        if [ -n "${!var}" ]; then
            sed -i "s|^${var}=.*|${var}=${!var}|" .env
            print_success "  Applied Codespaces secret: ${var}"
        fi
    done

    print_success ".env created from .env.example"
    print_success "SSO pre-configured for bundled Keycloak (admin/admin, user/user)"
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

# 8. Build the solution
print_step "Building JIM solution..."
if dotnet build JIM.sln --verbosity quiet --no-restore; then
    print_success "Solution built successfully"
else
    print_warning "Build had warnings or errors. Run 'dotnet build JIM.sln' to see details."
fi

# 9. Create connector-files directory with symlink to test data
print_step "Setting up connector-files directory..."
mkdir -p connector-files

# Create symlink to test/Data directory (dynamic - new files appear automatically)
if [ ! -L connector-files/test-data ]; then
    if [ -d "test/Data" ]; then
        ln -s "$(pwd)/test/Data" connector-files/test-data
        print_success "Symlink created: connector-files/test-data -> test/Data"
    else
        print_warning "test/Data directory not found, skipping symlink"
    fi
else
    print_success "Symlink already exists: connector-files/test-data"
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
        # with recovery steps. Do not print another warning here (would be
        # duplicate noise), but do leave a marker for setup completion to
        # flag the overall state.
        SIGNING_SETUP_FAILED=1
    fi
else
    print_warning "$SIGNING_SCRIPT not found or not executable - commit signing not configured"
    SIGNING_SETUP_FAILED=1
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

# 12. Display useful information
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${GREEN}✓ JIM Development Environment Ready!${NC}"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "Quick Start Commands:"
echo "  jim                - List all available jim aliases"
echo "  jim-compile        - Build the entire solution"
echo "  jim-test           - Run all tests"
echo "  jim-web            - Run the Blazor web application (local, debuggable)"
echo "  jim-db             - Start PostgreSQL"
echo ""
echo "Docker Stack Commands:"
echo "  jim-stack          - Start Docker stack (production-like)"
echo "  jim-stack-logs     - View Docker stack logs"
echo "  jim-stack-down     - Stop Docker stack"
echo ""
echo "Docker Builds (rebuild services):"
echo "  jim-build          - Build all services + start"
echo "  jim-build-web      - Build jim.web + start"
echo "  jim-build-worker   - Build jim.worker + start"
echo "  jim-build-scheduler - Build jim.scheduler + start"
echo ""
echo "Reset:"
echo "  jim-reset          - Reset JIM (delete database & logs volumes)"
echo ""
echo "Database Commands:"
echo "  jim-migrate        - Apply pending migrations"
echo "  jim-migration [N]  - Create a new migration"
echo "  jim-db-logs        - View database logs"
echo "  jim-db             - Start PostgreSQL"
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
echo "  jim-docs           - Preview docs site at http://localhost:8000"
echo "  Developer Guide:   docs/developer/"
echo "  Quick Reference:   CLAUDE.md"
echo ""
echo "🚀 To start developing (choose one):"
echo ""
echo "  Option 1 - Local Debug (Recommended):"
echo "    1. Run: jim-db && jim-keycloak"
echo "    2. Press F5 in VS Code"
echo "    3. Sign in with: admin / admin"
echo ""
echo "  Option 2 - Docker Stack:"
echo "    1. Run: jim-stack"
echo "    2. Open: http://localhost:5200"
echo "    3. Sign in with: admin / admin"
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
