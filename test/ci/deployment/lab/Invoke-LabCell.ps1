# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Runs one cell of the deployment lab's matrix on a fresh VM, from the lab host, and records each check's result.

.DESCRIPTION
    Invoke-DeploymentLab.ps1 starts one of these per cell; run it alone to repeat a single cell. Every cell starts the
    same way:

      1. Creates the cell's VM from its image, on its network, and on the air-gapped network proves the VM cannot
         reach the internet.
      2. Carries the release bundle in, and checks it against its published checksum and its own checksums.sha256,
         as the installation guide tells a customer to.
      3. Carries in PowerShell, the repository's deployment checks and the documentation they run commands from.

    Then it runs its scenario, from scenarios/<Scenario>.ps1 (README.md says what each checks), and every scenario
    ends by checking that nothing crashed: no container has restarted, and the kernel has logged no fault or container
    AppArmor denial since the last boot (Select-KernelFault.ps1).

    Results go to results.json in -OutputPath, with the cell's log (cell.log) and everything else the scenario saves.
    The VM, and an ExternalDatabase cell's database server, are deleted afterwards, unless -KeepVm says otherwise.

.PARAMETER CellName
    The cell, as Cells.psd1 names it.

.PARAMETER BundlePath
    The release bundle's archive, jim-release-<version>.tar.gz, with its .sha256 beside it.

.PARAMETER PreviousBundlePath
    For an Upgrade cell: the archive of the release it upgrades from.

.PARAMETER PayloadPath
    The archive of the repository's deployment checks and documentation that Invoke-DeploymentLab.ps1 makes.

.PARAMETER PowerShellPath
    PowerShell's Linux archive, to install on the VM.

.PARAMETER OutputPath
    The cell's own folder for its results and logs.

.PARAMETER KeepVm
    Never (the default) deletes the VM afterwards; OnFailure keeps it when a check failed, for inspection; Always
    keeps it.
#>
param(
    [Parameter(Mandatory)]
    [string]$CellName,

    [Parameter(Mandatory)]
    [string]$BundlePath,

    [string]$PreviousBundlePath,

    [Parameter(Mandatory)]
    [string]$PayloadPath,

    [Parameter(Mandatory)]
    [string]$PowerShellPath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [ValidateSet('Never', 'OnFailure', 'Always')]
    [string]$KeepVm = 'Never'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DeploymentLab.psm1') -Force

$lab = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'Lab.psd1')
$matrix = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'Cells.psd1')
$cell = $matrix.Cells | Where-Object { $_.Name -eq $CellName }
if (-not $cell) {
    throw "No cell named $CellName in Cells.psd1"
}
$cell.Os = $matrix.Images[$cell.Image].Os
$rootless = $cell.ContainsKey('Rootless') -and $cell.Rootless
$ubuntu = $cell.Os -like 'Ubuntu*'
$network = $lab.Networks[$cell.Network]
if ($cell.Scenario -eq 'Upgrade' -and -not $PreviousBundlePath) {
    throw "$CellName is an Upgrade cell, which needs -PreviousBundlePath"
}

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path
$log = Join-Path $OutputPath 'cell.log'
$vm = "jl-$CellName"
$hostName = $lab.HostName
# The lab's folder, for the scenarios, which are dot-sourced from scenarios/ and so see that as their own.
$labRoot = $PSScriptRoot
$authority = "http://$($network.Gateway):$($lab.IdentityProvider.Port)/realms/$($lab.IdentityProvider.Realm)"
$docs = Join-Path $PSScriptRoot '..' '..' '..' '..' 'docs' 'administration'
$getCommand = Join-Path $PSScriptRoot '..' 'Get-DocumentedCommand.ps1'
$started = Get-Date
$checks = [System.Collections.Generic.List[object]]::new()
$cellError = $null
$bundleName = Split-Path $BundlePath -Leaf
# Set once JIM is installed: where the lab host reaches it, and the installer's certificate authority.
$address = $null
$caPath = Join-Path $OutputPath 'ca.crt'
# An ExternalDatabase cell's PostgreSQL server, a VM of its own, which the scenario sets up.
$databaseVm = $null

