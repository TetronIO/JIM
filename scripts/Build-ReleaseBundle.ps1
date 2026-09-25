# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds a release bundle for air-gapped JIM deployments.

.DESCRIPTION
    Creates a self-contained release package containing:
    - Pre-built Docker images (exported as .tar files)
    - Docker Compose configuration files
    - PowerShell module
    - Installation documentation
    - SHA256 checksums for integrity verification

.PARAMETER Version
    The version number for the release (e.g., "0.2.0").
    If not specified, reads from the VERSION file.

.PARAMETER OutputPath
    The directory where the release bundle will be created.
    Defaults to ./release-output.

.PARAMETER SkipImageExport
    Skip exporting Docker images (useful for testing the bundle structure).

.PARAMETER IncludePostgres
    Include the PostgreSQL image in the bundle. Defaults to true.

.EXAMPLE
    ./Build-ReleaseBundle.ps1 -Version "0.2.0"

    Builds a release bundle for version 0.2.0.

.EXAMPLE
    ./Build-ReleaseBundle.ps1 -SkipImageExport

    Builds the bundle structure without exporting Docker images.

.NOTES
    This script is typically run by the CI/CD pipeline but can also be
    run locally for testing or manual releases.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version,

    [Parameter()]
    [string]$OutputPath = "./release-output",

    [switch]$SkipImageExport,

    [bool]$IncludePostgres = $true
)

$ErrorActionPreference = 'Stop'

# Determine repository root
$RepoRoot = Split-Path -Parent $PSScriptRoot

# Read PostgreSQL image reference from docker-compose.yml (single source of truth).
# The digest-pinned image in docker-compose.yml is maintained by Dependabot.
$composeContent = Get-Content (Join-Path $RepoRoot "docker-compose.yml") -Raw
if ($composeContent -match 'image:\s+((?:docker\.io/library/)?postgres:[^\s]+)') {
    $PostgresImage = $Matches[1]
    Write-Host "PostgreSQL image from docker-compose.yml: $PostgresImage" -ForegroundColor Gray
} else {
    throw "Could not find PostgreSQL image reference in docker-compose.yml"
}
Push-Location $RepoRoot

