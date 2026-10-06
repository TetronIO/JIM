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
      5. Checks the bundled PostgreSQL runs with the memory settings the installer sized for the host.
      6. Backs up and restores the encryption keys with the commands in Backup & Disaster Recovery, as written
         (rootless, with jim-podman as Running on Podman says), checks the archive with the page's own check,
         and checks the commands fetched no image, which an air-gapped host could not, and put the key volume
         back as it was; then starts JIM again on the restored keys.
      7. Saves the containers' inspect output, for Compare-RuntimeParity.ps1.
      8. Stops JIM, freeing its port for the next leg, unless -KeepRunning.

    Before stopping JIM in step 4, and again before step 7, it checks that no container restarted, and that the
    kernel logged no AppArmor denial under a container's or a runtime's profile and no process killed for a fault
    since the leg began (Select-KernelFault.ps1). A service that crashes is restarted and soon passes its health
    check again, so readiness alone cannot show a crash (#1953).

    It saves the kernel's log for the leg to the output folder, and on failure each container's log too, before
    rethrowing.

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

# Runs a native command, returning its standard output as text; its standard error, where the runtimes put
# warnings, is kept out of what callers compare, and shown only if the command fails. Throws on a non-zero exit
# unless -AllowFailure, when the exit code is left in $LASTEXITCODE for the caller.
function Invoke-Native {
    param(
        [Parameter(Mandatory)]
        [string[]]$Command,

        [switch]$AllowFailure
    )
    $executable = $Command[0]
    $arguments = @($Command | Select-Object -Skip 1)
    $output = [System.Collections.Generic.List[string]]::new()
    $errors = [System.Collections.Generic.List[string]]::new()
    & $executable @arguments 2>&1 | ForEach-Object {
        if ($_ -is [System.Management.Automation.ErrorRecord]) { $errors.Add("$_") } else { $output.Add("$_") }
    }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "Failed (exit $LASTEXITCODE): $($Command -join ' ')`n$((@($output) + @($errors)) -join "`n")"
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

# Runs a command as the documentation gives it, as an administrator does: as root, in a shell of root's own, so
# with root's environment rather than this one, whose XDG folders would point the jim account's Podman at the
# runner's. -e, so that a step that fails stops it.
function Invoke-Documented {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [switch]$AllowFailure
    )
    # By its path: run as root, with no sudo to find it, PowerShell would take the first env on PATH, executable or not.
    Invoke-Native -AllowFailure:$AllowFailure -Command ($elevate + @('/usr/bin/env', '-i',
        'PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin', 'HOME=/root', 'bash', '-ec', $Command))
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

# What the kernel and the audit system logged since the leg began: AppArmor's denials go to one or the other,
# depending on whether auditd runs.
function Get-KernelLog {
    $log = Invoke-Native ($elevate + @('journalctl', '--no-pager', '--quiet', '--output', 'short-iso',
        '--since', "@$legStarted", '_TRANSPORT=kernel', '+', '_TRANSPORT=audit'))
    @($log -split "`n" | Where-Object { $_ })
}

# No container restarted, and the kernel logged nothing against them, since the leg began. A service that crashes is
# restarted by its runtime and soon passes its health check again, so readiness alone hid JIM's .NET services aborting
# on every start under rootful Podman on Ubuntu 24.04, until #1953.
function Assert-NoCrash {
    param([Parameter(Mandatory)][string]$Stage)
    $restarts = @(foreach ($name in $containers.Values) {
        $count = [int](Invoke-Runtime @('inspect', '-f', '{{.RestartCount}}', $name))
        if ($count -gt 0) { "$name restarted $count times" }
    })
    $faults = @(Get-KernelLog | & (Join-Path $PSScriptRoot 'Select-KernelFault.ps1'))
    if ($restarts.Count -gt 0 -or $faults.Count -gt 0) {
        $found = $restarts + @($faults | Select-Object -First 10)
        if ($faults.Count -gt 10) {
            $found += "... and $($faults.Count - 10) more in $leg-kernel.log"
        }
        throw "JIM's containers crashed, or the kernel denied them, by the end of $Stage`:`n$($found -join "`n")"
    }
    Write-Step "no container restarted by the end of $Stage, and the kernel logged no AppArmor denial or fault against them"
}

function Save-KernelLog {
    Get-KernelLog | Set-Content (Join-Path $OutputPath "$leg-kernel.log")
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
        # The account's systemd manager: why a unit was not generated, or would not start.
        if ($Rootless -and (Invoke-Native -AllowFailure @('id', '-u', $account))) {
            $uid = Invoke-Native @('id', '-u', $account)
            Invoke-Native -AllowFailure ($elevate + @('journalctl', "_UID=$uid", '--no-pager', '-n', '200')) |
                Set-Content (Join-Path $OutputPath "$leg-journal.log")
        }
        Save-KernelLog
    }
    catch {
        Write-Warning "Could not save the containers' logs: $_"
    }
}

$caPath = Join-Path $OutputPath "$leg-ca.crt"
# Whole seconds, as journalctl --since takes them; a second early rather than late.
$legStarted = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 1
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
        # On Ubuntu 24.04, GitHub's runners among them, the installer must add the network and signal rules that
        # rootful Podman's AppArmor profiles need.
        'JIM_SETUP_FIX_APPARMOR=true'
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
    # The first start, which prepares the database, is where the services work hardest; the restart below replaces
    # the containers, and their restart counts with them.
    Assert-NoCrash -Stage 'the first start'

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

    # 5. The bundled PostgreSQL runs with the memory the installer sized for this host (#1943): the settings go from
    # .env through the compose file's command on Docker, and from jim-config.yaml through the pod's on Podman.
    $settingsFile = if ($Runtime -eq 'docker') { "$installPath/.env" } else { "$installPath/jim-config.yaml" }
    $settingsText = Invoke-Native ($elevate + @('cat', $settingsFile))
    $sized = foreach ($setting in 'shared_buffers', 'effective_cache_size', 'maintenance_work_mem', 'work_mem') {
        $key = "JIM_DB_$($setting.ToUpperInvariant())"
        if ($settingsText -notmatch "(?m)^\s*$key\s*[=:]\s*`"?(\d+)MB`"?\s*$") {
            throw "The installer did not size the database's ${setting}: $key is not set in $settingsFile"
        }
        $chosen = [long]$Matches[1]
        $actual = [long](Invoke-Sql "SELECT pg_size_bytes(current_setting('$setting'));")
        if ($actual -ne $chosen * 1MB) {
            throw "PostgreSQL runs with $setting = $actual bytes, not the ${chosen}MB the installer chose"
        }
        "$setting ${chosen}MB"
    }
    if ($Runtime -eq 'docker') {
        if ($settingsText -notmatch '(?m)^JIM_DB_SHM_SIZE=(\d+)mb\s*$') {
            throw "The installer did not size the database's /dev/shm: JIM_DB_SHM_SIZE is not set in $settingsFile"
        }
        $chosen = [long]$Matches[1]
        $actual = [long](Invoke-Runtime @('inspect', '-f', '{{.HostConfig.ShmSize}}', $containers.database))
        if ($actual -ne $chosen * 1MB) {
            throw "The database container's /dev/shm is $actual bytes, not the ${chosen}mb the installer chose"
        }
        $sized += "shm_size ${chosen}mb"
    }
    Write-Step "PostgreSQL runs with the memory the installer sized: $($sized -join ', ')"

    # 6. The key backup and restore that Backup & Disaster Recovery gives work as written, with JIM stopped as the page
    # and the upgrade guide have it, and fetch no image. Until #1949 the Docker ones ran an image JIM does not ship,
    # which an air-gapped host cannot fetch; until #1954 the Podman backup wrote an empty archive rootless, and said
    # nothing. A marker in the key volume makes the round trip visible, and a file added after the backup must not
    # survive the restore.
    $docs = Join-Path $PSScriptRoot '..' '..' '..' 'docs' 'administration'
    $getCommand = Join-Path $PSScriptRoot 'Get-DocumentedCommand.ps1'
    $page = Join-Path $docs 'backup-recovery.md'
    $tab = if ($Runtime -eq 'docker') { 'Docker' } else { 'Podman' }
    $backup = & $getCommand -Path $page -Heading '2. Back up the encryption keys' -Tab $tab
    $check = & $getCommand -Path $page -Heading '2. Back up the encryption keys'
    $restore = & $getCommand -Path $page -Heading 'Restoring' -Tab $tab
    if ($Runtime -eq 'podman') {
        # The page's installation folder is /opt/jim; this leg's is its own.
        $backup = $backup.Replace('/opt/jim/', "$installPath/")
        $restore = $restore.Replace('/opt/jim/', "$installPath/")
    }
    if ($Rootless) {
        # As Running on Podman says: jim-podman wherever the page runs sudo podman, defined as that page defines it.
        $rootlessCommands = & $getCommand -Path (Join-Path $docs 'podman.md') -Heading 'Rootless commands'
        $jimPodman = @($rootlessCommands -split "`n" | Where-Object { $_ -match '^jim-podman\(\)' })
        if ($jimPodman.Count -ne 1) {
            throw "Running on Podman's Rootless commands no longer define jim-podman on a line of its own:`n$rootlessCommands"
        }
        $backup = $jimPodman[0] + "`n" + ($backup -replace '\bsudo podman\b', 'jim-podman')
        $restore = $jimPodman[0] + "`n" + ($restore -replace '\bsudo podman\b', 'jim-podman')
    }
    $keys = Invoke-Runtime @('volume', 'inspect', '-f', '{{.Mountpoint}}', 'jim-keys-volume')
    $keyVolumeState = {
        Invoke-Native ($elevate + @('sh', '-c',
            'cd "$1" && find . -printf "%U:%G %m %p\n" | sort && find . -type f -exec sha256sum {} + | sort', 'sh', $keys))
    }
    $images = { Invoke-Runtime @('image', 'ls', '--no-trunc', '--format', '{{.Repository}}:{{.Tag}} {{.ID}}') }

    Invoke-Runtime @('exec', $containers.web, 'sh', '-c', "echo $marker > /data/keys/ci-boot-marker") | Out-Null
    if ($Runtime -eq 'docker') {
        Invoke-Compose @('stop', $containers.web, $containers.worker, $containers.scheduler)
    }
    else {
        Invoke-Systemctl @('stop', 'jim.service')
    }
    $keysBefore = & $keyVolumeState
    if ($keysBefore -notmatch '\./key-') {
        throw "JIM has written no encryption key for the documented backup to take:`n$keysBefore"
    }
    $imagesBefore = (& $images) -split "`n"
    # In a folder of its own, outside the uploaded output: the archive holds the keys.
    $backupFolder = Invoke-Native @('mktemp', '-d')
    Push-Location $backupFolder
    try {
        Invoke-Documented $backup | Out-Null
        $listed = Invoke-Documented -AllowFailure $check
        if ($LASTEXITCODE -ne 0) {
            throw "The documented key backup wrote an archive without the keys in it, which the page's own check found:`n$backup"
        }
        Invoke-Native ($elevate + @('sh', '-c', 'echo changed > "$1/ci-boot-marker" && echo stray > "$1/ci-stray"', 'sh', $keys)) |
            Out-Null
        Invoke-Documented $restore | Out-Null
    }
    finally {
        Pop-Location
        Invoke-Native -AllowFailure ($elevate + @('rm', '-rf', $backupFolder)) | Out-Null
    }

    $fetched = @((& $images) -split "`n" | Where-Object { $_ -notin $imagesBefore })
    if ($fetched) {
        throw "The documented key backup or restore fetched an image, which an air-gapped host cannot: $($fetched -join ', ')"
    }
    $keysAfter = & $keyVolumeState
    if ($keysAfter -cne $keysBefore) {
        throw "The documented key restore did not put the key volume back as the backup took it.`nBefore:`n$keysBefore`nAfter:`n$keysAfter"
    }
    if ($Runtime -eq 'docker') {
        Invoke-Compose @('start', $containers.web, $containers.worker, $containers.scheduler)
    }
    else {
        Invoke-Systemctl @('start', 'jim.service')
    }
    Wait-Until -Description 'ready over HTTPS on the restored keys' -Condition { Test-ReadyOverHttps }
    Wait-AllHealthy
    Write-Step "the documented key backup ($(@($listed -split "`n").Count) key files) and restore put the key volume back, fetching no image"

    Assert-NoCrash -Stage 'the leg'
    Save-KernelLog

    # 7. What each container runs, for the parity comparison.
    Invoke-Runtime (@('inspect') + @($containers.Values)) | Set-Content (Join-Path $OutputPath "$leg.inspect.json")
    Write-Step "saved $leg.inspect.json"

    if ($env:GITHUB_STEP_SUMMARY) {
        "- ✅ **$leg**: installed from the bundle, ready over HTTPS, $($containers.Count) of $($containers.Count) containers healthy, none restarted and none denied by the kernel, data kept through a stop and start, PostgreSQL sized to the host, the documented key backup and restore worked offline" |
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
    # 8. Free the port for the next leg.
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