#region What every scenario uses

function Write-CellLog {
    param([string]$Message)
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $Message"
    Add-Content -LiteralPath $log -Value $line
    Write-Host $line
}

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    Write-CellLog "$(if ($Passed) { 'PASS' } else { 'FAIL' })  $Name$(if ($Detail) { ": $Detail" })"
}

function Invoke-Guest {
    # One of guest/Invoke-LabGuestCommand.ps1's queries, on the VM; a query of JIM's database goes to its own server,
    # if it has one.
    param([string]$Action, [string]$Sql)
    if ($Action -eq 'Sql' -and $databaseVm) {
        return Invoke-LabVm -Name $databaseVm -Command @('runuser', '-u', 'postgres', '--', 'psql', '-d', 'jim', '-v',
            'ON_ERROR_STOP=1', '-Atc', $Sql)
    }
    $command = @('pwsh', '-NoProfile', '-File', '/root/jim/test/ci/deployment/lab/guest/Invoke-LabGuestCommand.ps1',
        '-Action', $Action, '-Runtime', $cell.Runtime)
    if ($rootless) {
        $command += '-Rootless'
    }
    if ($databaseVm) {
        $command += '-ExternalDatabase'
    }
    if ($Sql) {
        $command += @('-Sql', $Sql)
    }
    Invoke-LabVm -Name $vm -Command $command -Environment @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; NO_COLOR = '1' }
}

function Get-DocumentedCommand {
    param([string]$Page, [string]$Heading, [string]$Step, [string]$Tab, [int]$Block = 1)
    $arguments = @{ Path = (Join-Path $docs $Page); Heading = $Heading; Block = $Block }
    if ($Step) { $arguments.Step = $Step }
    if ($Tab) { $arguments.Tab = $Tab }
    & $getCommand @arguments
}

function Invoke-Documented {
    # Runs a command the documentation gives, as written, as root on the VM, in bash. For a rootless JIM it is
    # rewritten as Running on Podman says (jim-systemctl and jim-podman), with those helpers defined as that page
    # defines them, unless -Rootful (a change to the host itself). Returns the exit code; output goes to cell.log.
    param([string]$Command, [string]$WorkingDirectory = '/root', [string]$Title, [switch]$Rootful)
    $script = $Command
    if ($rootless -and -not $Rootful) {
        $helpers = Get-DocumentedCommand -Page 'podman.md' -Heading 'Rootless commands'
        $script = $helpers + "`n" + (ConvertTo-LabRootlessCommand -Command $Command)
    }
    Write-CellLog "running $Title, as documented:"
    $Command -split "`n" | ForEach-Object { Write-CellLog "    $_" }
    Invoke-LabVm -Name $vm -Command @('bash', '-ec', $script) -WorkingDirectory $WorkingDirectory -AllowFailure -LogPath $log | Out-Null
    $LASTEXITCODE
}

function Wait-Ready {
    # Polled, not asked once: Podman's own health check for jim-web is liveness only, so every container can be
    # healthy before JIM is ready.
    param([int]$TimeoutSeconds = 300)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $waited = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Date) -lt $deadline) {
        $answer = $null | & curl -sS --max-time 5 --noproxy $hostName --cacert $caPath --resolve "${hostName}:443:$address" `
            "https://$hostName/api/v1/health/ready" 2>$null
        if ("$answer" -match '"ready"') {
            return [int]$waited.Elapsed.TotalSeconds
        }
        Start-Sleep -Seconds 5
    }
    -1
}

function Get-JimVersion {
    $answer = $null | & curl -sS --max-time 10 --noproxy $hostName --cacert $caPath --resolve "${hostName}:443:$address" `
        "https://$hostName/api/v1/health/version" 2>$null
    try { ("$answer" | ConvertFrom-Json).version } catch { "$answer" }
}

