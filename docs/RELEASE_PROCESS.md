# JIM Release Process

This document describes how to create releases of JIM, including support for air-gapped deployments.

## Overview

JIM uses a tag-based release workflow. When we push a tag like `v0.2.0`, the GitHub Actions workflow automatically:

1. Validates the build and runs all tests
2. Builds and pushes Docker images to GitHub Container Registry (ghcr.io)
3. Publishes the PowerShell module to PSGallery
4. Creates an air-gapped deployment bundle
5. Creates a GitHub Release with all assets

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
├── images/
│   ├── jim-web.tar           # Docker image for web/API service
│   ├── jim-worker.tar        # Docker image for worker service
│   └── jim-scheduler.tar     # Docker image for scheduler service
├── compose/
│   ├── docker-compose.yml    # Main compose file
│   └── .env.example          # Environment template
├── powershell/
│   └── JIM/                   # PowerShell module directory
├── docs/
│   ├── README.md
│   ├── CHANGELOG.md
│   └── docs/                  # Full documentation
├── checksums.sha256          # SHA256 checksums for verification
└── INSTALL.md                # Installation instructions
```

### Deploying in an Air-Gapped Environment

1. **Transfer the bundle**: Copy `jim-release-X.Y.Z.tar.gz` to the target environment via approved media (USB, DVD, etc.)

2. **Verify integrity**:
   ```bash
   # Extract the bundle
   tar -xzf jim-release-X.Y.Z.tar.gz
   cd jim-release-X.Y.Z

   # Verify checksums
   sha256sum -c checksums.sha256
   ```

3. **Load Docker images**:
   ```bash
   docker load -i images/jim-web.tar
   docker load -i images/jim-worker.tar
   docker load -i images/jim-scheduler.tar
   ```

4. **Configure environment**:
   ```bash
   cd compose
   cp .env.example .env
   # Edit .env with your settings
   ```

5. **Start services**:
   ```bash
   docker compose up -d
   ```

6. **Install PowerShell module** (optional):
   ```powershell
   # Copy the module to a PowerShell module path
   Copy-Item -Recurse ./powershell/JIM $env:PSModulePath.Split(':')[0]/
   Import-Module JIM
   ```

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