try {
    # Read version from VERSION file if not specified
    if (-not $Version) {
        $versionFile = Join-Path $RepoRoot "VERSION"
        if (Test-Path $versionFile) {
            $Version = (Get-Content $versionFile -Raw).Trim()
        }
        else {
            throw "VERSION file not found and -Version parameter not specified."
        }
    }

    Write-Host "Building release bundle for JIM v$Version" -ForegroundColor Cyan
    Write-Host "Output path: $OutputPath" -ForegroundColor Gray

    # Create output directory structure
    $bundleName = "jim-release-$Version"
    $bundlePath = Join-Path $OutputPath $bundleName

    if (Test-Path $bundlePath) {
        Write-Host "Removing existing bundle directory..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $bundlePath
    }

    $directories = @(
        "$bundlePath/docker-images"
        "$bundlePath/compose"
        "$bundlePath/powershell"
        "$bundlePath/docs"
    )

    foreach ($dir in $directories) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    # Define Docker images
    $jimImages = @(
        @{ Name = "jim-web"; Dockerfile = "src/JIM.Web/Dockerfile"; Context = "." }
        @{ Name = "jim-worker"; Dockerfile = "src/JIM.Worker/Dockerfile"; Context = "." }
        @{ Name = "jim-scheduler"; Dockerfile = "src/JIM.Scheduler/Dockerfile"; Context = "." }
    )

    if (-not $SkipImageExport) {
        Write-Host "`nBuilding and exporting Docker images..." -ForegroundColor Cyan

        foreach ($image in $jimImages) {
            $imageName = $image.Name
            $imageTag = "ghcr.io/tetronio/${imageName}:$Version"

            Write-Host "  Building $imageName..." -ForegroundColor Gray
            docker build -t $imageTag -f $image.Dockerfile $image.Context --build-arg VERSION=$Version

            if ($LASTEXITCODE -ne 0) {
                throw "Failed to build $imageName"
            }

            Write-Host "  Exporting $imageName..." -ForegroundColor Gray
            $tarPath = Join-Path $bundlePath "docker-images/$imageName.tar"
            docker save -o $tarPath $imageTag

            if ($LASTEXITCODE -ne 0) {
                throw "Failed to export $imageName"
            }

            Write-Host "  Exported: $tarPath" -ForegroundColor Green
        }

        # Export PostgreSQL image (digest-pinned for reproducibility)
        if ($IncludePostgres) {
            Write-Host "  Pulling and exporting PostgreSQL (digest-pinned)..." -ForegroundColor Gray
            docker pull $PostgresImage

            if ($LASTEXITCODE -ne 0) {
                throw "Failed to pull PostgreSQL image"
            }

            $postgresTar = Join-Path $bundlePath "docker-images/postgres-18.tar"
            docker save -o $postgresTar $PostgresImage
            Write-Host "  Exported: $postgresTar" -ForegroundColor Green
        }
    }
    else {
        Write-Host "`nSkipping Docker image export (--SkipImageExport specified)" -ForegroundColor Yellow
    }

    # Copy Docker Compose files
    Write-Host "`nCopying Docker Compose configuration..." -ForegroundColor Cyan

    # docker-compose.override.yml is deliberately absent: it holds development-only settings
    # (Development mode, the demo Keycloak, PostgreSQL published on 5432), and Docker Compose
    # applies it automatically to any command run without -f in the same directory.
    $composeFiles = @(
        "docker-compose.yml"
        "deploy/docker-compose.production.yml"
        ".env.example"
    )

    foreach ($file in $composeFiles) {
        $sourcePath = Join-Path $RepoRoot $file
        if (Test-Path $sourcePath) {
            Copy-Item $sourcePath -Destination "$bundlePath/compose/"
            Write-Host "  Copied: $file" -ForegroundColor Gray
        }
    }

    # Copy PowerShell module
    Write-Host "`nCopying PowerShell module..." -ForegroundColor Cyan
    $psModuleSrc = Join-Path $RepoRoot "src/JIM.PowerShell"
    $psModuleDst = Join-Path $bundlePath "powershell/JIM"

    if (Test-Path $psModuleSrc) {
        Copy-Item -Recurse $psModuleSrc $psModuleDst
        # Remove test files from the bundle
        $testsPath = Join-Path $psModuleDst "Tests"
        if (Test-Path $testsPath) {
            Remove-Item -Recurse -Force $testsPath
        }
        Write-Host "  Copied JIM PowerShell module" -ForegroundColor Gray
    }
    else {
        Write-Warning "PowerShell module not found at $psModuleSrc"
    }

    # Copy documentation
    Write-Host "`nCopying documentation..." -ForegroundColor Cyan

    $docFiles = @(
        @{ Source = "CHANGELOG.md"; Dest = "docs/CHANGELOG.md" }
        @{ Source = "README.md"; Dest = "docs/README.md" }
    )

    foreach ($doc in $docFiles) {
        $sourcePath = Join-Path $RepoRoot $doc.Source
        if (Test-Path $sourcePath) {
            Copy-Item $sourcePath -Destination "$bundlePath/$($doc.Dest)"
            Write-Host "  Copied: $($doc.Source)" -ForegroundColor Gray
        }
    }

    # Create installation guide
    $installGuide = @"
# JIM Air-Gapped Installation Guide

Version: $Version

## Prerequisites

- Docker Engine 24.0 or later
- Docker Compose v2.20 or later
- At least 4GB RAM available for containers
- 10GB disk space

## Installation Steps

### 1. Transfer the Release Bundle

Transfer `jim-release-$Version.tar.gz` to your target system using your
organisation's approved secure file transfer method.

### 2. Extract the Bundle

``````bash
tar -xzf jim-release-$Version.tar.gz
cd jim-release-$Version
``````

### 3. Verify Integrity (Recommended)

``````bash
sha256sum -c checksums.sha256
``````

All files should report "OK".

### 4. Load Docker Images

``````bash
docker load -i docker-images/jim-web.tar
docker load -i docker-images/jim-worker.tar
docker load -i docker-images/jim-scheduler.tar
docker load -i docker-images/postgres-18.tar
``````

### 5. Configure Environment

``````bash
cd compose
cp .env.example .env
``````

Edit `.env` with your configuration:
- Database credentials
- SSO/OIDC settings
- Logging preferences

### 6. Install JIM's HTTPS Certificate

JIM serves HTTPS, which browsers on other machines need in order to sign in. It
reads its certificate and private key, as PEM files, from tls/tls.crt and
tls/tls.key in the compose folder. The key must be unencrypted and belong to
UID 1654, the user JIM runs as. Run the commands in this step as root.

**Option A: your organisation's certificate** (recommended). Have it issued for
the DNS names users will reach JIM at, then:

``````bash
mkdir -p tls && chmod 700 tls
cp /path/to/jim.crt tls/tls.crt   # the certificate, followed by any intermediate CA certificates
cp /path/to/jim.key tls/tls.key   # its unencrypted private key
chown 1654:1654 tls/tls.key && chmod 400 tls/tls.key
``````

**Option B: a certificate authority (CA) of JIM's own.** Replace jim.example.com
and 192.0.2.10 with the names and addresses users will reach JIM at, in both
files. The CA's name constraints let it sign certificates for those names only.
If users reach JIM by name only, delete the IP entries and replace the IP
constraint with: excluded;IP:0.0.0.0/0.0.0.0,excluded;IP:::/::

``````bash
mkdir -p tls && chmod 700 tls && cd tls

cat > ca.cnf <<'EOF'
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
O = JIM
CN = JIM certificate authority for jim.example.com
[ext]
basicConstraints = critical,CA:TRUE,pathlen:0
keyUsage = critical,keyCertSign,cRLSign
subjectKeyIdentifier = hash
nameConstraints = permitted;DNS:jim.example.com,permitted;IP:192.0.2.10/255.255.255.255
EOF

cat > server.cnf <<'EOF'
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = serverAuth
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid:always
subjectAltName = DNS:jim.example.com,IP:192.0.2.10
EOF

# The CA, valid for ten years
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out ca.key
openssl req -new -x509 -config ca.cnf -key ca.key -sha256 -days 3650 -out ca.crt
chmod 400 ca.key

# JIM's certificate, valid for a year. To renew it, run these four commands
# again, then restart jim.web.
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out tls.key
openssl req -new -key tls.key -subj "/CN=jim.example.com" -out tls.csr
openssl x509 -req -in tls.csr -CA ca.crt -CAkey ca.key -set_serial "0x`$(openssl rand -hex 16)" -days 365 -sha256 -extfile server.cnf -out tls.crt
chown 1654:1654 tls.key && chmod 400 tls.key

cd ..
``````

Add tls/ca.crt to the trusted root certificate authorities of every machine
whose browser or tools use JIM (for example by Group Policy). Keep tls/ca.key
secret: anyone holding it can issue certificates for JIM's names.

### 7. Start JIM

With the bundled PostgreSQL container:

``````bash
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d
``````

With an external PostgreSQL server (set JIM_DB_HOSTNAME in .env), leave out --profile with-db.

Pass the same -f files and --profile to every later docker compose command (ps, logs, stop).

### 8. Verify Installation

JIM is ready when jim.web shows as healthy; its health check calls the
readiness endpoint:

``````bash
docker compose -f docker-compose.yml -f docker-compose.production.yml ps
``````

Then open https://jim.example.com:5200 (set JIM_WEB_PORT in .env to use another
port), and register https://jim.example.com:5200/signin-oidc as a redirect URI
at your identity provider.

