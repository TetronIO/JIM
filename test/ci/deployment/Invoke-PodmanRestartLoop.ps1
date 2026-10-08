# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Restarts a rootful Podman JIM over and over, as the documentation gives it, and captures its network the moment
    a restart leaves JIM not ready (#2009).

.DESCRIPTION
    On AlmaLinux 9 with rootful Podman, some restarts left JIM's services logging "jim-database:5432: No route to
    host" for their whole five-minute wait, while the bundled PostgreSQL reported healthy. It could not be reproduced
    away from the lab that found it, so this script is for running there, as root, on the JIM host itself.

    Each round stops JIM (systemctl stop jim.service jim-database.service), waits, starts it again (systemctl start
    jim-database.service jim.service), and waits for JIM to answer ready inside its pod. Every round records a small
    snapshot of JIM's network in rounds.jsonl: the database pod's address, what the jim pod resolves jim-database to,
    the records aardvark-dns holds, whether each pod's veth is attached to the network's bridge and what NetworkManager
    makes of it, firewalld's trust of the network, and how a connection to port 5432 fares by name and at the
    database's own address, with the jim pod's neighbour entry for the address. The neighbour entry, not the time a
    connection took, tells "no route to host" from an address nobody answers for (FAILED, INCOMPLETE) apart from one
    a firewall rejects (resolved).

    The cause it found for #2009: NetworkManager took the database pod's veth for itself, starting DHCP on it, which
    released it from the bridge. setup.sh now tells NetworkManager to leave Podman's interfaces alone.

    The first ready round is captured in full as a baseline, in round-NN-baseline. A round where JIM is not ready in
    time is captured in full in round-NN-failed: everything the snapshot reads, plus each pod's interfaces, routes and
    neighbours, the firewall's rules, the journal since the round began and the containers' logs. The script then
    prints what Get-PodmanNetworkFinding.ps1 makes of it and stops, leaving JIM as it is so that it can be inspected
    by hand, unless -Continue. Restart JIM the documented way to bring it back.

    Rootful only, with the bundled database: the fault has only been seen there. Needs PowerShell 7 on the host; on
    an air-gapped host, install it from Microsoft's RPM package carried in, with libicu, which AlmaLinux's cloud images
    leave out. Without libicu, run pwsh with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 set.

.PARAMETER Rounds
    How many restarts to make. Default 20.

.PARAMETER GapSeconds
    How long JIM stays stopped in each round. Default 30, as in the lab that found #2009.

.PARAMETER ReadyTimeoutSeconds
    How long after starting JIM a round waits for it to be ready before calling the round failed. A healthy restart
    is ready in 5 to 20 seconds. Default 180, within the services' own five-minute wait for their database, so the
    capture sees the failure while it is happening.

.PARAMETER OutputPath
    Where to write the rounds and the captures. Default ./jim-restart-loop-<date and time>.

.PARAMETER Continue
    After a failed round, carry on with the next restart instead of stopping with JIM left as it failed.

.EXAMPLE
    sudo pwsh ./Invoke-PodmanRestartLoop.ps1 -Rounds 30

.EXAMPLE
    sudo pwsh ./Invoke-PodmanRestartLoop.ps1 -Rounds 100 -Continue -OutputPath /var/tmp/jim-restarts
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 10000)]
    [int]$Rounds = 20,

    [ValidateRange(0, 3600)]
    [int]$GapSeconds = 30,

    [ValidateRange(10, 3600)]
    [int]$ReadyTimeoutSeconds = 180,

    [string]$OutputPath = "./jim-restart-loop-$(Get-Date -Format 'yyyyMMdd-HHmmss')",

    [switch]$Continue
)

$ErrorActionPreference = 'Stop'

