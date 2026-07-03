# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Start (or stop) the light JIM stack in a Claude Code cloud sandbox for runtime verification.

.DESCRIPTION
    Brings up the minimum topology an agent needs to visually verify web changes and
    execute PowerShell module changes against a live instance:

      - jim.database and jim.keycloak as containers (docker compose)
      - JIM.Web and JIM.Worker running natively via dotnet run (the Worker applies
        database migrations, so it must run at least once against a fresh database)
      - A localhost:8181 -> localhost:8180 TCP bridge so the browser-facing Keycloak
        URL matches the bundled realm configuration (socat if available, otherwise
        a Python forwarder)

    Full Docker image builds (jim-build) are deliberately avoided: the sandbox egress
    proxy intercepts TLS, so dotnet restore inside Docker build stages fails without
    certificate injection. Native builds restore through the proxy without issue.

    See engineering/SANDBOX_RUNTIME_VERIFICATION.md for the verification workflow.

.PARAMETER Down
    Stop the native processes and containers instead of starting them.

.PARAMETER IncludeScheduler
    Also start JIM.Scheduler natively (not needed for most verification tasks).

.EXAMPLE
    pwsh ./scripts/Start-SandboxStack.ps1

.EXAMPLE
    pwsh ./scripts/Start-SandboxStack.ps1 -Down
#>
[CmdletBinding()]
param(
    [switch]$Down,
    [switch]$IncludeScheduler
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$logDir = Join-Path ([System.IO.Path]::GetTempPath()) 'jim-sandbox-logs'
$composeArgs = @('-f', 'docker-compose.yml', '-f', 'docker-compose.override.yml', '--profile', 'with-db')

function Write-Info([string]$Message) { Write-Host "[sandbox-stack] $Message" }

if ($Down) {
    Write-Info 'Stopping native JIM processes...'
    Get-Process -Name 'JIM.Web', 'JIM.Worker', 'JIM.Scheduler' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name 'python3' -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*8181*' } | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Info 'Stopping containers...'
    docker compose @composeArgs down
    Write-Info 'Stack stopped.'
    return
}

# --- Preconditions ----------------------------------------------------------
$dotnet = Join-Path $HOME '.dotnet/dotnet'
if (-not (Test-Path $dotnet)) {
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCmd) { $dotnet = $dotnetCmd.Source }
    else { throw '.NET SDK not found. The SessionStart hook (.claude/hooks/session-start.sh) should have installed it.' }
}
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker daemon is not running. The SessionStart hook should have started it; see /tmp/dockerd.log.' }
if (-not (Test-Path '.env')) {
    (Get-Content '.env.example') -replace 'your_secure_password_here', 'password' | Set-Content '.env'
    Write-Info 'Created .env from .env.example.'
}
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

# --- Containers -------------------------------------------------------------
Write-Info 'Starting database and Keycloak containers...'
docker compose @composeArgs up -d jim.database jim.keycloak
foreach ($attempt in 1..60) {
    $db = docker inspect --format '{{.State.Health.Status}}' jim.database 2>$null
    $kc = docker inspect --format '{{.State.Health.Status}}' jim.keycloak 2>$null
    if ($db -eq 'healthy' -and $kc -eq 'healthy') { break }
    Start-Sleep -Seconds 3
}
if ($db -ne 'healthy' -or $kc -ne 'healthy') { throw "Containers unhealthy after wait: database=$db keycloak=$kc" }
Write-Info 'Database and Keycloak are healthy.'

# --- Keycloak 8181 bridge ----------------------------------------------------
$bridgeUp = $false
try {
    $probe = [System.Net.Sockets.TcpClient]::new()
    $probe.Connect('127.0.0.1', 8181); $probe.Close(); $bridgeUp = $true
} catch { }
if (-not $bridgeUp) {
    if (Get-Command socat -ErrorAction SilentlyContinue) {
        Start-Process socat -ArgumentList 'TCP-LISTEN:8181,fork,reuseaddr,bind=0.0.0.0', 'TCP:127.0.0.1:8180' -RedirectStandardOutput "$logDir/bridge.log" -RedirectStandardError "$logDir/bridge.err"
    }
    else {
        $forwarder = @'
import socket, threading
def pipe(a, b):
    try:
        while True:
            d = a.recv(65536)
            if not d: break
            b.sendall(d)
    except OSError: pass
    finally:
        try: a.close(); b.close()
        except OSError: pass
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("0.0.0.0", 8181)); s.listen(50)
while True:
    c, _ = s.accept()
    u = socket.create_connection(("127.0.0.1", 8180))
    threading.Thread(target=pipe, args=(c, u), daemon=True).start()
    threading.Thread(target=pipe, args=(u, c), daemon=True).start()
'@
        $forwarderPath = Join-Path $logDir 'kc-bridge.py'
        Set-Content -Path $forwarderPath -Value $forwarder
        Start-Process python3 -ArgumentList $forwarderPath -RedirectStandardOutput "$logDir/bridge.log" -RedirectStandardError "$logDir/bridge.err"
    }
    Write-Info 'Started Keycloak bridge on localhost:8181.'
}

# --- Native services ---------------------------------------------------------
# Load .env into this process so child dotnet processes inherit it.
Get-Content '.env' | Where-Object { $_ -match '^\s*[^#\s]' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    [Environment]::SetEnvironmentVariable($name.Trim(), $value.Trim().Trim('"'))
}
[Environment]::SetEnvironmentVariable('JIM_DB_HOSTNAME', 'localhost')
[Environment]::SetEnvironmentVariable('JIM_LOG_PATH', $logDir)
[Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Development')

Write-Info 'Building solution...'
& $dotnet build JIM.sln --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed; fix the build before verifying at runtime.' }

Write-Info 'Starting JIM.Worker (applies migrations) and JIM.Web...'
Start-Process $dotnet -ArgumentList 'run', '--project', 'src/JIM.Worker', '--no-build' -RedirectStandardOutput "$logDir/worker.log" -RedirectStandardError "$logDir/worker.err"
[Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', 'http://localhost:5200')
Start-Process $dotnet -ArgumentList 'run', '--project', 'src/JIM.Web', '--no-build' -RedirectStandardOutput "$logDir/web.log" -RedirectStandardError "$logDir/web.err"
if ($IncludeScheduler) {
    Start-Process $dotnet -ArgumentList 'run', '--project', 'src/JIM.Scheduler', '--no-build' -RedirectStandardOutput "$logDir/scheduler.log" -RedirectStandardError "$logDir/scheduler.err"
}

Write-Info 'Waiting for web readiness (first run includes database migration and seeding)...'
$ready = $false
foreach ($attempt in 1..100) {
    try {
        $response = Invoke-WebRequest -Uri 'http://localhost:5200/api/v1/health/ready' -NoProxy -TimeoutSec 5 -SkipHttpErrorCheck
        if ($response.StatusCode -eq 200) { $ready = $true; break }
    } catch { }
    Start-Sleep -Seconds 3
}
if (-not $ready) { throw "JIM.Web did not become ready; check $logDir/web.log and $logDir/worker.log" }

Write-Info 'Stack is ready.'
Write-Host ''
Write-Host '  Web UI:        http://localhost:5200  (dev realm sign-in: admin / admin)'
Write-Host '  Keycloak:      http://localhost:8181  (admin console: admin / admin)'
Write-Host "  Service logs:  $logDir"
Write-Host '  Stop with:     pwsh ./scripts/Start-SandboxStack.ps1 -Down'
