# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Registers the scheduled task that keeps the lab's deployed scripts and JIM PowerShell module at the head of main
    (Update-LabScripts.ps1), hourly.

.DESCRIPTION
    Run once, from an elevated PowerShell 7 session on the Hyper-V host, after Install-LabScripts.ps1 has deployed
    Update-LabScripts.ps1 to <ScriptRoot>. Idempotent: it replaces a task of the same name.

    The task, \JIM\Update AD lab scripts, runs Update-LabScripts.ps1 as SYSTEM every hour from the next whole hour, so
    the host's copy is never more than an hour behind main, the 01:23 UTC nightly included whatever the clocks do. It
    needs no stored password (SYSTEM, and the repository is public). A run that overlaps the last is skipped, a run
    missed while the host was off runs when it starts, and each is stopped after 15 minutes.

    SYSTEM here runs main's lab scripts on the hypervisor host unattended: the same code an operator would deploy by
    hand from main, which only reaches main through a reviewed pull request.

.PARAMETER ScriptRoot
    Where the lab scripts are deployed. Default C:\jim-ad-lab.

.PARAMETER TaskName
    Default 'Update AD lab scripts', under the \JIM\ folder.

.EXAMPLE
    C:\jim-ad-lab\Register-LabScriptsUpdateTask.ps1
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing output.')]
[CmdletBinding()]
param(
    [string]$ScriptRoot = 'C:\jim-ad-lab',

    [string]$TaskName = 'Update AD lab scripts'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script = Join-Path $ScriptRoot 'Update-LabScripts.ps1'
if (-not (Test-Path -LiteralPath $script)) {
    throw "$script does not exist. Run Install-LabScripts.ps1 from a checkout of main first."
}
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source

$start = (Get-Date).Date.AddHours((Get-Date).Hour + 1).AddMinutes(5)
$trigger = New-ScheduledTaskTrigger -Once -At $start -RepetitionInterval (New-TimeSpan -Hours 1)
$action = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -NonInteractive -File `"$script`""
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 15)

$task = Register-ScheduledTask -TaskPath '\JIM\' -TaskName $TaskName -Trigger $trigger -Action $action -Principal $principal -Settings $settings `
    -Description 'Keeps the AD lab scripts and JIM PowerShell module at the head of main (test/integration/ad-lab/README.md).' -Force
Write-Host "Registered \JIM\${TaskName}: $pwsh $script, hourly from $($start.ToString('HH:mm')), as SYSTEM" -ForegroundColor Green
Write-Host "Log: $(Join-Path $ScriptRoot 'logs\update-lab-scripts.log'). Run it now with: Start-ScheduledTask -TaskPath '\JIM\' -TaskName '$TaskName'"
$null = $task