$networkName = 'jim'
$databaseName = 'jim-database'
$databaseContainer = 'jim-database-postgres'
# JIM's containers share their pod's network, and in the failing state each restarts in turn, so the probes run in
# whichever is up.
$jimContainers = @('jim-web', 'jim-worker', 'jim-scheduler')
$findingScript = Join-Path $PSScriptRoot 'Get-PodmanNetworkFinding.ps1'

function Write-Step {
    param([string]$Message)
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message"
}

# Runs a native command and returns its output, standard error included, as text. Throws on a non-zero exit unless
# -AllowFailure, when the exit code is left in $LASTEXITCODE.
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

# Best effort, for the captures: a command that fails or is missing leaves its error in the file instead.
function Save-Command {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string[][]]$Commands
    )
    $text = foreach ($command in $Commands) {
        "### $($command -join ' ')"
        try {
            Invoke-Native -AllowFailure -Command $command
        }
        catch [System.Management.Automation.CommandNotFoundException] {
            "(not available: $($command[0]))"
        }
        ''
    }
    $text | Set-Content -Path $Path
}

function Test-CommandAvailable {
    param([string]$Name)
    [bool](Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue)
}

function Get-RunningJimContainer {
    $running = @((Invoke-Native -AllowFailure @('podman', 'ps', '--format', '{{.Names}}')) -split "`n")
    $jimContainers | Where-Object { $_ -in $running } | Select-Object -First 1
}

function Test-JimReady {
    Invoke-Native -AllowFailure @('podman', 'exec', 'jim-web', 'curl', '-fsS', '-o', '/dev/null', '--max-time', '5',
        'http://localhost:8080/api/v1/health/ready') | Out-Null
    $LASTEXITCODE -eq 0
}

# Opens port 5432 at the given host from the jim pod, as JIM's services do, timing it inside the container.
function Test-DatabasePort {
    param([string]$Container, [string]$Target)
    if (-not $Container) {
        return [pscustomobject]@{ ExitCode = -1; Message = 'none of the jim pod''s containers is running'; Milliseconds = 0 }
    }
    $probe = 's=$(date +%s%N); out=$(timeout 10 bash -c "exec 3<>/dev/tcp/$1/5432" 2>&1); rc=$?; e=$(date +%s%N); ' +
        'printf "%s\n%s\n" "$rc" "$(( (e - s) / 1000000 ))"; printf "%s" "$out" | head -n 1'
    $lines = @((Invoke-Native -AllowFailure @('podman', 'exec', $Container, 'bash', '-c', $probe, 'probe', $Target)) -split "`n")
    if ($lines.Count -lt 2 -or $lines[0] -notmatch '^\d+$') {
        return [pscustomobject]@{ ExitCode = -1; Message = ($lines -join ' '); Milliseconds = 0 }
    }
    [pscustomobject]@{ ExitCode = [int]$lines[0]; Milliseconds = [int]$lines[1]; Message = ($lines | Select-Object -Skip 2) -join ' ' }
}

# The process whose network namespace a pod's containers share: its infra container's.
function Get-PodNetworkPid {
    param([string]$Pod)
    $infra = Invoke-Native -AllowFailure @('podman', 'pod', 'inspect', $Pod, '--format', '{{.InfraContainerID}}')
    if ($LASTEXITCODE -ne 0 -or -not $infra) { return $null }
    $processId = Invoke-Native -AllowFailure @('podman', 'inspect', '--format', '{{.State.Pid}}', $infra.Trim())
    if ($LASTEXITCODE -ne 0 -or $processId -notmatch '^\d+$' -or $processId -eq '0') { return $null }
    $processId
}

