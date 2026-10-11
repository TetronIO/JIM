# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Runs one cell of the deployment lab's matrix on a fresh VM, from the lab host, and records each check's result.

.DESCRIPTION
    Invoke-DeploymentLab.ps1 starts one of these per cell; run it alone to repeat a single cell. For an Install cell:

      1. Creates the cell's VM from its image, on its network, and on the air-gapped network proves the VM cannot
         reach the internet.
      2. Carries the release bundle in, and checks it against its published checksum and its own checksums.sha256,
         as the installation guide tells a customer to.
      3. Carries in PowerShell, the deployment-boot check and the documentation it runs commands from, and runs the
         check on the VM (Invoke-DeploymentBoot.ps1), with JIM's certificate issued for the lab's host name and the
         installer's firewall offer accepted. That installs JIM with the bundle's setup.sh and checks everything the
         deployment-boot job in CI does, on this cell's operating system.
      4. From the lab host, as another machine on the network: JIM answers ready over HTTPS, trusting only the
         installer's certificate authority; the initial administrator signs in through the identity provider as a
         browser does; and JIM holds exactly one Administrator.
      5. Writes a marker to the database and reboots the VM, then checks that JIM started by itself with the marker
         kept, is ready over HTTPS, and lets the administrator sign in.
      6. Checks that no container has restarted, and that the kernel has logged no fault or container AppArmor denial
         since the reboot (Select-KernelFault.ps1); a crashing service is restarted and soon looks healthy again.

    Results go to results.json in -OutputPath, with the cell's log (cell.log), the boot check's log and output, and
    the installer's certificate authority. The VM is deleted afterwards, unless -KeepVm says otherwise.

.PARAMETER CellName
    The cell, as Cells.psd1 names it.

.PARAMETER BundlePath
    The release bundle's archive, jim-release-<version>.tar.gz, with its .sha256 beside it.

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
$network = $lab.Networks[$cell.Network]

New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path
$log = Join-Path $OutputPath 'cell.log'
$vm = "jl-$CellName"
$hostName = $lab.HostName
$started = Get-Date
$checks = [System.Collections.Generic.List[object]]::new()
$cellError = $null

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
    param([string]$Action, [string]$Sql)
    $command = @('pwsh', '-NoProfile', '-File', '/root/jim/test/ci/deployment/lab/guest/Invoke-LabGuestCommand.ps1',
        '-Action', $Action, '-Runtime', $cell.Runtime)
    if ($rootless) {
        $command += '-Rootless'
    }
    if ($Sql) {
        $command += @('-Sql', $Sql)
    }
    Invoke-LabVm -Name $vm -Command $command -Environment @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; NO_COLOR = '1' }
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

