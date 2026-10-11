# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The Install scenario, dot-sourced by Invoke-LabCell.ps1 once the VM is up and the bundle checked: the
# deployment-boot check on the cell's operating system, then from another machine, then across a host reboot. See
# README.md, "What an Install cell checks".

# The deployment-boot check, on this cell's operating system: it installs JIM with the bundle's setup.sh and checks
# everything the deployment-boot job in CI does.
$boot = @('pwsh', '-NoProfile', '-File', '/root/jim/test/ci/deployment/Invoke-DeploymentBoot.ps1',
    '-BundlePath', $bundleFolder, '-Runtime', $cell.Runtime, '-Authority', $authority, '-HostName', $hostName,
    '-OpenFirewall', '-KeepRunning', '-OutputPath', '/root/boot')
if ($rootless) {
    $boot += '-Rootless'
}
Write-CellLog 'running the deployment-boot check on the VM (boot.log)'
$bootOutput = Invoke-LabVm -Name $vm -Command $boot -Environment @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'; NO_COLOR = '1' } `
    -AllowFailure -LogPath (Join-Path $OutputPath 'boot.log')
$bootExit = $LASTEXITCODE
Copy-LabFile -Source "${vm}:/root/boot" -Destination $OutputPath -Recurse
$bootFailure = ($bootOutput | Where-Object { $_ -match 'FAILED: ' } | Select-Object -Last 1) -replace '^.*FAILED: ', ''
Add-Check "the deployment-boot check: installs with setup.sh, ready, healthy, API key, data kept through a restart, PostgreSQL sized, documented key backup and rollback, no crashes" `
    ($bootExit -eq 0) $(if ($bootExit -ne 0) { if ($bootFailure) { $bootFailure } else { "exit $bootExit; see boot.log" } })
if ($bootExit -ne 0) {
    throw 'stopped: the deployment-boot check failed, so the checks that follow would only repeat its failure'
}

# From another machine.
$leg = if ($cell.Runtime -eq 'docker') { 'docker' } elseif ($rootless) { 'podman-rootless' } else { 'podman-rootful' }
Copy-LabFile -Source "${vm}:/opt/jim-$leg/tls/ca.crt" -Destination $caPath
if (Test-ReadyCheck 'installed') {
    Test-SignInCheck 'installed'
}

# A host reboot.
Invoke-Guest -Action Sql -Sql "CREATE SCHEMA IF NOT EXISTS jimlab; CREATE TABLE IF NOT EXISTS jimlab.marker (cell text); INSERT INTO jimlab.marker VALUES ('$CellName');" | Out-Null
Write-CellLog 'rebooting the VM'
Restart-LabVm -Name $vm
if (Test-ReadyCheck 'after a reboot') {
    $marker = Invoke-Guest -Action Sql -Sql 'SELECT cell FROM jimlab.marker;'
    Add-Check 'after a reboot: JIM started by itself, with its data' ("$marker" -eq $CellName) "found '$marker'"
    Test-SignInCheck 'after a reboot'
}
