# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Installs JIM from a release bundle with its own installer, on one runtime, and proves the result.

.DESCRIPTION
    One leg of the deployment-boot check. It does what a customer does, and then checks what they would:

      1. Installs JIM with the bundle's setup.sh, offline, with a certificate the installer generates.
      2. Waits until JIM answers ready over HTTPS, trusting only the installer's certificate authority.
      3. Waits until every container's own health check passes (podman healthcheck run on Podman).
      4. Writes a marker to the database and to the File Connector volume, stops JIM and starts it again
         (docker compose down and up; systemctl stop and start of the Quadlet units), and checks that JIM
         came back as new containers, ready over HTTPS, with both markers still there.
      5. Saves the containers' inspect output, for Compare-RuntimeParity.ps1.
      6. Stops JIM, freeing its port for the next leg, unless -KeepRunning.

    On failure it saves each container's log to the output folder before rethrowing.

.PARAMETER BundlePath
    The extracted release bundle, which holds setup.sh.

.PARAMETER Runtime
    docker or podman.

.PARAMETER Rootless
    Podman only: install JIM rootless, under the jim account the installer creates.

.PARAMETER Authority
    The OIDC authority to install JIM with; see Start-TestIdentityProvider.ps1.

.PARAMETER OutputPath
    Where to write the installer's log, the inspect output and, on failure, the containers' logs.

.PARAMETER KeepRunning
    Leave JIM running at the end, for looking at by hand.

.EXAMPLE
    ./test/ci/deployment/Invoke-DeploymentBoot.ps1 -BundlePath ./release-output/jim-release-ci -Runtime podman -Rootless -Authority $authority -OutputPath ./deployment-boot
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BundlePath,

    [Parameter(Mandatory)]
    [ValidateSet('docker', 'podman')]
    [string]$Runtime,

    [switch]$Rootless,

    [Parameter(Mandatory)]
    [string]$Authority,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

if ($Rootless -and $Runtime -ne 'podman') {
    throw '-Rootless applies to Podman only'
}

$leg = if ($Runtime -eq 'docker') { 'docker' } elseif ($Rootless) { 'podman-rootless' } else { 'podman-rootful' }
$installPath = "/opt/jim-$leg"
$hostName = 'jim.ci.test'
$account = 'jim'
$containers = if ($Runtime -eq 'docker') {
    [ordered]@{ web = 'jim.web'; worker = 'jim.worker'; scheduler = 'jim.scheduler'; database = 'jim.database' }
}
else {
    [ordered]@{ web = 'jim-web'; worker = 'jim-worker'; scheduler = 'jim-scheduler'; database = 'jim-database-postgres' }
}
$composeFiles = @('-f', 'docker-compose.yml', '-f', 'docker-compose.production.yml', '--profile', 'with-db')

$BundlePath = (Resolve-Path $BundlePath).Path
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path
$isRoot = (id -u) -eq '0'
# Always an array, possibly empty, so that it prefixes command lines rather than joining their words.
$elevate = @(if (-not $isRoot) { 'sudo' })

function Write-Step {
    param([string]$Message)
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ${leg}: $Message"
}

# Runs a native command, returning its output as text. Throws on a non-zero exit unless -AllowFailure, when
# the exit code is left in $LASTEXITCODE for the caller.
function Invoke-Native {
    param(
        [Parameter(Mandatory)]
        [string[]]$Command,

        [switch]$AllowFailure
    )
    $executable = $Command[0]
    $arguments = @($Command | Select-Object -Skip 1)
    $output = & $executable @arguments 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "Failed (exit $LASTEXITCODE): $($Command -join ' ')`n$($output -join "`n")"
    }
    $output -join "`n"
}

# The runtime's command line, run as whoever owns this installation's containers: root, or the jim account.
function Get-RuntimeCommand {
    if (-not $Rootless) {
        return $elevate + $Runtime
    }
    $uid = Invoke-Native @('id', '-u', $account)
    $accountHome = (Invoke-Native @('getent', 'passwd', $account)).Split(':')[5]
    $asAccount = if ($isRoot) { @('runuser', '-u', $account, '--') } else { @('sudo', '-u', $account) }
    # With a clean environment, as setup.sh uses: sudo may keep the caller's XDG_CONFIG_HOME, which would point
    # Podman at a folder the account cannot read.
    $asAccount + @('env', '-i', 'PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin', "HOME=$accountHome",
        "XDG_RUNTIME_DIR=/run/user/$uid", 'podman')
}

