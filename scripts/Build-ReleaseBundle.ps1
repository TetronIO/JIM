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
        @{ Name = "jim-web"; Dockerfile = "JIM.Web/Dockerfile"; Context = "." }
        @{ Name = "jim-worker"; Dockerfile = "JIM.Worker/Dockerfile"; Context = "." }
        @{ Name = "jim-scheduler"; Dockerfile = "JIM.Scheduler/Dockerfile"; Context = "." }
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

        # Export PostgreSQL image
        if ($IncludePostgres) {
            Write-Host "  Pulling and exporting postgres:18..." -ForegroundColor Gray
            docker pull postgres:18
            $postgresTar = Join-Path $bundlePath "docker-images/postgres-18.tar"
            docker save -o $postgresTar postgres:18
            Write-Host "  Exported: $postgresTar" -ForegroundColor Green
        }
    }
    else {
        Write-Host "`nSkipping Docker image export (--SkipImageExport specified)" -ForegroundColor Yellow
    }

    # Copy Docker Compose files
    Write-Host "`nCopying Docker Compose configuration..." -ForegroundColor Cyan

    $composeFiles = @(
        "docker-compose.yml"
        "docker-compose.override.codespaces.yml"
        ".env.example"
    )

    foreach ($file in $composeFiles) {
        $sourcePath = Join-Path $RepoRoot $file
        if (Test-Path $sourcePath) {
            Copy-Item $sourcePath -Destination "$bundlePath/compose/"
            Write-Host "  Copied: $file" -ForegroundColor Gray
        }
    }

    # Create production compose override
    $productionCompose = @"
# Production override for JIM
# Use with: docker compose -f docker-compose.yml -f docker-compose.production.yml up -d

services:
  jim.web:
    image: ghcr.io/tetronio/jim-web:$Version
    restart: unless-stopped
    build: !reset null

  jim.worker:
    image: ghcr.io/tetronio/jim-worker:$Version
    restart: unless-stopped
    build: !reset null

  jim.scheduler:
    image: ghcr.io/tetronio/jim-scheduler:$Version
    restart: unless-stopped
    build: !reset null

  jim.db:
    restart: unless-stopped
"@
    $productionCompose | Set-Content "$bundlePath/compose/docker-compose.production.yml"
    Write-Host "  Created: docker-compose.production.yml" -ForegroundColor Gray

    # Copy PowerShell module
    Write-Host "`nCopying PowerShell module..." -ForegroundColor Cyan
    $psModuleSrc = Join-Path $RepoRoot "JIM.PowerShell/JIM"
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
- SSO/OIDC settings (if applicable)
- Logging preferences

### 6. Start JIM

``````bash
docker compose -f docker-compose.yml -f docker-compose.production.yml up -d
``````

### 7. Verify Installation

Access JIM at http://localhost:5200 (or your configured port).

Run the health check:
``````bash
curl http://localhost:5200/health
``````

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

``````powershell
# Connect using API key
Connect-JIM -Server "http://localhost:5200" -ApiKey "your-api-key"

# Test connection
Test-JIMConnection

# List connected systems
Get-JIMConnectedSystem
``````

## Troubleshooting

### Container Logs
``````bash
docker compose logs jim.web
docker compose logs jim.worker
docker compose logs jim.scheduler
``````

### Database Connection
``````bash
docker compose exec jim.db psql -U jim -d jim -c "SELECT 1"
``````

### Restart Services
``````bash
docker compose restart
``````

## Support

For issues and questions:
- GitHub: https://github.com/TetronIO/JIM/issues
- Documentation: https://github.com/TetronIO/JIM/wiki
"@
    $installGuide | Set-Content "$bundlePath/docs/INSTALL.md"
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
2. Load images:      docker load -i docker-images/*.tar
3. Configure:        cp compose/.env.example compose/.env && edit compose/.env
4. Start:            cd compose && docker compose -f docker-compose.yml -f docker-compose.production.yml up -d

For detailed instructions, see docs/INSTALL.md

License: See https://tetron.io/jim/#licensing
"@
    $readme | Set-Content "$bundlePath/README.txt"
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
