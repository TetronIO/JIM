<#
.SYNOPSIS
    Wait for integration test systems to be ready

.DESCRIPTION
    Polls container health checks and service endpoints to ensure all
    required systems are ready before running tests

.PARAMETER Phase
    Test phase (1 or 2)

.PARAMETER TimeoutSeconds
    Maximum time to wait for systems to be ready (default: 600)

.EXAMPLE
    ./Wait-SystemsReady.ps1 -Phase 1
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet(1, 2)]
    [int]$Phase = 1,

    [Parameter(Mandatory=$false)]
    [int]$TimeoutSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"
. "$PSScriptRoot/utils/LDAP-Helpers.ps1"

Write-Host "Waiting for integration test systems (Phase $Phase) to be ready..." -ForegroundColor Cyan
Write-Host "Timeout: ${TimeoutSeconds}s" -ForegroundColor Gray

$startTime = Get-Date

function Test-ContainerHealthy {
    param([string]$ContainerName)

    try {
        $health = docker inspect --format='{{.State.Health.Status}}' $ContainerName 2>$null
        return $health -eq "healthy"
    }
    catch {
        return $false
    }
}

function Test-ContainerRunning {
    param([string]$ContainerName)

    try {
        $status = docker inspect --format='{{.State.Status}}' $ContainerName 2>$null
        return $status -eq "running"
    }
    catch {
        return $false
    }
}

# Phase 1 systems
$phase1Systems = @(
    @{
        Name = "samba-ad-primary"
        Description = "Samba AD Primary"
        HasHealthCheck = $true
        AdditionalCheck = $null
        PostReadySetup = {
            # Pre-built images already have TLS configured at build time
            # Only need to configure TLS if using the standard nowsci/samba-domain image
            $tlsConfigured = docker exec samba-ad-primary grep -q "tls enabled" /etc/samba/smb.conf 2>$null; $LASTEXITCODE -eq 0

            if ($tlsConfigured) {
                Write-Host "  ✓ Samba AD already configured with LDAPS (pre-built image)" -ForegroundColor Green
            }
            else {
                # Fallback: Configure TLS for standard nowsci/samba-domain image
                Write-Host "  Configuring LDAPS (TLS) on Samba AD (standard image)..." -ForegroundColor Gray

                # Add TLS settings to [global] section of smb.conf
                $script = @'
# Generate TLS certificates if they don't exist
mkdir -p /var/lib/samba/private/tls
if [ ! -f /var/lib/samba/private/tls/cert.pem ]; then
    openssl req -x509 -nodes -days 3650 \
        -newkey rsa:2048 \
        -keyout /var/lib/samba/private/tls/key.pem \
        -out /var/lib/samba/private/tls/cert.pem \
        -subj "/CN=testdomain.local/O=JIM Integration Testing" 2>/dev/null
    cp /var/lib/samba/private/tls/cert.pem /var/lib/samba/private/tls/ca.pem
    chmod 600 /var/lib/samba/private/tls/key.pem
fi

# Add TLS settings to smb.conf if not present
if ! grep -q "tls enabled" /etc/samba/smb.conf; then
    sed -i '/^\[global\]/a\
tls enabled = yes\
tls keyfile = /var/lib/samba/private/tls/key.pem\
tls certfile = /var/lib/samba/private/tls/cert.pem\
tls cafile = /var/lib/samba/private/tls/ca.pem' /etc/samba/smb.conf
    cp /etc/samba/smb.conf /etc/samba/external/smb.conf
    echo "TLS configuration added to smb.conf"
fi
'@
                docker exec samba-ad-primary bash -c $script

                # Restart Samba to apply TLS config
                Write-Host "  Restarting Samba to enable LDAPS..." -ForegroundColor Gray
                docker restart samba-ad-primary

                # Wait for container to be healthy again
                Start-Sleep -Seconds 10
                $retries = 0
                while ($retries -lt 12) {
                    $health = docker inspect --format='{{.State.Health.Status}}' samba-ad-primary 2>$null
                    if ($health -eq "healthy") { break }
                    Start-Sleep -Seconds 10
                    $retries++
                }

                if ($retries -ge 12) {
                    Write-Host "  ⚠ Samba AD may not be fully ready after TLS configuration" -ForegroundColor Yellow
                }

                Write-Host "  ✓ Samba AD configured with LDAPS support" -ForegroundColor Green
            }

            # Ensure Administrator password is set correctly (idempotent)
            docker exec samba-ad-primary samba-tool user setpassword Administrator --newpassword="Test@123!" 2>$null
        }
    }
)

# Phase 2 adds these systems
$phase2Systems = @(
    @{
        Name = "sqlserver-hris-a"
        Description = "SQL Server HRIS A"
        HasHealthCheck = $true
        AdditionalCheck = $null
    },
    @{
        Name = "postgres-target"
        Description = "PostgreSQL Target"
        HasHealthCheck = $true
        AdditionalCheck = $null
    }
)

# Determine which systems to check
$systemsToCheck = $phase1Systems
if ($Phase -eq 2) {
    $systemsToCheck += $phase2Systems
}

Write-Host "`nChecking $($systemsToCheck.Count) systems..." -ForegroundColor Gray

foreach ($system in $systemsToCheck) {
    Write-Host "`nWaiting for $($system.Description)..." -ForegroundColor Yellow

    # Wait for container to be running
    $running = Wait-ForCondition `
        -Condition { Test-ContainerRunning -ContainerName $system.Name } `
        -TimeoutSeconds $TimeoutSeconds `
        -IntervalSeconds 5 `
        -Description "$($system.Name) container running"

    if (-not $running) {
        Write-Host "✗ Container $($system.Name) not running" -ForegroundColor Red
        exit 1
    }

    # Wait for health check if applicable
    if ($system.HasHealthCheck) {
        $healthy = Wait-ForCondition `
            -Condition { Test-ContainerHealthy -ContainerName $system.Name } `
            -TimeoutSeconds $TimeoutSeconds `
            -IntervalSeconds 10 `
            -Description "$($system.Name) health check"

        if (-not $healthy) {
            Write-Host "✗ Container $($system.Name) not healthy" -ForegroundColor Red

            # Show container logs for debugging
            Write-Host "`nContainer logs:" -ForegroundColor Yellow
            docker logs --tail 50 $system.Name
            exit 1
        }
    }

    # Run additional checks if defined
    if ($null -ne $system.AdditionalCheck) {
        $additionalOk = Wait-ForCondition `
            -Condition $system.AdditionalCheck `
            -TimeoutSeconds 60 `
            -IntervalSeconds 5 `
            -Description "$($system.Name) additional check"

        if (-not $additionalOk) {
            Write-Host "✗ Additional check failed for $($system.Name)" -ForegroundColor Red
            exit 1
        }
    }

    Write-Host "✓ $($system.Description) is ready" -ForegroundColor Green

    # Run post-ready setup if defined (e.g., configure TLS)
    if ($null -ne $system.PostReadySetup) {
        try {
            & $system.PostReadySetup
        }
        catch {
            Write-Host "⚠ Post-ready setup failed for $($system.Name): $_" -ForegroundColor Yellow
            # Don't fail - post-ready setup is optional configuration
        }
    }
}

$elapsed = (Get-Date) - $startTime
Write-Host "`n✓ All systems ready after $($elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Green