function Invoke-Runtime {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [switch]$AllowFailure
    )
    # Rootless Podman fails in a folder its account cannot read, as the workspace may be.
    Push-Location /
    try {
        Invoke-Native -Command (@(Get-RuntimeCommand) + $Arguments) -AllowFailure:$AllowFailure
    }
    finally {
        Pop-Location
    }
}

function Invoke-Systemctl {
    param([string[]]$Arguments)
    $systemctl = @(if ($Rootless) { $elevate + @('systemctl', '--user', '-M', "$account@") } else { $elevate + 'systemctl' })
    Invoke-Native -Command ($systemctl + $Arguments) | Out-Null
}

function Invoke-Compose {
    param([string[]]$Arguments)
    Push-Location $installPath
    try {
        Invoke-Native -Command ($elevate + @('docker', 'compose') + $composeFiles + $Arguments) | Out-Null
    }
    finally {
        Pop-Location
    }
}

function Wait-Until {
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [scriptblock]$Condition,

        [int]$TimeoutMinutes = 10
    )
    $started = Get-Date
    $deadline = $started.AddMinutes($TimeoutMinutes)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) {
            throw "Timed out after $TimeoutMinutes minutes waiting for: $Description"
        }
        Start-Sleep -Seconds 5
    }
    Write-Step "$Description ($([int]((Get-Date) - $started).TotalSeconds)s)"
}

# Ready over HTTPS, at the certificate's name, trusting only the certificate authority the installer created.
function Test-ReadyOverHttps {
    $code = Invoke-Native -AllowFailure -Command @(
        'curl', '-s', '-o', '/dev/null', '-w', '%{http_code}', '--max-time', '10', '--noproxy', $hostName,
        '--cacert', $caPath, '--resolve', "${hostName}:443:127.0.0.1", "https://${hostName}/api/v1/health/ready")
    $code -eq '200'
}

function Test-ContainerHealthy {
    param([string]$Name)
    if ($Runtime -eq 'docker') {
        (Invoke-Runtime -AllowFailure @('inspect', '-f', '{{.State.Health.Status}}', $Name)) -eq 'healthy'
    }
    else {
        Invoke-Runtime -AllowFailure @('healthcheck', 'run', $Name) | Out-Null
        $LASTEXITCODE -eq 0
    }
}

function Wait-AllHealthy {
    foreach ($name in $containers.Values) {
        # Called afresh by Wait-Until, the condition sees this function's $name, and the script's functions.
        Wait-Until -TimeoutMinutes 5 -Description "$name healthy" -Condition { Test-ContainerHealthy $name }
    }
}

function Invoke-Sql {
    param([string]$Sql)
    Invoke-Runtime @('exec', $containers.database, 'psql', '-U', 'jim', '-d', 'jim', '-v', 'ON_ERROR_STOP=1', '-Atc', $Sql)
}

# Best effort: a failure here must not hide the failure being diagnosed.
function Save-Diagnostics {
    try {
        Invoke-Runtime -AllowFailure @('ps', '-a') | Set-Content (Join-Path $OutputPath "$leg-ps.txt")
        foreach ($service in $containers.Keys) {
            Invoke-Runtime -AllowFailure @('logs', '--tail', '300', $containers[$service]) |
                Set-Content (Join-Path $OutputPath "$leg-$service.log")
        }
    }
    catch {
        Write-Warning "Could not save the containers' logs: $_"
    }
}