function Test-ReadyCheck {
    param([string]$When)
    $seconds = Wait-Ready
    Add-Check "${When}: ready over HTTPS from another machine, trusting only the installer's CA" ($seconds -ge 0) `
        $(if ($seconds -lt 0) { 'not within 300 s' } else { '' })
    $seconds -ge 0
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

try {
    Write-CellLog "== $CellName ($($cell.Os), $($cell.Runtime)$(if ($rootless) { ' rootless' }), $($cell.Network), $($cell.Scenario)), bundle $(Split-Path $BundlePath -Leaf)"

    # 1. A fresh VM; on the air-gapped network, proof that it is.
    New-LabVm -Name $vm -Image $matrix.Images[$cell.Image].Image -Profile @($lab.BaseProfile, $network.Profile) `
        -MemoryGiB $cell.MemoryGiB
    $address = Get-LabVmAddress -Name $vm -Gateway $network.Gateway
    Write-CellLog "VM $vm is up at $address"
    if ($cell.Network -eq 'airgap') {
        Invoke-LabVm -Name $vm -Command @('curl', '-sS', '--max-time', '5', '-o', '/dev/null', 'https://github.com') -AllowFailure | Out-Null
        Add-Check 'no route out: github.com unreachable from the VM' ($LASTEXITCODE -ne 0)
    }

    # 2. The bundle, checked as the installation guide says.
    $bundleName = Split-Path $BundlePath -Leaf
    $bundleFolder = '/root/' + ($bundleName -replace '\.tar\.gz$', '')
    Copy-LabFile -Source $BundlePath -Destination "${vm}:/root/$bundleName"
    $published = "$BundlePath.sha256"
    if (Test-Path $published) {
        Copy-LabFile -Source $published -Destination "${vm}:/root/$bundleName.sha256"
        $verified = Invoke-LabVm -Name $vm -WorkingDirectory /root -Command @('sha256sum', '-c', "$bundleName.sha256") -AllowFailure
        Add-Check 'the archive matches its published checksum' ($LASTEXITCODE -eq 0) "$verified"
    }
    else {
        Add-Check 'the archive matches its published checksum' $false "no $bundleName.sha256 beside the archive"
    }
    Invoke-LabVm -Name $vm -Command @('tar', '-C', '/root', '-xzf', "/root/$bundleName") | Out-Null
    $contents = Invoke-LabVm -Name $vm -WorkingDirectory $bundleFolder -Command @('sha256sum', '-c', '--quiet', 'checksums.sha256') -AllowFailure
    Add-Check 'every file in the bundle matches checksums.sha256' ($LASTEXITCODE -eq 0) "$($contents | Select-Object -First 3)"

    # 3. The deployment-boot check, on this cell's operating system.
    Copy-LabFile -Source $PowerShellPath -Destination "${vm}:/root/powershell.tar.gz"
    Copy-LabFile -Source $PayloadPath -Destination "${vm}:/root/jim.tar.gz"
    Invoke-LabVm -Name $vm -Command @('sh', '-ec', 'mkdir -p /opt/microsoft/powershell /root/jim && tar -C /opt/microsoft/powershell -xzf /root/powershell.tar.gz && chmod +x /opt/microsoft/powershell/pwsh && ln -sf /opt/microsoft/powershell/pwsh /usr/local/bin/pwsh && tar -C /root/jim -xzf /root/jim.tar.gz') | Out-Null

    $authority = "http://$($network.Gateway):$($lab.IdentityProvider.Port)/realms/$($lab.IdentityProvider.Realm)"
    $boot = @('pwsh', '-NoProfile', '-File', '/root/jim/test/ci/deployment/Invoke-DeploymentBoot.ps1',
        '-BundlePath', $bundleFolder, '-Runtime', $cell.Runtime, '-Authority', $authority, '-HostName', $hostName,
        '-OpenFirewall', '-KeepRunning', '-OutputPath', '/root/boot')
    if ($rootless) {
        $boot += '-Rootless'
    }
    Write-CellLog 'running the deployment-boot check on the VM (boot.log)'
    $bootLog = Join-Path $OutputPath 'boot.log'
    $bootOutput = Invoke-LabVm -Name $vm -Command $boot -Environment @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' } `
        -AllowFailure -LogPath $bootLog
    $bootExit = $LASTEXITCODE
    Copy-LabFile -Source "${vm}:/root/boot" -Destination $OutputPath -Recurse
    $bootFailure = ($bootOutput | Where-Object { $_ -match 'FAILED: ' } | Select-Object -Last 1) -replace '^.*FAILED: ', ''
    Add-Check "the deployment-boot check: installs with setup.sh, ready, healthy, API key, data kept through a restart, PostgreSQL sized, documented key backup and rollback, no crashes" `
        ($bootExit -eq 0) $(if ($bootExit -ne 0) { if ($bootFailure) { $bootFailure } else { "exit $bootExit; see boot.log" } })
    if ($bootExit -ne 0) {
        throw 'the deployment-boot check failed, so the checks that follow would only repeat its failure'
    }

    # 4. From another machine.
    $leg = if ($cell.Runtime -eq 'docker') { 'docker' } elseif ($rootless) { 'podman-rootless' } else { 'podman-rootful' }
    $caPath = Join-Path $OutputPath 'ca.crt'
    Copy-LabFile -Source "${vm}:/opt/jim-$leg/tls/ca.crt" -Destination $caPath
    if (Test-ReadyCheck 'installed') {
        Test-SignInCheck 'installed'
    }

    # 5. A host reboot.
    Invoke-Guest -Action Sql -Sql "CREATE SCHEMA IF NOT EXISTS jimlab; CREATE TABLE IF NOT EXISTS jimlab.marker (cell text); INSERT INTO jimlab.marker VALUES ('$CellName');" | Out-Null
    Write-CellLog 'rebooting the VM'
    Restart-LabVm -Name $vm
    if (Test-ReadyCheck 'after a reboot') {
        $marker = Invoke-Guest -Action Sql -Sql 'SELECT cell FROM jimlab.marker;'
        Add-Check 'after a reboot: JIM started by itself, with its data' ("$marker" -eq $CellName) "found '$marker'"
        Test-SignInCheck 'after a reboot'
    }

    # 6. Nothing crashed and was quietly restarted.
    $restarts = Invoke-Guest -Action Restarts
    Add-Check "no container has restarted" ("$restarts" -eq '0') "$restarts restarts"
    $kernel = Invoke-LabVm -Name $vm -Command @('journalctl', '-k', '-b', '--no-pager', '--output', 'short-iso')
    $faults = @($kernel | & (Join-Path $PSScriptRoot '..' 'Select-KernelFault.ps1'))
    Add-Check 'since the reboot, no kernel fault and no AppArmor denial of a container' ($faults.Count -eq 0) `
        "$($faults | Select-Object -First 2)"
}
catch {
    $cellError = "$($_.Exception.Message)"
    Write-CellLog "STOPPED: $cellError"
}
finally {
    $failed = $cellError -or ($checks | Where-Object { -not $_.Passed }) -or $checks.Count -eq 0
    # A failed boot check is recorded as a check, not an error, since it says what failed.
    if ($cellError -like 'the deployment-boot check failed*') {
        $cellError = $null
    }
    [pscustomobject]@{
        Cell = $cell
        Checks = $checks
        Minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
        Error = $cellError
        Bundle = Split-Path $BundlePath -Leaf
        Vm = $vm
    } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputPath 'results.json')

    if ($KeepVm -eq 'Always' -or ($KeepVm -eq 'OnFailure' -and $failed)) {
        Write-CellLog "kept VM $vm"
    }
    else {
        Remove-LabVm -Name $vm
    }
    Write-CellLog "== ${CellName}: $(@($checks | Where-Object { $_.Passed }).Count) of $($checks.Count) checks passed$(if ($cellError) { "; stopped: $cellError" })"
}
exit $(if ($failed) { 1 } else { 0 })
