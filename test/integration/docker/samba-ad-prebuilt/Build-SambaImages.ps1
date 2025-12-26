<#
.SYNOPSIS
    Build pre-initialised Samba AD Docker images for integration testing

.DESCRIPTION
    Builds Docker images with Samba AD domain provisioning already complete.
    This reduces container startup time from ~5 minutes to ~30 seconds.

    ARCHITECTURE SUPPORT:
    Uses diegogslomp/samba-ad-dc as the base image, which provides native
    multi-architecture support for both AMD64 (x86_64) and ARM64 (Apple Silicon).
    Docker will automatically pull the correct architecture for your platform,
    eliminating the need for Rosetta emulation on Apple Silicon Macs.

    The build process:
    1. Starts a container from the base image in privileged mode
    2. Waits for domain provisioning to complete (indicated by healthy status)
    3. Runs post-provisioning setup (TLS, test OUs)
    4. Commits the container as a new image

    Images built:
    - ghcr.io/tetronio/jim-samba-ad:primary   (TESTDOMAIN.LOCAL)
    - ghcr.io/tetronio/jim-samba-ad:source    (SOURCEDOMAIN.LOCAL)
    - ghcr.io/tetronio/jim-samba-ad:target    (TARGETDOMAIN.LOCAL)

.PARAMETER Images
    Which images to build (Primary, Source, Target, All)

.PARAMETER Push
    Push images to GitHub Container Registry after building

.PARAMETER Registry
    Container registry to use (default: ghcr.io/tetronio)

.PARAMETER TimeoutSeconds
    Maximum time to wait for provisioning (default: 600)

.EXAMPLE
    ./Build-SambaImages.ps1 -Images All

.EXAMPLE
    ./Build-SambaImages.ps1 -Images Primary -Push
#>

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("Primary", "Source", "Target", "All")]
    [string]$Images = "All",

    [Parameter(Mandatory = $false)]
    [switch]$Push,

    [Parameter(Mandatory = $false)]
    [string]$Registry = "ghcr.io/tetronio",

    [Parameter(Mandatory = $false)]
    [int]$TimeoutSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot

# Image definitions
$imageDefinitions = @{
    Primary = @{
        Domain    = "TESTDOMAIN.LOCAL"
        Tag       = "primary"
        Password  = "Test@123!"
        Container = "samba-build-primary"
    }
    Source  = @{
        Domain    = "SOURCEDOMAIN.LOCAL"
        Tag       = "source"
        Password  = "Test@123!"
        Container = "samba-build-source"
    }
    Target  = @{
        Domain    = "TARGETDOMAIN.LOCAL"
        Tag       = "target"
        Password  = "Test@123!"
        Container = "samba-build-target"
    }
}

# Determine which images to build
$imagesToBuild = if ($Images -eq "All") {
    @("Primary", "Source", "Target")
}
else {
    @($Images)
}

# Base image for Samba AD - supports both AMD64 and ARM64
$baseImage = "diegogslomp/samba-ad-dc:latest"

