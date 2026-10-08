# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Builds a release bundle for air-gapped JIM deployments.

.DESCRIPTION
    Creates a self-contained release package containing:
    - Pre-built container images (exported as .tar files, which Docker and Podman both load)
    - Docker Compose configuration files
    - Podman pod files and Quadlet units
    - PowerShell module
    - Installation documentation
    - SHA256 checksums for integrity verification: checksums.sha256 in the bundle, listing each file by its path
      within it, and jim-release-X.Y.Z.tar.gz.sha256 beside the archive. Test-ReleaseBundle.ps1 checks both.

.PARAMETER Version
    The version number for the release (e.g., "0.2.0").
    If not specified, reads from the VERSION file.

.PARAMETER OutputPath
    The directory where the release bundle will be created.
    Defaults to ./release-output.

.PARAMETER SkipImageExport
    Skip exporting Docker images (useful for testing the bundle structure).

.PARAMETER SkipImageBuild
    Export JIM's images as they already are in Docker, tagged ghcr.io/tetronio/<image>:<Version>, rather
    than building them. CI builds them first, with its build cache; the script stops if one is missing.

.PARAMETER SkipArchive
    Leave the bundle as a folder, without also writing it into a .tar.gz archive.

.PARAMETER IncludePostgres
    Include the PostgreSQL image in the bundle. Defaults to true.

.EXAMPLE
    ./Build-ReleaseBundle.ps1 -Version "0.2.0"

    Builds a release bundle for version 0.2.0.

.EXAMPLE
    ./Build-ReleaseBundle.ps1 -Version "0.2.0" -SkipImageBuild -SkipArchive

    Bundles images already built and tagged ghcr.io/tetronio/jim-*:0.2.0, as a folder only.

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

    [switch]$SkipImageBuild,

    [switch]$SkipArchive,

    [bool]$IncludePostgres = $true
)

$ErrorActionPreference = 'Stop'

# Determine repository root
$RepoRoot = Split-Path -Parent $PSScriptRoot

