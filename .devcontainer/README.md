# JIM Development Container Configuration

This directory contains the configuration for GitHub Codespaces and VS Code Dev Containers, providing a fully automated development environment for JIM.

## 🚀 Quick Start

### Using GitHub Codespaces

1. Click the "Code" button on the GitHub repository
2. Select "Codespaces" tab
3. Click "Create codespace on main"
4. Wait 3-5 minutes for automatic setup
5. Start coding! Everything is pre-configured.

### Using VS Code Dev Containers (Local)

1. Install Docker Desktop
2. Install VS Code extension: "Dev Containers"
3. Open JIM repository in VS Code
4. Press `F1` → "Dev Containers: Reopen in Container"
5. Wait for container to build and setup to complete

## ✨ What's Included

### Pre-installed Tools

- **.NET 9.0 SDK** - Latest .NET framework
- **dotnet-ef** - Entity Framework Core CLI tools
- **Docker-in-Docker** - Run docker-compose inside the container
- **GitHub CLI (gh)** - GitHub operations from terminal
- **Zsh + Oh My Zsh** - Enhanced shell with useful features

### VS Code Extensions

- **Anthropic.claude-code** - Claude Code AI assistant
- **ms-dotnettools.csharp** - C# language support
- **ms-dotnettools.csdevkit** - C# Dev Kit
- **AykutSarac.jsoncrack-vscode** - JSON visualizer
- **cweijan.vscode-postgresql-client2** - PostgreSQL client
- **editorconfig.editorconfig** - EditorConfig support
- **humao.rest-client** - REST API testing
- **eamodio.gitlens** - Git supercharged
- And more...

### Automatic Setup

When the container is created, `setup.sh` automatically:

1. ✅ Installs/updates `dotnet-ef` tools
2. ✅ Restores NuGet packages
3. ✅ Creates `.env` file with defaults (or from GitHub secrets)
4. ✅ Starts PostgreSQL database
5. ✅ Applies Entity Framework migrations
6. ✅ Builds the JIM solution
7. ✅ Creates helpful shell aliases

### Port Forwarding

Automatically configured ports:

| Port | Service | Auto-Open |
|------|---------|-----------|
| 5432 | PostgreSQL | Silent |
| 8080 | Adminer (DB UI) | Notify |
| 5000 | JIM Web (HTTP) | Notify |
| 7000 | JIM Web (HTTPS) | Open Browser |
| 5203 | JIM API (HTTP) | Notify |
| 7203 | JIM API (HTTPS) | Open Preview |

## 🔧 Configuration Files

### `devcontainer.json`

Main configuration file defining:
- Docker Compose integration
- VS Code extensions and settings
- Port forwarding
- Container features (Docker-in-Docker, .NET SDK)
- Environment variables

### `setup.sh`

Post-creation script that:
- Sets up the development environment
- Creates `.env` file
- Starts and configures the database
- Builds the solution
- Creates shell aliases

### `../.vscode/launch.json`

Debugging configurations for:
- JIM Web (Blazor) - F5 to debug
- JIM API - Debug the REST API
- JIM Worker - Debug background worker
- JIM Scheduler - Debug the scheduler
- **Compound configs** - Debug multiple services at once

### `../.vscode/tasks.json`

Build tasks accessible via `Ctrl+Shift+B`:
- Build individual projects or entire solution
- Run all tests or specific test projects
- Database migrations (add, remove, update)
- Docker operations (up, down, logs)

## 🔐 Managing Secrets

### Option 1: GitHub Codespaces Secrets (Recommended)

Set secrets at: https://github.com/settings/codespaces

**Individual Secrets:**
```
DB_PASSWORD=your-secure-password
SSO_AUTHORITY=https://login.microsoftonline.com/your-tenant/v2.0
SSO_CLIENT_ID=your-client-id
SSO_SECRET=your-client-secret
```

**OR Entire .env as Base64:**
```bash
# Locally, encode your .env
cat .env | base64

# Create GitHub secret called DOTENV_BASE64 with the output
```