function Test-ReadyCheck {
    # -Trusting names the certificate authority in $caPath, for the check's name.
    param([string]$When, [string]$Trusting = "the installer's CA")
    $seconds = Wait-Ready
    Add-Check "${When}: ready over HTTPS from another machine, trusting only $Trusting" ($seconds -ge 0) `
        $(if ($seconds -lt 0) { 'not within 300 s' } else { '' })
    $seconds -ge 0
}

function Test-VersionCheck {
    # By release number: a lab build reports its VERSION, while its bundle is labelled <version>-lab.<commit>.
    param([string]$When, [string]$Expected)
    $reported = Get-JimVersion
    $same = try {
        (Test-LabVersionAtMost -Version $reported -Limit $Expected) -and (Test-LabVersionAtMost -Version $Expected -Limit $reported)
    }
    catch { $false }
    Add-Check "${When}: JIM reports version $Expected" $same "reports '$reported'"
}

function Test-SignInCheck {
    param([string]$When)
    $signIn = Invoke-LabSignIn -Address $address -HostName $hostName -CaPath $caPath `
        -User $lab.IdentityProvider.User -Password $lab.IdentityProvider.Password
    Add-Check "${When}: the initial administrator signs in through the identity provider" $signIn.Passed $signIn.Detail
    try {
        $administrators = Invoke-Guest -Action Sql -Sql 'SELECT count(*) FROM "MetaverseObjectRole" mr JOIN "Roles" r ON r."Id" = mr."RolesId" WHERE r."Name" = ''Administrator'''
        Add-Check "${When}: JIM holds exactly one Administrator" ("$administrators" -eq '1') "found $administrators"
    }
    catch {
        Add-Check "${When}: JIM holds exactly one Administrator" $false "$($_.Exception.Message)"
    }
}

