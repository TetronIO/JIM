# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Starts the Remote PowerShell harness for Tetron.PowerShellRemoting and prints the settings its tests need.

.DESCRIPTION
    Brings up the harness's Kerberos realm (HARNESS.TEST, Samba AD as the KDC) from docker-compose.yml, then makes it
    usable from the host:

    - Rebinds Samba to the container's interface. Provisioning records the interface as eth0@ifNN, which no longer
      matches once the container restarts, and Samba then listens on loopback only.
    - Creates the client account (svc-winrm) and the WinRM service account (winrm-harness) with its SPNs
      HTTP/winrm-harness.harness.test and HTTP/winrm-harness, if they do not exist.
    - Exports the service's keytab to -OutputDirectory.

    Safe to re-run. The harness proves Tetron.PowerShellRemoting against an independent implementation, not against
    Windows; the Active Directory lab does that (#2021, spike Phase 1b).

.PARAMETER OutputDirectory
    Where to write the service keytab. Defaults to a folder under the system temporary directory.

.PARAMETER Down
    Stops the harness and removes its containers and volumes.

.EXAMPLE
    ./test/integration/remoting-harness/Start-RemotingHarness.ps1
    Starts the harness and prints the JIM_REMOTING_HARNESS_* variables to export before running the tests.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'jim-remoting-harness'),
    [switch]$Down
)

$ErrorActionPreference = 'Stop'
$composeFile = Join-Path $PSScriptRoot 'docker-compose.yml'
$container = 'remoting-harness-kdc'

function Invoke-Samba {
    param([Parameter(Mandatory)][string]$Command)
    $output = docker exec $container bash -c "export PATH=/usr/local/samba/bin:`$PATH; $Command" 2>&1
    return @($output | ForEach-Object { "$_" })
}

function Get-KdcAddress {
    return (docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' $container).Trim()
}

function Test-KdcPort {
    param([string]$Address)
    $client = [Net.Sockets.TcpClient]::new()
    try {
        return $client.ConnectAsync($Address, 88).Wait(2000) -and $client.Connected
    }
    catch [AggregateException] {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

if ($Down) {
    docker compose -f $composeFile down -v
    Write-Host 'Remoting harness stopped and its volumes removed.'
    return
}

docker compose -f $composeFile up -d kdc | Out-Null

Write-Host 'Waiting for the KDC to finish provisioning...'
$deadline = (Get-Date).AddMinutes(20)
while ((Invoke-Samba 'samba-tool user list 2>/dev/null') -notcontains 'krbtgt') {
    if ((Get-Date) -gt $deadline) { throw 'The KDC did not finish provisioning within 20 minutes.' }
    Start-Sleep -Seconds 10
}

$binding = Invoke-Samba 'grep -E "^\s*interfaces\s*=" /usr/local/samba/etc/smb.conf'
if (($binding -join '') -match 'eth0@if') {
    Write-Host 'Rebinding Samba to eth0 and restarting the KDC...'
    Invoke-Samba "sed -i 's/interfaces = lo eth0@if[0-9]*/interfaces = lo eth0/' /usr/local/samba/etc/smb.conf" | Out-Null
    docker restart $container | Out-Null
}

$deadline = (Get-Date).AddMinutes(5)
while (-not (Test-KdcPort (Get-KdcAddress))) {
    if ((Get-Date) -gt $deadline) { throw 'The KDC did not start listening on port 88 within 5 minutes.' }
    Start-Sleep -Seconds 3
}

$users = Invoke-Samba 'samba-tool user list'
Invoke-Samba 'samba-tool domain passwordsettings set --complexity=off --min-pwd-length=1' | Out-Null
if ($users -notcontains 'svc-winrm') {
    Invoke-Samba "samba-tool user create svc-winrm 'Client@123!'" | Out-Null
    Invoke-Samba 'samba-tool user setexpiry svc-winrm --noexpiry' | Out-Null
}
if ($users -notcontains 'winrm-harness') {
    Invoke-Samba "samba-tool user create winrm-harness 'Service@123!'" | Out-Null
    Invoke-Samba 'samba-tool user setexpiry winrm-harness --noexpiry' | Out-Null
}
$spns = Invoke-Samba 'samba-tool spn list winrm-harness'
foreach ($spn in 'HTTP/winrm-harness.harness.test', 'HTTP/winrm-harness') {
    if (-not ($spns -match [regex]::Escape($spn))) {
        Invoke-Samba "samba-tool spn add $spn winrm-harness" | Out-Null
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$keytab = Join-Path $OutputDirectory 'winrm-harness.keytab'
Invoke-Samba 'rm -f /tmp/winrm-harness.keytab && samba-tool domain exportkeytab /tmp/winrm-harness.keytab --principal=HTTP/winrm-harness.harness.test' | Out-Null
docker cp -q "${container}:/tmp/winrm-harness.keytab" $keytab
if (-not $IsWindows) { chmod 600 $keytab }

$address = Get-KdcAddress
Write-Host ''
Write-Host 'Remoting harness ready. Export these before running Tetron.PowerShellRemoting.Tests:'
Write-Host "  JIM_REMOTING_HARNESS_KDC=$address"
Write-Host "  JIM_REMOTING_HARNESS_KEYTAB=$keytab"
[pscustomobject]@{ Kdc = $address; Keytab = $keytab }