The `setup.sh` script will automatically use these secrets when creating `.env`.

### Option 2: Manual .env (Simplest)

Just create `.env` manually in the Codespace. It's gitignored and persists for 30 days.

## 🛠️ Shell Aliases

The setup creates these handy aliases:

### Build & Test
```bash
jim-build          # Build entire solution
jim-test           # Run all tests
jim-clean          # Clean and rebuild
```

### Run Services
```bash
jim-web            # Run Blazor web app
jim-api            # Run REST API
jim-worker         # Run background worker
```

### Database
```bash
jim-db             # Start PostgreSQL
jim-db-stop        # Stop PostgreSQL
jim-db-logs        # View database logs
jim-adminer        # Get Adminer URL
jim-migrate        # Apply migrations
jim-migration [N]  # Create new migration
```

## 🐛 Debugging

### Debug Single Service

1. Open Debug panel (`Ctrl+Shift+D`)
2. Select configuration (e.g., "JIM Web (Blazor)")
3. Press `F5`
4. Set breakpoints and debug!

### Debug Multiple Services

Use compound configurations:

- **"JIM Full Stack"** - Web + API + Worker
- **"JIM Web Stack"** - Web + API only

Select from debug dropdown and press `F5` to debug all at once.

### Debug Configuration Features

- **Auto-open browser** when service starts
- **Hot reload** enabled for development
- **Source maps** configured for Blazor
- **Proper environment variables** set automatically

## 🔄 Codespace Lifecycle

### When Created
- Full setup runs (~3-5 minutes)
- Database migrations applied
- Solution built
- Ready to code

### When Started (after stop)
- Database automatically restarts
- No rebuild needed
- Instant resume

### When Deleted (after 30 days)
- Devcontainer configuration is preserved in repo
- Next Codespace recreates environment automatically
- Secrets are preserved in GitHub

## 📝 Customization

### Add More VS Code Extensions

Edit `devcontainer.json`:
```json
"extensions": [
  "existing.extensions",
  "publisher.your-extension"
]
```

### Add More Shell Aliases

Edit `setup.sh`, add to the aliases section:
```bash
alias your-alias='your-command'
```

### Modify Port Forwarding

Edit `devcontainer.json`:
```json
"forwardPorts": [5432, 8080, 9999],
"portsAttributes": {
  "9999": {
    "label": "My Service",
    "onAutoForward": "openBrowser"
  }
}
```

## 🆘 Troubleshooting

### Database Won't Start

```bash
docker compose -f db.yml up -d
docker compose -f db.yml logs
```

### Migrations Fail

```bash
# Ensure database is running
docker compose -f db.yml ps

# Apply migrations manually
dotnet ef database update --project JIM.PostgresData
```

### Build Errors

```bash
# Clean rebuild
jim-clean

# Or manually
dotnet clean JIM.sln
dotnet restore JIM.sln
dotnet build JIM.sln
```

### Extensions Not Installing

1. Press `F1`
2. Type "Developer: Reload Window"
3. Extensions will retry installation

### Need to Reset Everything

```bash
# Stop and remove database
docker compose -f db.yml down -v

# Re-run setup
bash .devcontainer/setup.sh
```

## 📚 Resources

- [VS Code Dev Containers](https://code.visualstudio.com/docs/devcontainers/containers)
- [GitHub Codespaces](https://docs.github.com/en/codespaces)
- [JIM Developer Guide](../docs/DEVELOPER_GUIDE.md)
- [JIM Quick Reference](../CLAUDE.md)

## 💡 Tips

1. **Use shell aliases** - Much faster than typing full commands
2. **Use compound debug configs** - Debug multiple services simultaneously
3. **Use Tasks** - `Ctrl+Shift+B` for quick builds
4. **Use GitHub Codespaces secrets** - Keep sensitive data secure
5. **Use Adminer** - Visual database management at localhost:8080
6. **Check setup logs** - If something fails, setup.sh shows helpful output

---

**Happy coding! The environment is ready for you to focus on building JIM.** 🚀