## Installing the PowerShell Module

### Option A: Import Directly

``````powershell
Import-Module ./powershell/JIM/JIM.psd1
``````

### Option B: Install to Module Path

``````powershell
`$modulePath = `$env:PSModulePath.Split([IO.Path]::PathSeparator)[0]
Copy-Item -Recurse ./powershell/JIM "`$modulePath/JIM"
Import-Module JIM
``````

### Verify Module

``````powershell
Get-Module JIM
Get-Command -Module JIM
``````

## Connecting to JIM

The machine running the module must trust JIM's certificate (or, for Option B,
its CA).

``````powershell
# Connect using API key
Connect-JIM -Url "https://jim.example.com:5200" -ApiKey "your-api-key"

# Test connection
Test-JIMConnection

# List connected systems
Get-JIMConnectedSystem
``````

## Troubleshooting

Run these in the compose folder.

### Container Logs
``````bash
docker compose -f docker-compose.yml -f docker-compose.production.yml logs jim.web
docker compose -f docker-compose.yml -f docker-compose.production.yml logs jim.worker
docker compose -f docker-compose.yml -f docker-compose.production.yml logs jim.scheduler
``````

### Database Connection
``````bash
docker compose -f docker-compose.yml -f docker-compose.production.yml exec jim.database psql -U jim -d jim -c "SELECT 1"
``````