# The pod's veth on the host, and whether it is attached to the bridge: the pod's interface names its peer by index,
# after "@if", and the peer must be the bridge's port on the host. Nothing when the pod is not running.
function Get-PodVeth {
    param([string]$Pod, [string]$Bridge)
    $processId = Get-PodNetworkPid $Pod
    if (-not $processId -or -not $Bridge) { return $null }
    $links = Invoke-Native -AllowFailure @('nsenter', '-t', $processId, '-n', 'ip', '-o', 'link', 'show')
    $peers = @([regex]::Matches($links, '@if(\d+)') | ForEach-Object { $_.Groups[1].Value })
    $hostLinks = @((Invoke-Native -AllowFailure @('ip', '-o', 'link', 'show')) -split "`n")
    foreach ($peer in $peers) {
        $hostLink = $hostLinks | Where-Object { $_ -match "^${peer}: " } | Select-Object -First 1
        if ($hostLink -and $hostLink -match "^${peer}: ([^@:]+)") {
            return [pscustomobject]@{ Name = $Matches[1]; OnBridge = $hostLink -match "\bmaster $([regex]::Escape($Bridge))\b" }
        }
    }
    [pscustomobject]@{ Name = $null; OnBridge = $false }
}

# NetworkManager's device list, as nmcli prints it with colons escaped inside values; nothing without NetworkManager.
function Get-NetworkManagerDevice {
    if (-not (Test-CommandAvailable 'nmcli')) { return @() }
    $lines = Invoke-Native -AllowFailure @('nmcli', '-t', '-f', 'DEVICE,STATE,CONNECTION', 'device')
    if ($LASTEXITCODE -ne 0) { return @() }
    @($lines -split "`n" | Where-Object { $_ } | ForEach-Object {
        $fields = @($_ -split '(?<!\\):' | ForEach-Object { $_ -replace '\\:', ':' })
        [pscustomobject]@{ Device = $fields[0]; State = $fields[1]; Connection = ($fields | Select-Object -Skip 2) -join ':' }
    })
}

# What NetworkManager logged since the round began about releasing a device from a bridge, as it does when it takes
# a veth for a connection of its own.
function Get-NetworkManagerRelease {
    param([long]$Since)
    $journal = Invoke-Native -AllowFailure @('journalctl', '--no-pager', '--quiet', '-o', 'cat', '-u', 'NetworkManager.service',
        '--since', "@$Since")
    @($journal -split "`n" | Where-Object { $_ -match 'released from controller' })
}

# The state of the pod's neighbour entry for an address (REACHABLE, STALE, FAILED, ...), or nothing if it has none.
function Get-NeighbourState {
    param([string]$Pod, [string]$Address)
    $processId = Get-PodNetworkPid $Pod
    if (-not $processId -or -not $Address) { return $null }
    $entry = (Invoke-Native -AllowFailure @('nsenter', '-t', $processId, '-n', 'ip', 'neigh', 'show', $Address)).Trim()
    if (-not $entry) { return $null }
    ($entry -split '\s+')[-1]
}

function Get-Network {
    $json = Invoke-Native -AllowFailure @('podman', 'network', 'inspect', $networkName)
    if ($LASTEXITCODE -ne 0) { return $null }
    $network = @($json | ConvertFrom-Json)[0]
    [pscustomobject]@{
        Bridge = $network.network_interface
        Subnet = @($network.subnets | ForEach-Object { $_.subnet } | Where-Object { $_ -notmatch ':' })[0]
    }
}

# The IPv4 addresses aardvark-dns holds for the database's name: its configuration lists one container a line, as
# "<id> <IPv4s> <IPv6s> <names>", each list comma-separated, after a first line of the addresses it listens on.
function Get-AardvarkAddress {
    $path = "/run/containers/networks/aardvark-dns/$networkName"
    if (-not (Test-Path $path)) { return @() }
    @(Get-Content $path | Select-Object -Skip 1 | ForEach-Object {
        $fields = $_ -split ' '
        if ($fields.Count -ge 4 -and ($fields[3] -split ',') -contains $databaseName) {
            $fields[1] -split ',' | Where-Object { $_ }
        }
    } | Select-Object -Unique)
}

