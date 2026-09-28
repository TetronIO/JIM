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

            # Save it under its name and tag, never the digest reference itself. Saved by digest reference, the
            # archive carries no image name, so docker load produces an anonymous image, and the compose file's
            # digest-pinned reference then finds nothing: the bundled database never starts on an air-gapped host.
            # Loaded by name, the image still has its digest, which is what the compose file's reference checks.
            $postgresTagged = $PostgresImage -replace '@sha256:[0-9a-f]+$', ''
            docker tag $PostgresImage $postgresTagged
            $postgresTar = Join-Path $bundlePath "docker-images/postgres-18.tar"
            docker save -o $postgresTar $postgresTagged
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to export the PostgreSQL image"
            }
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

    # The installer, which installs from this bundle when run inside it, and the version it installs.
    Copy-Item (Join-Path $RepoRoot "deploy/setup.sh") -Destination "$bundlePath/setup.sh"
    if (-not $IsWindows) {
        chmod 755 "$bundlePath/setup.sh"
    }
    "$Version`n" | Set-Content -NoNewline "$bundlePath/VERSION"
    Write-Host "  Copied: deploy/setup.sh, and wrote VERSION" -ForegroundColor Gray

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

This bundle installs JIM without an internet connection. Its installer, setup.sh,
uses the images and files in this bundle and downloads nothing.

## Before You Start

You need:

- A Linux server with Docker Engine 24.0 or later and Docker Compose v2.24 or
  later, 4 GB of RAM or more, and 20 GB of free disk space
- OpenSSL, which every mainstream Linux distribution installs by default
- The DNS name users will reach JIM at
- A client registration for JIM at your identity provider: its authority URL,
  client ID and secret, API scope, and the claim value of the first
  administrator. The SSO Setup Guide at https://docs.junctional.io describes
  each provider; read it from a connected machine.
- A PostgreSQL 18 server, unless you use the one in this bundle
- Your organisation's certificate and key for JIM's name, unless the installer
  creates them

## Install

1. Transfer jim-release-$Version.tar.gz to the server by your organisation's
   approved method, then extract it and check it arrived intact; every line
   should end in OK:

    ``````bash
    tar -xzf jim-release-$Version.tar.gz
    cd jim-release-$Version
    sha256sum -c checksums.sha256
    ``````

2. Run the installer as root:

    ``````bash
    sudo ./setup.sh
    ``````

   It installs JIM in /opt/jim and asks about:

   - the database: the bundled PostgreSQL, or your own server
   - your identity provider
   - the HTTPS port: 443 unless you choose another
   - the certificate: one it creates, with a certificate authority (CA) of its
     own, or your organisation's certificate and key
   - any reverse proxy or load balancer in front of JIM

   It then loads JIM's images, starts JIM, and waits until JIM is ready.

3. Do what the installer lists under Next steps:

   - At your identity provider, register JIM's two redirect URIs,
     https://jim.example.com/signin-oidc and
     https://jim.example.com/signout-callback-oidc, with your JIM name (and
     :port if you chose one other than 443).
   - If the installer created the certificate, add /opt/jim/tls/ca.crt to the
     trusted root certificate authorities of every machine whose browser or
     tools use JIM, for example by Group Policy. Until then, browsers warn about
     JIM's certificate.

Then open https://jim.example.com and sign in.

## Looking After JIM

The installation keeps a copy of the installer:

``````bash
# Renew a certificate the installer created, before it expires (it lasts a year)
sudo /opt/jim/setup.sh --renew-certificate

# Change the certificate's names, or move to your organisation's certificate
sudo /opt/jim/setup.sh --certificate
``````

Run Docker Compose commands in /opt/jim, naming both compose files, and with
--profile with-db if you use the bundled PostgreSQL:

``````bash
cd /opt/jim
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db ps
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db logs jim.web
``````

## Installing Without the Installer

If your organisation's policy requires every step by hand:

``````bash
# As root, in the extracted bundle
for f in docker-images/*.tar; do docker load -i "`$f"; done
mkdir -p /opt/jim/tls && chmod 700 /opt/jim/tls
cp compose/docker-compose.yml compose/docker-compose.production.yml /opt/jim/
cp compose/.env.example /opt/jim/.env && chmod 600 /opt/jim/.env
``````

Edit /opt/jim/.env: set DOCKER_REGISTRY=ghcr.io/tetronio/ and
JIM_VERSION=$Version, and the identity provider settings its comments
describe. For the bundled PostgreSQL, set JIM_DB_HOSTNAME=jim.database (the
template's localhost is for development) and choose a strong JIM_DB_PASSWORD;
for your own server, give its name and JIM's credentials there.

Put JIM's certificate and key in place. For your organisation's certificate:

``````bash
cp /path/to/jim.crt /opt/jim/tls/tls.crt   # the certificate, followed by any intermediate CA certificates
cp /path/to/jim.key /opt/jim/tls/tls.key   # its unencrypted private key
chown 1654:1654 /opt/jim/tls/tls.key && chmod 400 /opt/jim/tls/tls.key
``````

For a certificate of JIM's own instead, run only the installer's certificate
step: sudo JIM_INSTALL_DIR=/opt/jim ./setup.sh --certificate

Then start JIM. Leave out --profile with-db if you use your own PostgreSQL
server, and --pull never stops Docker from trying the internet:

``````bash
cd /opt/jim
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db up -d --pull never
``````

JIM is ready when docker compose ... ps shows jim.web as healthy.

## Installing the PowerShell Module

``````powershell
`$modulePath = `$env:PSModulePath.Split([IO.Path]::PathSeparator)[0]
Copy-Item -Recurse ./powershell/JIM "`$modulePath/JIM"
Import-Module JIM
``````

The machine running the module must trust JIM's certificate, or the
certificate authority the installer created.

``````powershell
Connect-JIM -Url "https://jim.example.com" -ApiKey "your-api-key"
Test-JIMConnection
``````

## Support

- Documentation: https://docs.junctional.io
- Issues: https://github.com/TetronIO/JIM/issues
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

This bundle installs JIM without an internet connection.

Contents:
---------
- setup.sh        : The installer; it installs from this bundle
- VERSION         : The JIM version this bundle installs
- docker-images/  : Pre-built Docker images (tar format)
- compose/        : Docker Compose configuration files
- powershell/     : JIM PowerShell module
- docs/           : Installation guide, readme and changelog
- checksums.sha256: SHA256 checksums for integrity verification

Quick Start:
------------
1. Verify:  sha256sum -c checksums.sha256
2. Install: sudo ./setup.sh

For details, including installing by hand, see docs/INSTALL.md.

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