# Read PostgreSQL image reference from docker-compose.yml (single source of truth).
# The digest-pinned image in docker-compose.yml is maintained by Dependabot.
$PostgresImage = & (Join-Path $PSScriptRoot 'Get-PostgresImageReference.ps1')
Write-Host "PostgreSQL image from docker-compose.yml: $PostgresImage" -ForegroundColor Gray
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
        "$bundlePath/podman"
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

            if ($SkipImageBuild) {
                docker image inspect $imageTag *> $null
                if ($LASTEXITCODE -ne 0) {
                    throw "The image $imageTag is not in Docker. Build it first, or leave out -SkipImageBuild."
                }
            }
            else {
                Write-Host "  Building $imageName..." -ForegroundColor Gray
                docker build -t $imageTag -f $image.Dockerfile $image.Context --build-arg VERSION=$Version

                if ($LASTEXITCODE -ne 0) {
                    throw "Failed to build $imageName"
                }
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

            # The compose and pod files pin PostgreSQL by its registry digest, which resolves on an air-gapped
            # host only if the archive carries the registry's own manifest. Docker's containerd image store saves
            # it; the classic store writes a manifest of its own, whose digest the pinned reference never matches,
            # so the bundled database would try to download its image and fail. Refuse to build that bundle.
            $postgresDigest = ($PostgresImage -split '@')[1]
            $archiveIndex = tar -xOf $postgresTar index.json 2>$null
            if ($LASTEXITCODE -ne 0 -or -not ($archiveIndex -match [regex]::Escape($postgresDigest))) {
                throw "The PostgreSQL archive does not carry the registry manifest $postgresDigest, so the pinned image would not resolve on an air-gapped host. Build the bundle with Docker's containerd image store (https://docs.docker.com/engine/storage/containerd/)."
            }

            # Docker's classic image store, on the installing host, drops that registry digest when it loads the
            # archive, so the pinned reference still would not resolve there. Record the image's ID (its config
            # digest, which that store keeps as the image's ID): the installer checks the loaded image against it
            # before running the image by its ID instead.
            $postgresIds = & (Join-Path $PSScriptRoot 'Get-ImageArchiveIds.ps1') -ArchivePath $postgresTar
            $postgresIdsPath = Join-Path $bundlePath "docker-images/postgres-18.image-ids"
            (($postgresIds -join "`n") + "`n") | Set-Content -NoNewline $postgresIdsPath
            Write-Host "  Exported: $postgresTar (image ID $($postgresIds -join ', '))" -ForegroundColor Green
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

    # The Podman files, with this release's image references filled in.
    Write-Host "`nRendering the Podman files..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Build-PodmanFiles.ps1') -Version $Version -OutputPath "$bundlePath/podman"

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
        # Leave out what is only for developing the module: its tests, and the notes for coding agents.
        foreach ($developmentOnly in 'Tests', 'CLAUDE.md') {
            $developmentPath = Join-Path $psModuleDst $developmentOnly
            if (Test-Path $developmentPath) {
                Remove-Item -Recurse -Force $developmentPath
            }
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

- A Linux server with 4 GB of RAM or more and 20 GB of free disk space, and
  either Docker Engine 24.0 or later with Docker Compose v2.24 or later, or
  Podman 4.4 or later with systemd (RHEL 9 and 10 include both)
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

1. Before transferring it, check the download: in the folder holding
   jim-release-$Version.tar.gz and jim-release-$Version.tar.gz.sha256, both
   from the release page, sha256sum -c jim-release-$Version.tar.gz.sha256
   should print OK. Transfer it to the server by your organisation's approved
   method, then extract it and check it arrived intact; every line should end
   in OK:

    ``````bash
    tar -xzf jim-release-$Version.tar.gz
    cd jim-release-$Version
    sha256sum -c checksums.sha256
    ``````

2. Run the installer as root:

    ``````bash
    sudo ./setup.sh
    ``````

   It uses Docker or Podman, whichever the server has. Where both are
   installed it asks which; name one with --runtime docker or --runtime podman.
   On Podman, JIM runs as root, as on Docker, and systemd starts it at boot;
   add --rootless to run it instead under an account named jim, which the
   installer creates.

   It installs JIM in /opt/jim and asks about:

   - the database: the bundled PostgreSQL, or your own server
   - your identity provider
   - the HTTPS port: 443 unless you choose another
   - the certificate: one it creates, with a certificate authority (CA) of its
     own, or your organisation's certificate and key
   - any reverse proxy or load balancer in front of JIM
   - on Podman, whether to open the port in firewalld, if it is running

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

On Docker, run Docker Compose commands in /opt/jim, naming both compose files,
and with --profile with-db if you use the bundled PostgreSQL:

``````bash
cd /opt/jim
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db ps
docker compose -f docker-compose.yml -f docker-compose.production.yml --profile with-db logs jim.web
``````

On Podman, systemd runs JIM as jim.service (and the bundled PostgreSQL as
jim-database.service):

``````bash
sudo systemctl status jim.service
sudo systemctl restart jim.service
sudo podman ps
sudo podman logs jim-web
``````

Installed with --rootless, JIM belongs to the jim account: its own systemd
manager runs JIM, and its own Podman holds JIM's containers.

``````bash
# systemctl for the jim account's systemd manager, through the account's own bus
jim-systemctl() { sudo -u jim XDG_RUNTIME_DIR=/run/user/`$(id -u jim) DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/`$(id -u jim)/bus systemctl --user "`$@"; }
# Podman as the jim account, from the root folder, which the account can read
jim-podman() { (cd / && sudo -u jim XDG_RUNTIME_DIR=/run/user/`$(id -u jim) podman "`$@"); }
jim-systemctl status jim.service
jim-podman ps
jim-podman logs jim-web
``````

## Installing Without the Installer

If your organisation's policy requires every step by hand, follow the steps
for your runtime.

### With Docker

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
for your own server, give its name and JIM's credentials there. The bundled
PostgreSQL's memory settings (JIM_DB_SHARED_BUFFERS and those beside it) suit a
4 GB host unless set; on a larger host, size them as the installer would (the
Configuration Reference, "Bundled PostgreSQL Memory", gives the rules).

For the bundled PostgreSQL on Docker's classic image store (docker info shows
Storage Driver: overlay2), Docker drops the registry digest the compose file
pins PostgreSQL by when it loads the image. Run the loaded image by its ID
instead, after checking it is the one this bundle records:

``````bash
image=`$(docker load -i docker-images/postgres-18.tar | sed -n 's/^Loaded image: //p')
id=`$(docker image inspect -f '{{.Id}}' "`$image")
grep -qxF "`$id" docker-images/postgres-18.image-ids && echo "JIM_DB_IMAGE=`$id" >> /opt/jim/.env
``````

Nothing is added if the IDs differ; then extract the bundle again and check it
with sha256sum -c checksums.sha256.

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

### With Podman

These steps install JIM rootful, as the installer does by default. Running
on Podman, on the documentation site, also gives the rootless steps. As root,
in the extracted bundle:

``````bash
for f in docker-images/*.tar; do podman load -i "`$f"; done
mkdir -p /opt/jim/tls /etc/containers/systemd && chmod 700 /opt/jim/tls
cp podman/jim.yaml podman/jim-database.yaml podman/jim-config.yaml /opt/jim/
cp podman/quadlet/jim.network podman/quadlet/jim.kube podman/quadlet/jim-database.kube /etc/containers/systemd/
``````

Leave out jim-database.kube if you use your own PostgreSQL server. Edit
/opt/jim/jim-config.yaml: fill in the identity provider settings, and for your
own PostgreSQL server set JIM_DB_HOSTNAME to its name. For the bundled
PostgreSQL on a host larger than 4 GB, size its memory as for Docker above. To
use a port other than 443, change PublishPort= in /etc/containers/systemd/jim.kube.

Store the database password and client secret in Podman, from a copy of
podman/jim-secrets.yaml with both filled in, then delete the copy:

``````bash
podman kube play jim-secrets.yaml && shred -u jim-secrets.yaml
``````

Put JIM's certificate and key in /opt/jim/tls as tls.crt and tls.key, as for
Docker above (Podman needs no chown), and store them in Podman as well:

``````bash
printf 'apiVersion: v1\nkind: Secret\nmetadata:\n  name: jim-tls\ndata:\n  tls.crt: %s\n  tls.key: %s\n' \
  "`$(base64 -w0 /opt/jim/tls/tls.crt)" "`$(base64 -w0 /opt/jim/tls/tls.key)" | podman kube play --replace -
``````

If firewalld is running, open JIM's port without reloading firewalld, and have
Podman restore its rules for JIM's network whenever firewalld reloads. If
NetworkManager is running, tell it to leave Podman's interfaces alone, or it can
unplug JIM's database from JIM's network. Then start JIM:

``````bash
firewall-cmd --add-service=https && firewall-cmd --permanent --add-service=https
systemctl enable --now netavark-firewalld-reload.service
printf '[keyfile]\nunmanaged-devices+=interface-name:veth*;interface-name:podman*\n' > /etc/NetworkManager/conf.d/90-jim-podman.conf
nmcli general reload conf
systemctl daemon-reload
systemctl start jim-database.service jim.service
``````

JIM is ready when podman healthcheck run jim-web succeeds.

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
- docker-images/  : Pre-built container images (tar format), for Docker or Podman
- compose/        : Docker Compose configuration files
- podman/         : Podman pod files, settings and secrets templates, and Quadlet units
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

    # Generate checksums, each file named by its path within the bundle, which is where sha256sum -c looks for it
    # when run there. $bundlePath may be relative (the release passes ./release-output) while a file's FullName is
    # absolute, so compare full paths: cutting the one by the other's length named every file under the build
    # runner's folders instead, and sha256sum -c failed on every line (#1942).
    Write-Host "`nGenerating checksums..." -ForegroundColor Cyan
    $bundleFullPath = (Resolve-Path $bundlePath).Path
    $checksumPath = Join-Path $bundleFullPath "checksums.sha256"
    $filesToHash = @(Get-ChildItem -Path $bundleFullPath -Recurse -File | Where-Object { $_.FullName -ne $checksumPath })

    $checksums = foreach ($file in $filesToHash) {
        $relativePath = [IO.Path]::GetRelativePath($bundleFullPath, $file.FullName).Replace('\', '/')
        $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLower()
        [pscustomobject]@{ Path = $relativePath; Line = "$hash  $relativePath" }
    }

    # LF line endings, which sha256sum needs: on a CRLF line it looks for a file whose name ends in a carriage return.
    (($checksums | Sort-Object Path | ForEach-Object Line) -join "`n") + "`n" | Set-Content -NoNewline $checksumPath
    Write-Host "  Generated checksums for $($filesToHash.Count) files" -ForegroundColor Gray

    Write-Host "`nRelease bundle complete!" -ForegroundColor Green
    Write-Host "Bundle location: $bundlePath" -ForegroundColor Cyan

    if (-not $SkipArchive) {
        Write-Host "`nCreating release archive..." -ForegroundColor Cyan
        $tarballPath = Join-Path $OutputPath "$bundleName.tar.gz"

        Push-Location $OutputPath
        try {
            tar -czf "$bundleName.tar.gz" $bundleName
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to create the archive $tarballPath"
            }

            # The archive's own checksum, published beside it, so that the download can be checked before it is
            # carried into an air-gapped site: sha256sum -c on this file, in the folder holding both.
            $archiveHash = (Get-FileHash -Path "$bundleName.tar.gz" -Algorithm SHA256).Hash.ToLower()
            "$archiveHash  $bundleName.tar.gz`n" | Set-Content -NoNewline "$bundleName.tar.gz.sha256"
        }
        finally {
            Pop-Location
        }

        $tarballSize = (Get-Item $tarballPath).Length / 1MB
        Write-Host "  Created: $tarballPath ($([math]::Round($tarballSize, 2)) MB), and its checksum, $bundleName.tar.gz.sha256" -ForegroundColor Green
        Write-Host "Archive: $tarballPath" -ForegroundColor Cyan
    }
}
finally {
    Pop-Location
}