function Test-BridgeTrafficFiltered {
    param([string]$Bridge)
    $global = '/proc/sys/net/bridge/bridge-nf-call-iptables'
    if (-not (Test-Path $global)) { return $false }
    if ((Get-Content $global -Raw).Trim() -eq '1') { return $true }
    $perBridge = "/sys/class/net/$Bridge/bridge/nf_call_iptables"
    (Test-Path $perBridge) -and (Get-Content $perBridge -Raw).Trim() -eq '1'
}

function Get-NetworkError {
    param([long]$Since)
    $journal = Invoke-Native -AllowFailure @('journalctl', '--no-pager', '--quiet', '--output', 'short-iso', '--since', "@$Since")
    @($journal -split "`n" | Where-Object { $_ -match '(?i)netavark|aardvark' -and $_ -match '(?i)error|fail' })
}

function Get-NetworkSnapshot {
    param([long]$Since)
    $network = Get-Network
    $bridge = $network.Bridge
    $databaseVeth = Get-PodVeth -Pod $databaseName -Bridge $bridge
    $jimVeth = Get-PodVeth -Pod 'jim' -Bridge $bridge
    $databaseAddress = (Invoke-Native -AllowFailure @('podman', 'inspect', $databaseContainer, '--format',
            "{{(index .NetworkSettings.Networks `"$networkName`").IPAddress}}")).Trim()
    if ($LASTEXITCODE -ne 0 -or $databaseAddress -notmatch '^\d+\.\d+\.\d+\.\d+$') { $databaseAddress = $null }

    $container = Get-RunningJimContainer
    $resolved = @()
    if ($container) {
        $hosts = Invoke-Native -AllowFailure @('podman', 'exec', $container, 'getent', 'ahostsv4', $databaseName)
        $resolved = @($hosts -split "`n" | ForEach-Object { ($_ -split '\s+')[0] } |
            Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' } | Select-Object -Unique)
    }

    $firewalldRunning = $false
    $trusted = @()
    if (Test-CommandAvailable 'firewall-cmd') {
        $firewalldRunning = (Invoke-Native -AllowFailure @('firewall-cmd', '--state')).Trim() -eq 'running'
        if ($firewalldRunning) {
            $trusted = @((Invoke-Native -AllowFailure @('firewall-cmd', '--zone=trusted', '--list-sources')) -split '\s+' | Where-Object { $_ })
        }
    }

    [pscustomobject]@{
        Time = (Get-Date).ToString('o')
        ProbeContainer = $container
        DatabaseAddress = $databaseAddress
        PreviousDatabaseAddresses = @($script:previousAddresses)
        ResolvedAddresses = $resolved
        AardvarkAddresses = @(Get-AardvarkAddress)
        TcpByName = Test-DatabasePort -Container $container -Target $databaseName
        TcpByAddress = if ($databaseAddress) { Test-DatabasePort -Container $container -Target $databaseAddress } else { $null }
        # Read after the attempt above, so that it says how the attempt found the address.
        DatabaseNeighbour = Get-NeighbourState -Pod 'jim' -Address $databaseAddress
        DatabasePodOnBridge = $databaseVeth.OnBridge
        JimPodOnBridge = $jimVeth.OnBridge
        DatabaseVeth = $databaseVeth.Name
        JimVeth = $jimVeth.Name
        NetworkManagerDevices = @(Get-NetworkManagerDevice)
        NetworkManagerReleases = @(Get-NetworkManagerRelease $Since)
        Bridge = $bridge
        FirewalldRunning = $firewalldRunning
        Subnet = $network.Subnet
        TrustedSources = $trusted
        BridgeTrafficFiltered = Test-BridgeTrafficFiltered $bridge
        ReloadHelperActive = (Invoke-Native -AllowFailure @('systemctl', 'is-active', 'netavark-firewalld-reload.service')).Trim() -eq 'active'
        NetworkErrors = @(Get-NetworkError $Since)
    }
}

# Everything the snapshot reads, and what lies behind it, for reading after the event.
function Save-Capture {
    param([string]$Folder, [pscustomobject]$Snapshot, [long]$Since)
    New-Item -ItemType Directory -Path $Folder -Force | Out-Null
    $Snapshot | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Folder 'snapshot.json')
    $findings = @(& $findingScript -Snapshot $Snapshot)
    Set-Content -Path (Join-Path $Folder 'findings.txt') -Value $(if ($findings.Count) { $findings } else { '(nothing abnormal found in the network)' })

    Save-Command (Join-Path $Folder 'podman.txt') @(
        @('podman', 'ps', '-a'), @('podman', 'pod', 'ps'), @('podman', 'network', 'inspect', $networkName))
    $aardvark = '/run/containers/networks/aardvark-dns'
    Save-Command (Join-Path $Folder 'aardvark-dns.txt') @(
        @('ls', '-la', $aardvark), @('cat', "$aardvark/$networkName"), @('cat', "$aardvark/aardvark.pid"), @('pgrep', '-a', 'aardvark-dns'))
    Save-Command (Join-Path $Folder 'host-network.txt') @(
        @('ip', '-d', 'link', 'show'), @('ip', 'addr', 'show'), @('ip', 'route', 'show'), @('ip', '-s', 'neigh', 'show'),
        @('bridge', 'link', 'show'), @('bridge', 'fdb', 'show', 'br', "$($Snapshot.Bridge)"),
        @('sysctl', 'net.bridge.bridge-nf-call-iptables', 'net.ipv4.ip_forward'), @('grep', 'br_netfilter', '/proc/modules'))
    foreach ($pod in 'jim', $databaseName) {
        $processId = Get-PodNetworkPid $pod
        if ($processId) {
            Save-Command (Join-Path $Folder "$pod-pod-network.txt") @(
                @('nsenter', '-t', $processId, '-n', 'ip', '-d', 'link', 'show'), @('nsenter', '-t', $processId, '-n', 'ip', 'addr', 'show'),
                @('nsenter', '-t', $processId, '-n', 'ip', 'route', 'show'), @('nsenter', '-t', $processId, '-n', 'ip', '-s', 'neigh', 'show'),
                @('nsenter', '-t', $processId, '-n', 'ss', '-tan'))
        }
    }
    if ($Snapshot.ProbeContainer) {
        Save-Command (Join-Path $Folder 'jim-pod-dns.txt') @(
            @('podman', 'exec', $Snapshot.ProbeContainer, 'cat', '/etc/resolv.conf', '/etc/hosts'),
            @('podman', 'exec', $Snapshot.ProbeContainer, 'getent', 'ahosts', $databaseName))
    }
    Save-Command (Join-Path $Folder 'networkmanager.txt') @(
        @('nmcli', '-t', '-f', 'DEVICE,TYPE,STATE,CONNECTION', 'device'), @('nmcli', '-t', '-f', 'NAME,UUID,TYPE,DEVICE', 'connection', 'show'),
        @('NetworkManager', '--print-config'),
        @('journalctl', '--no-pager', '--output', 'short-iso', '-u', 'NetworkManager.service', '--since', "@$Since"))
    Save-Command (Join-Path $Folder 'firewall.txt') @(
        @('firewall-cmd', '--state'), @('firewall-cmd', '--get-default-zone'), @('firewall-cmd', '--get-active-zones'),
        @('firewall-cmd', '--list-all-zones'), @('systemctl', 'status', '--no-pager', 'firewalld', 'netavark-firewalld-reload'),
        @('nft', 'list', 'ruleset'), @('iptables-save'))
    Save-Command (Join-Path $Folder 'journal.txt') @(
        @('journalctl', '--no-pager', '--output', 'short-iso', '--since', "@$Since"))
    foreach ($name in @($jimContainers) + $databaseContainer) {
        Save-Command (Join-Path $Folder "$name.log") @(@('podman', 'logs', '--tail', '300', $name))
    }
    $findings
}