function Test-StabilityCheck {
    # A crashing service is restarted and soon looks healthy again, so readiness alone cannot show it (#1953).
    $restarts = Invoke-Guest -Action Restarts
    Add-Check 'no container has restarted' ("$restarts" -eq '0') "$restarts restarts"
    $kernel = Invoke-LabVm -Name $vm -Command @('journalctl', '-k', '-b', '--no-pager', '--output', 'short-iso')
    $faults = @($kernel | & (Join-Path $PSScriptRoot '..' 'Select-KernelFault.ps1'))
    Add-Check 'since the last boot, no kernel fault and no AppArmor denial of a container' ($faults.Count -eq 0) `
        "$($faults | Select-Object -First 2)"
}

function Copy-Bundle {
    # Carries a bundle into the VM and extracts it; returns the folder it extracted to.
    param([string]$Path)
    $name = Split-Path $Path -Leaf
    Copy-LabFile -Source $Path -Destination "${vm}:/root/$name"
    Invoke-LabVm -Name $vm -Command @('tar', '-C', '/root', '-xzf', "/root/$name") | Out-Null
    '/root/' + ($name -replace '\.tar\.gz$', '')
}

function Get-BundleVersion {
    param([string]$Path)
    (Split-Path $Path -Leaf) -replace '^jim-release-', '' -replace '\.tar\.gz$', ''
}

function Get-BundleModule {
    # The JIM PowerShell module from a bundle, extracted on the lab host for the checks run from it; returns its
    # manifest's path.
    param([string]$Path)
    $folder = Join-Path $OutputPath "module-$(Get-BundleVersion $Path)"
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    & tar -xzf $Path -C $folder --wildcards '*/powershell/JIM/*' --strip-components=2
    if ($LASTEXITCODE -ne 0) {
        throw "Could not take the PowerShell module out of $(Split-Path $Path -Leaf)"
    }
    Join-Path $folder 'JIM' 'JIM.psd1'
}

function New-SetupSettings {
    # setup.sh's settings for an unattended installation, as a customer gives them: the lab's identity provider, the
    # bundled database, a certificate for the lab's host name and the VM's address, and the firewall and AppArmor
    # offers accepted. -Extra adds to them or replaces them.
    param([hashtable]$Extra = @{})
    $settings = @{
        JIM_SSO_AUTHORITY = $authority; JIM_SSO_CLIENT_ID = 'jim-web'; JIM_SSO_SECRET = 'jim-dev-secret'
        JIM_SSO_API_SCOPE = 'jim-api'; JIM_SSO_CLAIM_TYPE = 'sub'; JIM_SSO_MV_ATTRIBUTE = 'Subject Identifier'
        JIM_SSO_INITIAL_ADMIN = '00000000-0000-0000-0000-000000000001'; JIM_SETUP_DB_MODE = 'bundled'; JIM_WEB_PORT = '443'
        JIM_SETUP_TLS_MODE = 'generate'; JIM_SETUP_TLS_NAMES = "$hostName,$address"; JIM_TRUSTED_PROXIES = ''
        JIM_SETUP_AUTO_START = 'true'; JIM_SETUP_RUNTIME = $cell.Runtime; JIM_SETUP_PODMAN_ROOTLESS = "$($rootless.ToString().ToLower())"
        JIM_SETUP_FIX_APPARMOR = 'true'; JIM_SETUP_OPEN_FIREWALL = 'true'
    }
    foreach ($key in $Extra.Keys) {
        $settings[$key] = $Extra[$key]
    }
    $settings
}

function Invoke-Setup {
    # Runs a bundle's setup.sh on the VM, as root, with the settings given, and returns its exit code and output. Each
    # line of -Answer answers one of its questions in turn; with none, its standard input is closed, so every question
    # takes its default. The output also goes to -Log in the cell's folder.
    param([string]$Folder, [hashtable]$Settings = @{}, [string[]]$Argument = @(), [string[]]$Answer = @(), [string]$Log)
    $command = if ($Answer) {
        @('bash', '-c', "printf '%s\n' `"`$@`" | ./setup.sh $($Argument -join ' ')", 'setup') + $Answer
    }
    else {
        @('./setup.sh') + $Argument
    }
    Write-CellLog "running $Folder/setup.sh $Argument ($Log)"
    $output = Invoke-LabVm -Name $vm -WorkingDirectory $Folder -Environment $Settings -Command $command -AllowFailure `
        -LogPath (Join-Path $OutputPath $Log)
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output) }
}

function Get-Fingerprint {
    @(Invoke-Guest -Action Fingerprint)
}

function Test-UnchangedCheck {
    # That the installation is as its fingerprint was: every file, and every JIM container running.
    param([string]$Name, [string[]]$Before)
    $difference = @(Compare-LabFingerprint -Before $Before -After (Get-Fingerprint))
    Add-Check $Name ($difference.Count -eq 0) "$($difference | Select-Object -First 4)"
}

function Invoke-DocumentedStart {
    # Starts JIM as the documentation says: on Docker, as installing by hand does (without the bundled database's
    # profile for a server of JIM's own, as it says); on Podman, as Operating JIM does. Returns the exit code.
    if ($cell.Runtime -eq 'docker') {
        $start = Get-DocumentedCommand -Page 'deployment.md' -Heading 'By Hand' -Block 3
        if ($databaseVm) {
            $start = $start -replace '--profile with-db\s*', ''
        }
    }
    else {
        $start = (Get-DocumentedCommand -Page 'podman.md' -Heading 'Operating JIM') -split "`n" |
            Where-Object { $_ -match 'systemctl restart jim\.service' } | Select-Object -First 1
        if (-not $start) {
            throw 'Operating JIM no longer gives systemctl restart jim.service'
        }
    }
    Invoke-Documented -Command $start -WorkingDirectory '/opt/jim' -Title 'starting JIM'
}

#endregion