function Wait-SambaReady {
    param(
        [string]$ContainerName,
        [int]$TimeoutSeconds
    )

    $startTime = Get-Date
    $lastProgressUpdate = Get-Date
    $provisioningComplete = $false

    while ($true) {
        $elapsed = (Get-Date) - $startTime
        $elapsedSec = [int]$elapsed.TotalSeconds

        if ($elapsedSec -ge $TimeoutSeconds) {
            Write-Host "  ERROR: Timeout ($TimeoutSeconds s) waiting for Samba to be ready" -ForegroundColor Red
            return $false
        }

        # Check if provisioning completed by looking for marker file
        # diegogslomp/samba-ad-dc uses /usr/local/samba/etc/smb.conf
        if (-not $provisioningComplete) {
            $markerCheck = docker exec $ContainerName test -f /usr/local/samba/etc/smb.conf 2>&1
            if ($LASTEXITCODE -eq 0) {
                $provisioningComplete = $true
                Write-Host "  Provisioning complete ($elapsedSec s)" -ForegroundColor Green
            }
        }

        # Check if smbclient can connect (Samba is ready for connections)
        # diegogslomp/samba-ad-dc has samba binaries in /usr/local/samba/bin/
        if ($provisioningComplete) {
            $smbCheck = docker exec $ContainerName /usr/local/samba/bin/smbclient -L localhost -U% -N 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  Samba accepting connections ($elapsedSec s)" -ForegroundColor Green
                return $true
            }
        }

        # Progress update every 30 seconds
        if (((Get-Date) - $lastProgressUpdate).TotalSeconds -ge 30) {
            if ($provisioningComplete) {
                Write-Host "  Waiting for Samba to start ($elapsedSec s)..." -ForegroundColor DarkGray
            }
            else {
                Write-Host "  Waiting for provisioning ($elapsedSec s)..." -ForegroundColor DarkGray
            }
            $lastProgressUpdate = Get-Date
        }

        Start-Sleep -Seconds 5
    }
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "Building Pre-initialised Samba AD Images" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "This process provisions Samba AD domains and commits them as Docker images." -ForegroundColor Gray
Write-Host "Each image takes ~3-5 minutes to build but only needs to be built once." -ForegroundColor Gray
Write-Host ""