if ((Invoke-Native @('id', '-u')).Trim() -ne '0') {
    throw 'Run as root: this restarts a rootful Podman JIM, the installation #2009 was seen on.'
}
foreach ($unit in 'jim.service', 'jim-database.service') {
    Invoke-Native -AllowFailure @('systemctl', 'cat', $unit) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "No $unit here: this needs a rootful Podman JIM with the bundled database, installed with systemd."
    }
}
foreach ($tool in 'podman', 'nsenter', 'ip', 'journalctl') {
    if (-not (Test-CommandAvailable $tool)) { throw "$tool is needed, and is not installed." }
}

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path
Save-Command (Join-Path $OutputPath 'host.txt') @(
    @('cat', '/etc/os-release'), @('uname', '-a'), @('nproc'), @('free', '-m'), @('getenforce'),
    @('podman', 'version'), @('rpm', '-q', 'podman', 'netavark', 'aardvark-dns', 'firewalld', 'nftables', 'kernel'),
    @('systemctl', 'is-enabled', 'netavark-firewalld-reload.service'))
$roundsPath = Join-Path $OutputPath 'rounds.jsonl'
Write-Step "Restarting JIM $Rounds times, $GapSeconds s apart; writing to $OutputPath"

$script:previousAddresses = [System.Collections.Generic.List[string]]::new()
$baselineSaved = $false
$failed = 0
for ($round = 1; $round -le $Rounds; $round++) {
    $since = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 1
    Invoke-Native @('systemctl', 'stop', 'jim.service', 'jim-database.service') | Out-Null
    Start-Sleep -Seconds $GapSeconds
    $started = Get-Date
    Invoke-Native @('systemctl', 'start', 'jim-database.service', 'jim.service') | Out-Null

    $deadline = $started.AddSeconds($ReadyTimeoutSeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-JimReady) { $ready = $true; break }
        Start-Sleep -Seconds 2
    }
    $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)

    $snapshot = Get-NetworkSnapshot -Since $since
    [ordered]@{ Round = $round; Ready = $ready; Seconds = $seconds; Snapshot = $snapshot } |
        ConvertTo-Json -Depth 5 -Compress | Add-Content $roundsPath
    $resolvedText = if ($snapshot.ResolvedAddresses.Count) { $snapshot.ResolvedAddresses -join ', ' } else { 'nothing' }

    if ($ready) {
        Write-Step "round ${round}: ready in $seconds s; database at $($snapshot.DatabaseAddress), resolved to $resolvedText"
        if (-not $baselineSaved) {
            Save-Capture -Folder (Join-Path $OutputPath ('round-{0:d2}-baseline' -f $round)) -Snapshot $snapshot -Since $since | Out-Null
            $baselineSaved = $true
        }
    }
    else {
        $failed++
        $folder = Join-Path $OutputPath ('round-{0:d2}-failed' -f $round)
        Write-Step "round ${round}: NOT READY after $seconds s; database at $($snapshot.DatabaseAddress), resolved to $resolvedText. Capturing to $folder"
        $findings = @(Save-Capture -Folder $folder -Snapshot $snapshot -Since $since)
        if ($findings.Count -eq 0) {
            Write-Host '  Nothing abnormal found in the network; see the capture, the containers'' logs above all.'
        }
        foreach ($finding in $findings) { Write-Host "  - $finding" }
        if (-not $Continue) {
            Write-Step "Stopped with JIM as it failed, for inspection. Bring it back with: systemctl stop jim.service jim-database.service && systemctl start jim-database.service jim.service"
            exit 1
        }
    }
    if ($snapshot.DatabaseAddress) { $script:previousAddresses.Add($snapshot.DatabaseAddress) }
}

Write-Step "$Rounds rounds, $failed failed. Rounds in $roundsPath"
if ($failed -gt 0) { exit 1 }