try {
    Write-CellLog "== $CellName ($($cell.Os), $($cell.Runtime)$(if ($rootless) { ' rootless' }), $($cell.Network), $($cell.Scenario)), bundle $bundleName$(if ($PreviousBundlePath) { ", from $(Split-Path $PreviousBundlePath -Leaf)" })"

    # 1. A fresh VM; on the air-gapped network, proof that it is.
    New-LabVm -Name $vm -Image $matrix.Images[$cell.Image].Image -Profile @($lab.BaseProfile, $network.Profile) `
        -MemoryGiB $cell.MemoryGiB
    $address = Get-LabVmAddress -Name $vm -Gateway $network.Gateway
    Write-CellLog "VM $vm is up at $address"
    if ($cell.Network -eq 'airgap') {
        Invoke-LabVm -Name $vm -Command @('curl', '-sS', '--max-time', '5', '-o', '/dev/null', 'https://github.com') -AllowFailure | Out-Null
        Add-Check 'no route out: github.com unreachable from the VM' ($LASTEXITCODE -ne 0)
    }

    # 2. The bundle under test, checked as the installation guide says.
    $bundleFolder = Copy-Bundle $BundlePath
    $published = "$BundlePath.sha256"
    if (Test-Path $published) {
        Copy-LabFile -Source $published -Destination "${vm}:/root/$bundleName.sha256"
        $verified = Invoke-LabVm -Name $vm -WorkingDirectory /root -Command @('sha256sum', '-c', "$bundleName.sha256") -AllowFailure
        Add-Check 'the archive matches its published checksum' ($LASTEXITCODE -eq 0) "$verified"
    }
    else {
        Add-Check 'the archive matches its published checksum' $false "no $bundleName.sha256 beside the archive"
    }
    $contents = Invoke-LabVm -Name $vm -WorkingDirectory $bundleFolder -Command @('sha256sum', '-c', '--quiet', 'checksums.sha256') -AllowFailure
    Add-Check 'every file in the bundle matches checksums.sha256' ($LASTEXITCODE -eq 0) "$($contents | Select-Object -First 3)"

    # 3. PowerShell, and the repository's deployment checks with the documentation they read.
    Copy-LabFile -Source $PowerShellPath -Destination "${vm}:/root/powershell.tar.gz"
    Copy-LabFile -Source $PayloadPath -Destination "${vm}:/root/jim.tar.gz"
    Invoke-LabVm -Name $vm -Command @('sh', '-ec', 'mkdir -p /opt/microsoft/powershell /root/jim && tar -C /opt/microsoft/powershell -xzf /root/powershell.tar.gz && chmod +x /opt/microsoft/powershell/pwsh && ln -sf /opt/microsoft/powershell/pwsh /usr/local/bin/pwsh && tar -C /root/jim -xzf /root/jim.tar.gz') | Out-Null

    . (Join-Path $PSScriptRoot 'scenarios' "$($cell.Scenario).ps1")

    Test-StabilityCheck
}
catch {
    $cellError = "$($_.Exception.Message)"
    Write-CellLog "STOPPED: $cellError"
}
finally {
    $failed = $cellError -or ($checks | Where-Object { -not $_.Passed }) -or $checks.Count -eq 0
    # A step that failed and stopped the scenario is recorded as a check, which says what failed, not as an error.
    if ($cellError -like 'stopped: *') {
        $cellError = $null
    }
    [pscustomobject]@{
        Cell = $cell
        Checks = $checks
        Minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
        Error = $cellError
        Bundle = $bundleName
        PreviousBundle = if ($PreviousBundlePath) { Split-Path $PreviousBundlePath -Leaf }
        Vm = $vm
    } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputPath 'results.json')

    $vms = @($vm) + @($databaseVm | Where-Object { $_ })
    if ($KeepVm -eq 'Always' -or ($KeepVm -eq 'OnFailure' -and $failed)) {
        Write-CellLog "kept VM $($vms -join ' and ')"
    }
    else {
        $vms | ForEach-Object { Remove-LabVm -Name $_ }
    }
    Write-CellLog "== ${CellName}: $(@($checks | Where-Object { $_.Passed }).Count) of $($checks.Count) checks passed$(if ($cellError) { "; stopped: $cellError" })"
}
exit $(if ($failed) { 1 } else { 0 })
