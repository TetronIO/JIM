# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Registers the scheduled task that starts the nightly Active Directory lab run at 01:23 UTC (Start-LabNightly.ps1).

.DESCRIPTION
    Run once, from an elevated PowerShell 7 session on the Hyper-V host, after Install-LabScripts.ps1 has deployed
    Start-LabNightly.ps1 to <ScriptRoot> and Start-LabNightly.ps1 -SetToken has stored the dispatch token. Idempotent: it
    replaces a task of the same name.

    The task, \JIM\Start AD lab nightly, runs Start-LabNightly.ps1 as SYSTEM daily at 01:23 UTC. The trigger is in UTC
    (a "Z" start boundary), so it stays at 01:23 UTC when the host's clock changes for summer time, as ad-lab.yml's cron
    does. A run missed while the host was off runs when it starts (the script then starts the nightly only if none has
    started that day), and each is stopped after 10 minutes.

.PARAMETER ScriptRoot
    Where the lab scripts are deployed. Default C:\jim-ad-lab.

.PARAMETER TaskName
    Default 'Start AD lab nightly', under the \JIM\ folder.

.EXAMPLE
    C:\jim-ad-lab\Register-LabNightlyTask.ps1
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing output.')]
[CmdletBinding()]
param(
    [string]$ScriptRoot = 'C:\jim-ad-lab',

    [string]$TaskName = 'Start AD lab nightly'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script = Join-Path $ScriptRoot 'Start-LabNightly.ps1'
if (-not (Test-Path -LiteralPath $script)) {
    throw "$script does not exist. Run Install-LabScripts.ps1 from a checkout of main first."
}
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source

$trigger = New-ScheduledTaskTrigger -Daily -At ([DateTime]::Today.AddHours(1).AddMinutes(23))
# 01:23 UTC, whatever the host's time zone: a start boundary ending in Z is synchronised across time zones.
$trigger.StartBoundary = [DateTime]::UtcNow.Date.AddDays(1).AddHours(1).AddMinutes(23).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
$action = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -NonInteractive -File `"$script`""
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

$task = Register-ScheduledTask -TaskPath '\JIM\' -TaskName $TaskName -Trigger $trigger -Action $action -Principal $principal -Settings $settings `
    -Description 'Starts the nightly AD lab run (ad-lab.yml on main) at 01:23 UTC (test/integration/ad-lab/README.md).' -Force
Write-Host "Registered \JIM\${TaskName}: $pwsh $script, daily at 01:23 UTC, as SYSTEM" -ForegroundColor Green
Write-Host "Log: $(Join-Path $ScriptRoot 'logs\start-lab-nightly.log'). Check it without starting anything: $script -WhatIf"
$null = $task