### Restart Services
``````bash
docker compose -f docker-compose.yml -f docker-compose.production.yml restart
``````

## Support

For issues and questions:
- GitHub: https://github.com/TetronIO/JIM/issues
- Documentation: https://docs.junctional.io
"@
    # LF line endings: the guide is read, and its commands pasted, on Linux hosts. This script is checked out
    # with CRLF (.gitattributes), so the here-string carries CRLF until converted, and a heredoc copied from
    # it would write carriage returns into the configuration files it creates.
    ($installGuide -replace "`r`n", "`n") + "`n" | Set-Content -NoNewline "$bundlePath/docs/INSTALL.md"
    Write-Host "  Created: INSTALL.md" -ForegroundColor Gray

    # Create README
    $readme = @"
JIM (Junctional Identity Manager) - Release $Version
=====================================================

This bundle contains everything needed for an air-gapped deployment of JIM.

Contents:
---------
- docker-images/  : Pre-built Docker images (tar format)
- compose/        : Docker Compose configuration files
- powershell/     : JIM PowerShell module
- docs/           : Documentation and changelog
- checksums.sha256: SHA256 checksums for integrity verification

Quick Start:
------------
1. Verify checksums: sha256sum -c checksums.sha256
2. Load images:      for f in docker-images/*.tar; do docker load -i "`$f"; done
3. Configure:        cp compose/.env.example compose/.env && edit compose/.env
4. Certificate:      put JIM's HTTPS certificate and key in compose/tls (see docs/INSTALL.md, step 6)
5. Start:            cd compose && docker compose -f docker-compose.yml -f docker-compose.production.yml up -d

For detailed instructions, see docs/INSTALL.md

License: See https://junctional.io/license
"@
    ($readme -replace "`r`n", "`n") + "`n" | Set-Content -NoNewline "$bundlePath/README.txt"
    Write-Host "  Created: README.txt" -ForegroundColor Gray

    # Generate checksums
    Write-Host "`nGenerating checksums..." -ForegroundColor Cyan
    Push-Location $bundlePath

    $checksumFile = "checksums.sha256"
    $filesToHash = Get-ChildItem -Recurse -File | Where-Object { $_.Name -ne $checksumFile }

    $checksums = @()
    foreach ($file in $filesToHash) {
        $relativePath = $file.FullName.Substring($bundlePath.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLower()
        $checksums += "$hash  $relativePath"
    }

    $checksums | Set-Content $checksumFile
    Write-Host "  Generated checksums for $($filesToHash.Count) files" -ForegroundColor Gray

    Pop-Location

    # Create tarball
    Write-Host "`nCreating release archive..." -ForegroundColor Cyan
    $tarballPath = Join-Path $OutputPath "$bundleName.tar.gz"

    Push-Location $OutputPath
    tar -czf "$bundleName.tar.gz" $bundleName
    Pop-Location

    if ($LASTEXITCODE -eq 0) {
        $tarballSize = (Get-Item $tarballPath).Length / 1MB
        Write-Host "  Created: $tarballPath ($([math]::Round($tarballSize, 2)) MB)" -ForegroundColor Green
    }
    else {
        Write-Warning "Failed to create tarball"
    }

    Write-Host "`nRelease bundle complete!" -ForegroundColor Green
    Write-Host "Bundle location: $bundlePath" -ForegroundColor Cyan
    Write-Host "Archive: $tarballPath" -ForegroundColor Cyan
}
finally {
    Pop-Location
}
