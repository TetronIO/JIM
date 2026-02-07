#!/bin/bash
set -e

echo "🚀 Setting up JIM development environment..."
echo ""

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
if dotnet tool install --global dotnet-ef --version 9.0.0; then
    print_success "dotnet-ef 9.0.0 installed globally"
else
    print_warning "dotnet-ef installation failed - you may need to install manually: dotnet tool install --global dotnet-ef --version 9.0.0"
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
    print_warning "Remember to update SSO settings for real authentication!"
fi

# 4. Install PowerShell Pester module for testing
print_step "Installing PowerShell Pester module..."
if pwsh -NoProfile -Command 'Set-PSRepository PSGallery -InstallationPolicy Trusted; Install-Module -Name Pester -MinimumVersion 5.0 -Force -Scope CurrentUser' 2>/dev/null; then
    print_success "Pester module installed"
else
    print_warning "Pester installation failed - you can install manually: Install-Module -Name Pester -MinimumVersion 5.0 -Force"
fi

# 5. Build the solution
print_step "Building JIM solution..."
if dotnet build JIM.sln --verbosity quiet --no-restore; then
    print_success "Solution built successfully"
else
    print_warning "Build had warnings or errors. Run 'dotnet build JIM.sln' to see details."
fi

# 6. Create connector-files directory with symlink to test data
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

# 7. Configure Git SSH commit signing
print_step "Configuring Git SSH commit signing..."

# Check if SSH agent has keys forwarded
if ssh-add -l &>/dev/null; then
    # Get the first SSH key from the agent
    SSH_KEY=$(ssh-add -L | head -1)

    if [ -n "$SSH_KEY" ]; then
        # Configure git to use SSH signing
        git config --global gpg.format ssh
        git config --global commit.gpgsign true
        git config --global user.signingkey "key::$SSH_KEY"

        # Create allowed_signers file for local verification
        # Uses the git user email (if configured) or a placeholder
        GIT_EMAIL=$(git config --global user.email || echo "developer@local")
        mkdir -p ~/.ssh
        echo "$GIT_EMAIL $SSH_KEY" > ~/.ssh/allowed_signers
        git config --global gpg.ssh.allowedSignersFile ~/.ssh/allowed_signers

        print_success "Git SSH signing configured (key from SSH agent)"
    else
        print_warning "SSH agent has no keys - commit signing not configured"
    fi
else
    print_warning "SSH agent not available - commit signing not configured"
    print_warning "To enable signing, ensure SSH agent forwarding is working"
fi

# 8. Create useful shell aliases
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

# 9. Display useful information
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
echo ""
echo "  When running locally (F5):"
echo "    JIM Web:         https://localhost:7000"
echo ""
echo "  When running Docker stack:"
echo "    JIM Web:         http://localhost:5200"
echo ""
echo "📖 Documentation:"
echo "  Developer Guide:   docs/DEVELOPER_GUIDE.md"
echo "  Quick Reference:   CLAUDE.md"
echo ""
echo "🚀 To start developing (choose one):"
echo ""
echo "  Option 1 - Local Debug (Recommended):"
echo "    1. Press F5 in VS Code"
echo "    2. Select 'JIM Full Stack' or 'JIM Web Stack'"
echo "    3. Set breakpoints and debug"
echo ""
echo "  Option 2 - Docker Stack:"
echo "    1. Review .env file and update SSO settings"
echo "    2. Run: jim-stack"
echo "    3. Open: http://localhost:5200"
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
