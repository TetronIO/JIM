# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The Soak scenario, dot-sourced by Invoke-LabCell.ps1 once the VM is up and the bundle checked: JIM restarted many
# times as Operating JIM says, then the host rebooted several times, JIM coming back by itself each time with its data.
# A fault that strikes one restart in a few passes a single restart most of the time: #2009 (NetworkManager unplugging
# rootful Podman pods from their bridge) failed 6 of 20 restarts, and so passed a one-restart check about two runs in
# three. A soak cell runs on request (OnRequest in Cells.psd1): after the lab's images
# are rebuilt, when operating system updates arrive, and on a release candidate. See README.md, "What a Soak cell
# checks".

if ($cell.Runtime -ne 'podman') {
    throw "a Soak cell restarts JIM as Operating JIM says for Podman; a $($cell.Runtime) soak needs that runtime's own restart"
}

# Scenarios share Invoke-LabCell.ps1's scope, so these names must not be its own (it times the cell by $started).
$installPath = '/opt/jim'
$restartCount = 20
$rebootCount = 5
# Once this many rounds of a loop have failed its check has failed, and a broken host would otherwise spend 300 s on
# every round left: the negative control (the #2009 fix removed) failed all 20 restarts, which took 100 minutes.
$giveUpAfter = 3

# 1. Installed, as a customer would, and reachable from another machine.
$installed = (Invoke-Setup -Folder $bundleFolder -Settings (New-SetupSettings) -Log 'setup-install.log').ExitCode -eq 0
Add-Check 'installed with setup.sh' $installed
if (-not $installed) {
    throw 'stopped: JIM did not install'
}
Copy-LabFile -Source "${vm}:$installPath/tls/ca.crt" -Destination $caPath
if (-not (Test-ReadyCheck 'installed')) {
    throw 'stopped: JIM is not ready'
}
Test-SignInCheck 'installed'
Invoke-Guest -Action Sql -Sql "CREATE SCHEMA IF NOT EXISTS jimlab; CREATE TABLE IF NOT EXISTS jimlab.marker (cell text); INSERT INTO jimlab.marker VALUES ('$CellName');" | Out-Null

function Save-SoakJournal {
    # What systemd and the kernel said during a round that failed, for working out why.
    param([string]$Round, [string]$Since)
    $journal = Invoke-LabVm -Name $vm -AllowFailure -Command @('journalctl', '--since', $Since, '--no-pager', '--output', 'short-iso')
    $journal | Set-Content (Join-Path $OutputPath "$Round-journal.log")
    $pods = Invoke-LabVm -Name $vm -AllowFailure -Command @('podman', 'ps', '-a', '--format', '{{.Names}} {{.Status}}')
    $pods | Set-Content (Join-Path $OutputPath "$Round-containers.log")
}

function Get-GuestClock {
    "$(Invoke-LabVm -Name $vm -Command @('date', '-u', '+%Y-%m-%d %H:%M:%S'))".Trim() + ' UTC'
}

# 2. Restarted, again and again, as Operating JIM says.
$failedRestarts = [System.Collections.Generic.List[int]]::new()
$slowest = 0
for ($round = 1; $round -le $restartCount -and $failedRestarts.Count -lt $giveUpAfter; $round++) {
    $since = Get-GuestClock
    $restartedOk = (Invoke-DocumentedStart) -eq 0
    $seconds = if ($restartedOk) { Wait-Ready } else { -1 }
    Write-CellLog "restart $round of ${restartCount}: $(if ($seconds -ge 0) { "ready after $seconds s" } else { 'not ready within 300 s' })"
    if ($seconds -lt 0) {
        $failedRestarts.Add($round)
        Save-SoakJournal -Round "restart-$round" -Since $since
    }
    $slowest = [math]::Max($slowest, $seconds)
}
Add-Check "restarted $restartCount times as Operating JIM says: ready again within 300 s every time" ($failedRestarts.Count -eq 0) `
    $(if ($failedRestarts.Count) { "not ready after restart $($failedRestarts -join ', ')$(if ($failedRestarts.Count -ge $giveUpAfter) { ', so the rest were skipped' })" } else { "slowest $slowest s" })
$marker = Invoke-Guest -Action Sql -Sql 'SELECT cell FROM jimlab.marker;'
Add-Check "after $restartCount restarts: the data kept" ("$marker" -eq $CellName) "found '$marker'"
Test-SignInCheck "after $restartCount restarts"

# 3. The host rebooted, again and again: JIM starts by itself each time, with its data.
$failedReboots = [System.Collections.Generic.List[int]]::new()
$slowest = 0
for ($round = 1; $round -le $rebootCount -and $failedReboots.Count -lt $giveUpAfter; $round++) {
    $since = Get-GuestClock
    Restart-LabVm -Name $vm
    $seconds = Wait-Ready
    $marker = if ($seconds -ge 0) { "$(Invoke-Guest -Action Sql -Sql 'SELECT cell FROM jimlab.marker;')" } else { '' }
    Write-CellLog "reboot $round of ${rebootCount}: $(if ($seconds -ge 0) { "ready after $seconds s, data '$marker'" } else { 'not ready within 300 s' })"
    if ($seconds -lt 0 -or $marker -ne $CellName) {
        $failedReboots.Add($round)
        Save-SoakJournal -Round "reboot-$round" -Since $since
    }
    $slowest = [math]::Max($slowest, $seconds)
}
Add-Check "rebooted $rebootCount times: JIM started by itself every time, with its data" ($failedReboots.Count -eq 0) `
    $(if ($failedReboots.Count) { "failed after reboot $($failedReboots -join ', ')$(if ($failedReboots.Count -ge $giveUpAfter) { ', so the rest were skipped' })" } else { "slowest $slowest s" })
Test-SignInCheck "after $rebootCount reboots"
