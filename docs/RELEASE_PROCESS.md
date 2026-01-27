# JIM Release Process

This document describes how to create releases of JIM, including support for air-gapped deployments.

## Overview

JIM uses a tag-based release workflow. When we push a tag like `v0.2.0`, the GitHub Actions workflow automatically:

1. Validates the build and runs all tests
2. Builds and pushes Docker images to GitHub Container Registry (ghcr.io)
3. Publishes the PowerShell module to PSGallery
4. Creates an air-gapped deployment bundle
5. Creates a GitHub Release with all assets

## Release History

| Version | Date | Notes |
|---------|------|-------|
| `0.2.0-alpha` | 2026-01-27 | PowerShell module expanded from 3 to 64 cmdlets. Published to [PSGallery](https://www.powershellgallery.com/packages/JIM/0.2.0-alpha) manually (no git tag created). |
| `0.1.0-alpha` | 2025-12-12 | Initial preview. PowerShell module published to [PSGallery](https://www.powershellgallery.com/packages/JIM/0.1.0-alpha) with 3 connection cmdlets. Published manually (no git tag created). |

> **Note:** Keep this table updated whenever a release is published so the project has a single source of truth for what has shipped, independent of external services like PSGallery or GHCR.

## Version Management

### VERSION File

The `VERSION` file in the repository root is the single source of truth for the version number. It contains just the version string (e.g., `0.2.0`).

All projects automatically read this version via `Directory.Build.props`, so you only need to update one file.

### Updating the Version

1. Edit the `VERSION` file with the new version number
2. Update `CHANGELOG.md` with the release notes
3. Commit both changes
4. Create and push a tag matching the version

```bash
# Update VERSION file
echo "0.3.0" > VERSION

# Update CHANGELOG.md (move Unreleased items to new version section)

# Commit
git add VERSION CHANGELOG.md
git commit -m "Bump version to 0.3.0"

# Create and push tag
git tag v0.3.0
git push origin main --tags
```

## Creating a Release

### Prerequisites

1. **PSGALLERY_API_KEY** secret configured in GitHub repository settings (for PowerShell module publishing)
2. Write access to push tags
3. All tests passing on main branch

### Steps

1. **Update the changelog**: Move items from `[Unreleased]` to a new version section in `CHANGELOG.md`

2. **Update the version**: Edit the `VERSION` file

3. **Commit changes**:
   ```bash
   git add VERSION CHANGELOG.md
   git commit -m "Release v0.3.0"
   git push origin main
   ```

4. **Create the release tag**:
   ```bash
   git tag v0.3.0
   git push origin v0.3.0
   ```

5. **Monitor the workflow**: The release workflow will run automatically. Check the Actions tab for progress.

6. **Verify the release**: Once complete, verify:
   - GitHub Release page has the bundle and checksums
   - Docker images are available at `ghcr.io/tetronio/jim-web:0.3.0` (etc.)
   - PowerShell module is available on PSGallery

## Air-Gapped Deployments

JIM supports deployment into air-gapped (disconnected) environments where there is no Internet access.

### Release Bundle Contents

The release bundle (`jim-release-X.Y.Z.tar.gz`) contains:

```
jim-release-X.Y.Z/
+-- images/
|   +-- jim-web.tar           # Docker image for web/API service
|   +-- jim-worker.tar        # Docker image for worker service
|   +-- jim-scheduler.tar     # Docker image for scheduler service
+-- compose/
|   +-- docker-compose.yml    # Main compose file
|   +-- .env.example          # Environment template
+-- powershell/
|   +-- JIM/                  # PowerShell module directory
+-- docs/
|   +-- README.md
|   +-- CHANGELOG.md
|   +-- docs/                 # Full documentation
+-- checksums.sha256          # SHA256 checksums for verification
+-- INSTALL.md                # Installation instructions
```

### Deploying in an Air-Gapped Environment

#### Prerequisites

Before deploying JIM, ensure you have:

- **Docker Engine** (20.10+) and **Docker Compose** (v2+) installed
- **PostgreSQL 18** - either as a container or external database server
- A DNS name or IP address for the JIM server
- TLS certificates if enabling HTTPS (recommended for production)
- An OIDC identity provider accessible from the air-gapped network (e.g., AD FS, Keycloak)

#### Step 1: Transfer and Verify the Bundle

```bash
# Transfer jim-release-X.Y.Z.tar.gz via approved media (USB, DVD, etc.)

# Extract the bundle
tar -xzf jim-release-X.Y.Z.tar.gz
cd jim-release-X.Y.Z

# Verify checksums
sha256sum -c checksums.sha256
```

#### Step 2: Load Docker Images

```bash
# Load JIM images
docker load -i images/jim-web.tar
docker load -i images/jim-worker.tar
docker load -i images/jim-scheduler.tar

# If using bundled PostgreSQL (optional)
docker load -i images/postgres.tar
```

#### Step 3: Set Up PostgreSQL

**Option A: Use the bundled PostgreSQL container** (simpler, suitable for smaller deployments)

The `docker-compose.yml` uses Docker Compose profiles to make the database service optional. To include the bundled PostgreSQL:

```bash
# Start with bundled database
docker compose --profile with-db up -d
```

**Option B: Use an external PostgreSQL server** (recommended for production)

If you have an existing PostgreSQL server:

1. Create a database and user:
   ```sql
   CREATE DATABASE jim;
   CREATE USER jim WITH ENCRYPTED PASSWORD 'your_secure_password';
   GRANT ALL PRIVILEGES ON DATABASE jim TO jim;
   ```

2. Update `.env` with your database connection:
   ```
   DB_HOSTNAME=your-postgres-server.local
   DB_NAME=jim
   DB_USERNAME=jim
   DB_PASSWORD=your_secure_password
   ```

3. Start JIM without the database profile:
   ```bash
   # Start without bundled database (uses external)
   docker compose up -d
   ```

The JIM services will connect to your external PostgreSQL server using the `DB_HOSTNAME` from `.env`.

#### Step 4: Configure Environment

```bash
cd compose
cp .env.example .env
```

Edit `.env` with your settings:

```bash
# Database (if using external PostgreSQL)
DB_HOSTNAME=your-postgres-server.local
DB_NAME=jim
DB_USERNAME=jim
DB_PASSWORD=your_secure_password

# SSO/OIDC - Configure for your air-gapped identity provider
SSO_AUTHORITY=https://adfs.your-domain.local/adfs
SSO_CLIENT_ID=your-client-id
SSO_SECRET=your-client-secret
SSO_API_SCOPE=api://your-client-id/access_as_user

# User identity mapping
SSO_UNIQUE_IDENTIFIER_CLAIM_TYPE=sub
SSO_UNIQUE_IDENTIFIER_METAVERSE_ATTRIBUTE_NAME=Subject Identifier
SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE=your-admin-identifier

# Logging
LOGGING_LEVEL=Information
LOGGING_PATH=/var/log/jim
```

#### Step 5: Configure TLS (Recommended for Production)

JIM can be deployed behind a reverse proxy (nginx, Traefik, HAProxy) for TLS termination, or you can configure TLS directly.

**Option A: Reverse Proxy (Recommended)**

Deploy nginx or another reverse proxy in front of JIM:

```nginx
# /etc/nginx/sites-available/jim
server {
    listen 443 ssl;
    server_name jim.your-domain.local;

    ssl_certificate /etc/ssl/certs/jim.crt;
    ssl_certificate_key /etc/ssl/private/jim.key;

    location / {
        proxy_pass http://localhost:5200;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

**Option B: Direct TLS in Docker**

Mount certificates into the container and configure ASP.NET Core to use them (requires additional configuration in `docker-compose.yml`).

#### Step 6: Configure DNS

Ensure your JIM server is resolvable by name in your network:

1. Add a DNS A record pointing to your JIM server's IP address
2. Or add an entry to `/etc/hosts` on client machines:
   ```
   192.168.1.100  jim.your-domain.local
   ```

The OIDC redirect URIs configured in your identity provider must match the JIM server's accessible URL.

#### Step 7: Configure File Connector Volumes (If Using File Connector)

If you plan to use the File Connector to import/export CSV files, configure a volume mount:

1. Create a directory on the host for connector files:
   ```bash
   mkdir -p /opt/jim/connector-files
   ```

2. Add a volume mount to `docker-compose.yml` for the `jim.worker` service:
   ```yaml
   jim.worker:
     volumes:
       - jim-logs-volume:/var/log/jim
       - /opt/jim/connector-files:/var/connector-files
   ```

3. Place CSV files in `/opt/jim/connector-files/` on the host

4. In JIM, configure the File Connector with the container path: `/var/connector-files/yourfile.csv`

#### Step 8: Start Services

```bash
# With bundled PostgreSQL:
docker compose --profile with-db up -d

# With external PostgreSQL:
docker compose up -d

# Check all services are running
docker compose ps

# View logs
docker compose logs -f
```

#### Step 9: Apply Database Migrations

On first run, apply database migrations:

```bash
docker compose exec jim.web dotnet ef database update
```

#### Step 10: Access JIM

1. Open your browser to `https://jim.your-domain.local` (or `http://localhost:5200` if no TLS)
2. Log in with your SSO credentials
3. The initial admin user (configured via `SSO_UNIQUE_IDENTIFIER_INITIAL_ADMIN_CLAIM_VALUE`) will have full access

#### Step 11: Install PowerShell Module (Optional)

For automation and scripting:

```powershell
# Copy the module to a PowerShell module path
Copy-Item -Recurse ./powershell/JIM "$env:USERPROFILE\Documents\PowerShell\Modules\"

# Or for Linux/macOS
Copy-Item -Recurse ./powershell/JIM ~/.local/share/powershell/Modules/

# Import and connect
Import-Module JIM
Connect-JIM -BaseUrl "https://jim.your-domain.local" -ApiKey "your-api-key"
```

### Air-Gapped Network Checklist

Before going live, verify:

- [ ] All Docker images loaded successfully (`docker images | grep jim`)
- [ ] PostgreSQL is accessible and migrations applied
- [ ] SSO/OIDC identity provider is accessible from JIM server
- [ ] DNS resolves JIM server name correctly
- [ ] TLS certificates are valid and trusted (if using HTTPS)
- [ ] Firewall allows traffic on required ports (5200/HTTP, 443/HTTPS, 5432/PostgreSQL)
- [ ] File connector volumes mounted (if using File Connector)
- [ ] Initial admin user can log in
- [ ] Logs are being written to configured path

### Building a Release Bundle Locally

You can build a release bundle locally for testing:

```powershell
# Build with current images
./scripts/Build-ReleaseBundle.ps1 -Version 0.2.0 -OutputPath ./release

# Skip image export (faster, for testing bundle structure)
./scripts/Build-ReleaseBundle.ps1 -Version 0.2.0 -OutputPath ./release -SkipImageExport

# Include PostgreSQL image (larger bundle, ~1GB+)
./scripts/Build-ReleaseBundle.ps1 -Version 0.2.0 -OutputPath ./release -IncludePostgres
```

## Docker Images

### Image Names and Tags

Images are published to GitHub Container Registry:

| Service | Image |
|---------|-------|
| Web/API | `ghcr.io/tetronio/jim-web` |
| Worker | `ghcr.io/tetronio/jim-worker` |
| Scheduler | `ghcr.io/tetronio/jim-scheduler` |

Each release creates the following tags:
- `X.Y.Z` - Exact version (e.g., `0.2.0`)
- `X.Y` - Minor version (e.g., `0.2`)
- `latest` - Most recent release

### Using Published Images

To use published images instead of building locally:

```bash
# Set environment variables
export DOCKER_REGISTRY=ghcr.io/tetronio/
export JIM_VERSION=0.2.0

# Start services
docker compose up -d
```

Or add to your `.env` file:
```
DOCKER_REGISTRY=ghcr.io/tetronio/
JIM_VERSION=0.2.0
```

### Image Signing

All images are signed using [cosign](https://github.com/sigstore/cosign) for supply chain security. To verify an image:

```bash
cosign verify ghcr.io/tetronio/jim-web:0.2.0
```

## PowerShell Module

The JIM PowerShell module is automatically published to [PSGallery](https://www.powershellgallery.com/) on each release.

### Installing from PSGallery

```powershell
Install-Module -Name JIM
```

### Installing from Release Bundle

For air-gapped environments, copy the module from the release bundle:

```powershell
# From the extracted bundle
Copy-Item -Recurse ./powershell/JIM "$env:USERPROFILE\Documents\PowerShell\Modules\"
Import-Module JIM
```

## Troubleshooting

### Release Workflow Fails

1. **Build/test failures**: Check the workflow logs for specific errors
2. **PSGallery publish fails**: Verify `PSGALLERY_API_KEY` secret is set correctly
3. **Docker push fails**: Ensure `GITHUB_TOKEN` has `packages: write` permission

### Image Load Fails in Air-Gapped Environment

1. Verify the `.tar` file wasn't corrupted during transfer:
   ```bash
   sha256sum -c checksums.sha256
   ```

2. Ensure sufficient disk space for Docker images

3. Check Docker is running:
   ```bash
   docker info
   ```

### Version Mismatch

If versions appear inconsistent:

1. Verify `VERSION` file contains the correct version
2. Run a clean build: `dotnet clean && dotnet build`
3. Check the tag matches the VERSION file content

## Security Considerations

1. **Verify checksums**: Always verify SHA256 checksums before deploying in production
2. **Verify signatures**: Use cosign to verify image signatures when possible
3. **Review changes**: Check CHANGELOG.md for security-relevant changes before upgrading
4. **Backup before upgrade**: Always backup your database before upgrading JIM
