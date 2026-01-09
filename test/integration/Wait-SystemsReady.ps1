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
        Description = "Subatomic AD"
        HasHealthCheck = $true
        AdditionalCheck = $null
        PostReadySetup = {
            # Pre-built images (diegogslomp/samba-ad-dc) already have TLS configured at build time
            # Check both possible smb.conf locations for backwards compatibility
            $tlsConfigured = docker exec samba-ad-primary bash -c 'grep -q "tls enabled" /usr/local/samba/etc/smb.conf 2>/dev/null || grep -q "tls enabled" /etc/samba/smb.conf 2>/dev/null' 2>$null; $LASTEXITCODE -eq 0

            if ($tlsConfigured) {
                Write-Host "  ✓ Samba AD already configured with LDAPS (pre-built image)" -ForegroundColor Green
            }
            else {
                # Fallback: Configure TLS for base image without pre-configured TLS
                Write-Host "  Configuring LDAPS (TLS) on Samba AD..." -ForegroundColor Gray

                # Detect which Samba image is in use by checking paths
                # diegogslomp/samba-ad-dc uses /usr/local/samba/
                # nowsci/samba-domain uses /var/lib/samba/ and /etc/samba/
                $script = @'
# Detect Samba paths - diegogslomp/samba-ad-dc vs nowsci/samba-domain
if [ -d /usr/local/samba ]; then
    SAMBA_PRIVATE="/usr/local/samba/private"
    SAMBA_ETC="/usr/local/samba/etc"
else
    SAMBA_PRIVATE="/var/lib/samba/private"
    SAMBA_ETC="/etc/samba"
fi

# Generate TLS certificates if they don't exist
mkdir -p ${SAMBA_PRIVATE}/tls
if [ ! -f ${SAMBA_PRIVATE}/tls/cert.pem ]; then
    openssl req -x509 -nodes -days 3650 \
        -newkey rsa:2048 \
        -keyout ${SAMBA_PRIVATE}/tls/key.pem \
        -out ${SAMBA_PRIVATE}/tls/cert.pem \
        -subj "/CN=subatomic.local/O=JIM Integration Testing" 2>/dev/null
    cp ${SAMBA_PRIVATE}/tls/cert.pem ${SAMBA_PRIVATE}/tls/ca.pem
    chmod 600 ${SAMBA_PRIVATE}/tls/key.pem
fi

# Add TLS settings to smb.conf if not present
if ! grep -q "tls enabled" ${SAMBA_ETC}/smb.conf; then
    sed -i "/^\[global\]/a\\
tls enabled = yes\\
tls keyfile = ${SAMBA_PRIVATE}/tls/key.pem\\
tls certfile = ${SAMBA_PRIVATE}/tls/cert.pem\\
tls cafile = ${SAMBA_PRIVATE}/tls/ca.pem" ${SAMBA_ETC}/smb.conf
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
            # Use samba-tool from symlinked path (works for both image types)
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

$systemIndex = 0
foreach ($system in $systemsToCheck) {
    $systemIndex++
    Write-Host "`n[$systemIndex/$($systemsToCheck.Count)] Waiting for $($system.Description)..." -ForegroundColor Yellow

    # Wait for container to be running with progress bar
    $runningOp = Start-TimedOperation -Name "Container starting" -TotalSteps ($TimeoutSeconds / 5)
    $running = $false
    $attempts = 0
    $maxAttempts = $TimeoutSeconds / 5

    while ($attempts -lt $maxAttempts -and -not $running) {
        $attempts++
        Update-OperationProgress -Operation $runningOp -CurrentStep $attempts -Status "$($system.Name) starting..."
        $running = Test-ContainerRunning -ContainerName $system.Name
        if (-not $running) {
            Start-Sleep -Seconds 5
        }
    }

    if (-not $running) {
        Complete-TimedOperation -Operation $runningOp -Success $false -Message "Container $($system.Name) not running"
        exit 1
    }
    Complete-TimedOperation -Operation $runningOp -Success $true -Message "Container running"

    # Wait for health check if applicable with progress bar
    if ($system.HasHealthCheck) {
        $healthOp = Start-TimedOperation -Name "Health check" -TotalSteps ($TimeoutSeconds / 10)
        $healthy = $false
        $attempts = 0
        $maxAttempts = $TimeoutSeconds / 10

        while ($attempts -lt $maxAttempts -and -not $healthy) {
            $attempts++
            Update-OperationProgress -Operation $healthOp -CurrentStep $attempts -Status "Waiting for healthy status..."
            $healthy = Test-ContainerHealthy -ContainerName $system.Name
            if (-not $healthy) {
                Start-Sleep -Seconds 10
            }
        }

        if (-not $healthy) {
            Complete-TimedOperation -Operation $healthOp -Success $false -Message "Container $($system.Name) not healthy"

            # Show container logs for debugging
            Write-Host "`nContainer logs:" -ForegroundColor Yellow
            docker logs --tail 50 $system.Name
            exit 1
        }
        Complete-TimedOperation -Operation $healthOp -Success $true -Message "Health check passed"
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

    Write-Host "  ✓ $($system.Description) is ready" -ForegroundColor Green

    # Run post-ready setup if defined (e.g., configure TLS)
    if ($null -ne $system.PostReadySetup) {
        try {
            & $system.PostReadySetup
        }
        catch {
            Write-Host "  ⚠ Post-ready setup failed for $($system.Name): $_" -ForegroundColor Yellow
            # Don't fail - post-ready setup is optional configuration
        }
    }
}

$elapsed = (Get-Date) - $startTime
Write-Host "`n✓ All systems ready after $($elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor Green