$caPath = Join-Path $OutputPath "$leg-ca.crt"
try {
    # 1. Install, as a customer does, answering every question in advance.
    Write-Step "installing from $BundlePath"
    $installerArguments = @('--runtime', $Runtime)
    if ($Rootless) {
        $installerArguments += '--rootless'
    }
    $settings = @(
        "JIM_INSTALL_DIR=$installPath"
        'JIM_SETUP_DB_MODE=bundled'
        'JIM_SETUP_AUTO_START=true'
        "JIM_SSO_AUTHORITY=$Authority"
        'JIM_SSO_CLIENT_ID=jim-web'
        'JIM_SSO_SECRET=jim-dev-secret'
        'JIM_SSO_API_SCOPE=jim-api'
        'JIM_SSO_CLAIM_TYPE=sub'
        'JIM_SSO_MV_ATTRIBUTE=Subject Identifier'
        'JIM_SSO_INITIAL_ADMIN=00000000-0000-0000-0000-000000000001'
        'JIM_WEB_PORT=443'
        'JIM_SETUP_TLS_MODE=generate'
        "JIM_SETUP_TLS_NAMES=$hostName"
        'JIM_TRUSTED_PROXIES='
        'JIM_SETUP_OPEN_FIREWALL=false'
    )
    $installLog = Join-Path $OutputPath "$leg-install.log"
    $started = Get-Date
    # Through bash, for its redirections: the installer reads no answers from standard input, and its whole
    # output goes to the log.
    $installer = $elevate + @('env') + $settings + @("$BundlePath/setup.sh") + $installerArguments
    & bash -c '"$@" < /dev/null > "$0" 2>&1' $installLog @installer
    $installExit = $LASTEXITCODE
    Get-Content $installLog | Select-Object -Last 25 | ForEach-Object { Write-Host "    $_" }
    if ($installExit -ne 0) {
        throw "The installer failed (exit $installExit); see $installLog"
    }
    Write-Step "installed and ready ($([int]((Get-Date) - $started).TotalSeconds)s)"

    Invoke-Native ($elevate + @('cat', "$installPath/tls/ca.crt")) | Set-Content $caPath

    # 2 and 3. Ready over HTTPS, and every container's own health check passing.
    Wait-Until -Description 'ready over HTTPS' -Condition { Test-ReadyOverHttps }
    Wait-AllHealthy

    # 4. Data survives JIM being stopped and started, as new containers.
    $marker = [Guid]::NewGuid().ToString('N')
    Invoke-Sql "CREATE TABLE ci_boot_marker (token text); INSERT INTO ci_boot_marker VALUES ('$marker');" | Out-Null
    Invoke-Runtime @('exec', $containers.worker, 'sh', '-c', "echo $marker > /connector-files/ci-boot-marker") | Out-Null
    $webIdBefore = Invoke-Runtime @('inspect', '-f', '{{.Id}}', $containers.web)

    Write-Step 'stopping and starting JIM'
    if ($Runtime -eq 'docker') {
        Invoke-Compose @('down')
        Invoke-Compose @('up', '-d', '--pull', 'never')
    }
    else {
        Invoke-Systemctl @('stop', 'jim.service', 'jim-database.service')
        Invoke-Systemctl @('start', 'jim-database.service', 'jim.service')
    }
    Wait-Until -Description 'ready over HTTPS after the restart' -Condition { Test-ReadyOverHttps }
    Wait-AllHealthy

    if ((Invoke-Runtime @('inspect', '-f', '{{.Id}}', $containers.web)) -eq $webIdBefore) {
        throw 'JIM came back in the same containers: the restart did not recreate them'
    }
    if ((Invoke-Sql 'SELECT token FROM ci_boot_marker;') -ne $marker) {
        throw 'The database marker did not survive the restart'
    }
    if ((Invoke-Runtime @('exec', $containers.worker, 'cat', '/connector-files/ci-boot-marker')) -ne $marker) {
        throw 'The File Connector volume marker did not survive the restart'
    }
    Write-Step 'the database and the File Connector volume kept their data'

    # 5. What each container runs, for the parity comparison.
    Invoke-Runtime (@('inspect') + @($containers.Values)) | Set-Content (Join-Path $OutputPath "$leg.inspect.json")
    Write-Step "saved $leg.inspect.json"

    if ($env:GITHUB_STEP_SUMMARY) {
        "- ✅ **$leg**: installed from the bundle, ready over HTTPS, $($containers.Count) of $($containers.Count) containers healthy, data kept through a stop and start" |
            Add-Content $env:GITHUB_STEP_SUMMARY
    }
}
catch {
    Write-Step "FAILED: $_"
    Save-Diagnostics
    if ($env:GITHUB_STEP_SUMMARY) {
        "- ❌ **$leg**: $($_.Exception.Message.Split("`n")[0])" | Add-Content $env:GITHUB_STEP_SUMMARY
    }
    throw
}
finally {
    # 6. Free the port for the next leg.
    if (-not $KeepRunning -and (Test-Path $caPath)) {
        Write-Step 'stopping JIM'
        try {
            if ($Runtime -eq 'docker') {
                Invoke-Compose @('down')
            }
            else {
                Invoke-Systemctl @('stop', 'jim.service', 'jim-database.service')
            }
        }
        catch {
            Write-Warning "Could not stop JIM: $_"
        }
    }
}