foreach ($imageName in $imagesToBuild) {
    $config = $imageDefinitions[$imageName]
    $fullTag = "$Registry/jim-samba-ad:$($config.Tag)"
    $containerName = $config.Container

    Write-Host "=============================================" -ForegroundColor Yellow
    Write-Host "Building: $fullTag" -ForegroundColor Yellow
    Write-Host "  Domain: $($config.Domain)" -ForegroundColor Gray
    Write-Host "=============================================" -ForegroundColor Yellow
    Write-Host ""

    $startTime = Get-Date

    # Clean up any existing container
    Write-Host "Step 1: Cleaning up any existing build container..." -ForegroundColor Cyan
    docker rm -f $containerName 2>$null | Out-Null

    # Start the container in privileged mode (required for Samba provisioning)
    # diegogslomp/samba-ad-dc environment variables:
    #   REALM = full domain (e.g., TESTDOMAIN.LOCAL)
    #   DOMAIN = short domain (e.g., TESTDOMAIN)
    #   ADMIN_PASS = administrator password
    #   DNS_FORWARDER = DNS forwarder IP
    $shortDomain = $config.Domain.Split('.')[0]
    Write-Host "Step 2: Starting container for provisioning..." -ForegroundColor Cyan
    Write-Host "  Base image: $baseImage" -ForegroundColor DarkGray
    Write-Host "  Realm: $($config.Domain)" -ForegroundColor DarkGray
    Write-Host "  Short domain: $shortDomain" -ForegroundColor DarkGray
    docker run -d `
        --name $containerName `
        --privileged `
        -e "REALM=$($config.Domain)" `
        -e "DOMAIN=$shortDomain" `
        -e "ADMIN_PASS=$($config.Password)" `
        -e "DNS_FORWARDER=8.8.8.8" `
        $baseImage

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to start container" -ForegroundColor Red
        exit 1
    }

    # Wait for the container to become ready (provisioning complete + Samba running)
    Write-Host "Step 3: Waiting for domain provisioning (this takes ~3-5 minutes)..." -ForegroundColor Cyan
    $ready = Wait-SambaReady -ContainerName $containerName -TimeoutSeconds $TimeoutSeconds

    if (-not $ready) {
        Write-Host "ERROR: Samba failed to become ready" -ForegroundColor Red
        Write-Host "Container logs:" -ForegroundColor Yellow
        docker logs --tail 100 $containerName
        docker rm -f $containerName | Out-Null
        exit 1
    }

    Write-Host "  Domain provisioned successfully" -ForegroundColor Green

    # Copy and run post-provision script
    Write-Host "Step 4: Running post-provisioning setup (TLS, OUs)..." -ForegroundColor Cyan
    docker cp "$scriptDir/post-provision.sh" "${containerName}:/post-provision.sh"
    docker exec $containerName chmod +x /post-provision.sh
    docker exec $containerName /post-provision.sh

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Post-provisioning failed" -ForegroundColor Red
        docker rm -f $containerName | Out-Null
        exit 1
    }

    # The base image declares /usr/local/samba/{etc,private,var} as volumes.
    # Docker commit/export don't capture volume data, so we need to copy the
    # provisioned data to non-volume locations, then move it back at runtime.
    Write-Host "Step 5: Copying provisioned data from volumes..." -ForegroundColor Cyan

    # Copy volume data to backup locations (these will be captured by commit)
    docker exec $containerName bash -c "cp -a /usr/local/samba/etc /usr/local/samba/etc.provisioned"
    docker exec $containerName bash -c "cp -a /usr/local/samba/private /usr/local/samba/private.provisioned"
    docker exec $containerName bash -c "cp -a /usr/local/samba/var /usr/local/samba/var.provisioned"

    # Copy the startup script that restores volume data and starts samba
    docker cp "$scriptDir/start-samba.sh" "${containerName}:/start-samba.sh"
    docker exec $containerName chmod +x /start-samba.sh

    # Stop the container cleanly before committing
    Write-Host "Step 6: Stopping container for commit..." -ForegroundColor Cyan
    docker stop $containerName | Out-Null

    # Commit the container as a new image
    Write-Host "Step 7: Committing container as image..." -ForegroundColor Cyan
    docker commit `
        --change "ENV REALM=$($config.Domain)" `
        --change "ENV DOMAIN=$shortDomain" `
        --change "ENV ADMIN_PASS=$($config.Password)" `
        --change "ENV DNS_FORWARDER=8.8.8.8" `
        --change "ENV DOMAINPASS=$($config.Password)" `
        --change "ENV NOCOMPLEXITY=true" `
        --change 'HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 CMD /usr/local/samba/bin/smbclient -L localhost -U% -N || exit 1' `
        --change 'CMD ["/start-samba.sh"]' `
        $containerName `
        $fullTag

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to commit image" -ForegroundColor Red
        docker rm -f $containerName | Out-Null
        exit 1
    }

    # Clean up build container
    Write-Host "Step 8: Cleaning up build container..." -ForegroundColor Cyan
    docker rm -f $containerName | Out-Null

    $elapsed = (Get-Date) - $startTime
    Write-Host ""
    Write-Host "Image built: $fullTag" -ForegroundColor Green
    Write-Host "Build time: $($elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Green
    Write-Host ""

    # Push if requested
    if ($Push) {
        Write-Host "Pushing $fullTag..." -ForegroundColor Cyan
        docker push $fullTag

        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: Failed to push $fullTag" -ForegroundColor Red
            exit 1
        }
        Write-Host "  Pushed successfully" -ForegroundColor Green
        Write-Host ""
    }
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "Build Complete" -ForegroundColor Green
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Images built:" -ForegroundColor Gray
foreach ($imageName in $imagesToBuild) {
    $config = $imageDefinitions[$imageName]
    Write-Host "  - $Registry/jim-samba-ad:$($config.Tag)" -ForegroundColor Gray
}
Write-Host ""

if (-not $Push) {
    Write-Host "To push images to the registry:" -ForegroundColor Yellow
    Write-Host "  ./Build-SambaImages.ps1 -Images All -Push" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Or push individually:" -ForegroundColor Yellow
    foreach ($imageName in $imagesToBuild) {
        $config = $imageDefinitions[$imageName]
        Write-Host "  docker push $Registry/jim-samba-ad:$($config.Tag)" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "To use these images with integration tests:" -ForegroundColor Yellow
Write-Host "  docker compose -f docker-compose.integration-tests.yml up -d" -ForegroundColor Gray
