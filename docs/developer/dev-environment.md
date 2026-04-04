---
title: Development Environment
---

# Development Environment

JIM provides a fully configured development environment through GitHub Codespaces and VS Code devcontainers. All dependencies — .NET 9.0 SDK, Docker, PostgreSQL, VS Code extensions, and shell aliases — are pre-installed and ready to use.

## GitHub Codespaces (Recommended)

The fastest way to get started is with GitHub Codespaces:

1. Open the [JIM repository](https://github.com/TetronIO/JIM) on GitHub
2. Click **Code** > **Codespaces** > **Create codespace on main**
3. Wait for provisioning (automatic setup via `.devcontainer/setup.sh`)
4. Start developing — all tools and aliases are available immediately

The setup script automatically creates a `.env` file with development defaults. SSO is pre-configured for the bundled Keycloak instance — sign in with `admin` / `admin`.

### Custom Environment Variables in Codespaces

To restore your own `.env` file automatically in Codespaces, set a `DOTENV_BASE64` secret in your GitHub Codespaces settings. The setup script will decode and apply it during provisioning.

## Local Devcontainer

If you prefer to develop locally:

1. Clone the repository: `git clone https://github.com/TetronIO/JIM.git`
2. Open the folder in VS Code
3. Install the [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) extension
4. When prompted, click **Reopen in Container** (or use the command palette: `Dev Containers: Reopen in Container`)
5. Wait for the container to build and configure

The devcontainer provides the same environment as Codespaces — all dependencies, extensions, and shell aliases are configured automatically.

## Bundled Keycloak

JIM ships a bundled Keycloak instance for development SSO, removing the need for an external identity provider during development.

| Setting | Value |
|---------|-------|
| Admin console URL | `http://localhost:8181` |
| Admin credentials | `admin` / `admin` |
| Pre-configured realm | `jim` |

Keycloak management aliases:

- `jim-keycloak` — Start Keycloak only (for local debugging workflow)
- `jim-keycloak-stop` — Stop Keycloak
- `jim-keycloak-logs` — View Keycloak logs

## Shell Aliases

Aliases are automatically configured from `.devcontainer/jim-aliases.sh`. If aliases are not available, run `source ~/.zshrc` or restart the terminal.

### Build and Test

| Alias | Description |
|-------|-------------|
| `jim` | List all available jim aliases |
| `jim-compile` | Build entire solution (`dotnet build JIM.sln`) |
| `jim-test` | Run unit and workflow tests (excludes Explicit) |
| `jim-test-all` | Run all tests including Explicit and Pester |

### Database

| Alias | Description |
|-------|-------------|
| `jim-db` | Start PostgreSQL (for local debugging) |
| `jim-db-stop` | Stop PostgreSQL |
| `jim-migrate` | Apply EF Core migrations |
| `jim-postgres-tune` | Re-tune PostgreSQL for current CPU/RAM |

### Docker Stack

| Alias | Description |
|-------|-------------|
| `jim-stack` | Start the full Docker stack |
| `jim-stack-logs` | View Docker stack logs |
| `jim-stack-down` | Stop the Docker stack |
| `jim-restart` | Restart stack (re-reads `.env`, no rebuild) |

### Docker Builds

Rebuild and restart services after code changes:

| Alias | Description |
|-------|-------------|
| `jim-build` | Build all services and start |
| `jim-build-web` | Build jim.web and start |
| `jim-build-worker` | Build jim.worker and start |
| `jim-build-scheduler` | Build jim.scheduler and start |

### Other

| Alias | Description |
|-------|-------------|
| `jim-reset` | Reset JIM (delete database and log volumes) |
| `jim-diagrams` | Export Structurizr C4 diagrams as SVG |
| `jim-prd` | Create a new PRD from template |

## Development Workflows

Choose one of two workflows depending on your task:

### Workflow 1 — Local Debugging (Recommended)

Best for active development with breakpoints and hot reload.

1. Start the database: `jim-db`
2. Start Keycloak (if needed): `jim-keycloak`
3. Press **F5** in VS Code to launch with the debugger
4. Access JIM at `https://localhost:7000`, Swagger at `/api/swagger`

### Workflow 2 — Full Docker Stack

Best for integration testing or a production-like environment.

1. Start all services: `jim-stack`
2. Access JIM at `http://localhost:5200`, Swagger at `/api/swagger`

!!! note "Rebuilding after code changes"
    When running the Docker stack, compiled code changes require a container rebuild. Use `jim-build-web`, `jim-build-worker`, or `jim-build-scheduler` to rebuild affected services. Simply refreshing the browser is not sufficient.

## Development URLs

| URL | Description |
|-----|-------------|
| `http://localhost:5200` | JIM Web UI (Docker stack) |
| `http://localhost:5200/api/swagger` | Swagger API documentation |
| `http://localhost:8181` | Keycloak admin console (`admin` / `admin`) |
| `https://localhost:7000` | JIM Web UI (local debugging) |
| `http://localhost:8000` | MkDocs documentation preview |

## Documentation Preview

The devcontainer includes [MkDocs Material](https://squidfunk.github.io/mkdocs-material/) for previewing the documentation site locally. To start the preview server:

```bash
jim-docs
```

This runs `mkdocs serve` bound to `0.0.0.0:8000` and opens a live-reloading preview. Changes to files in `docs/` or `mkdocs.yml` are reflected immediately in the browser.

To build the static site without serving:

```bash
jim-docs-build
```

## Git Configuration

VS Code automatically copies your host machine's `~/.gitconfig` into devcontainers (controlled by the `dev.containers.copyGitConfig` setting, enabled by default). Configure Git on your **host machine** and it will be available in all devcontainers automatically.

### Required Setup (Host Machine)

```bash
git config --global user.name "Your Name"
git config --global user.email "your@email.com"
```

### Optional — SSH Commit Signing

```bash
git config --global gpg.format ssh
git config --global commit.gpgsign true
git config --global user.signingkey "key::ssh-ed25519 AAAA... your-comment"
```

!!! tip
    Use the `key::` prefix for `user.signingkey` so the key is stored as a literal string rather than a file path. File paths from the host will not exist inside the container.

VS Code automatically forwards the SSH agent into the container, so SSH authentication for `git push` and `git pull` works without copying keys. VS Code also injects a credential helper that forwards HTTPS credential requests back to the host.
