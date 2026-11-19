# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Makes sure this host holds the JIM PowerShell module of the commit under test, running the hourly lab scripts update
    now when it does not, so a run never waits for the update.

.DESCRIPTION
    The AD FS registration (Register-LabIdentityProvider.ps1) runs from this host's copy of the module,
    module\<ModuleDigest>\JIM, which the update task (\JIM\Update AD lab scripts, Update-LabScripts.ps1 as SYSTEM)
    deploys at the head of main. A run within the hour of a change to the module used to find no copy of it. The runner
    calls this first, over the control channel, as the jim-lab account: when the copy is missing it starts the update
    task, which Install-LabScripts.ps1 lets jim-lab run (Grant-LabScheduledTaskRun), waits for it to finish and checks
    the copy (Sync-LabModuleCopy). The update deploys only the head of main, so a run of any other commit still finds
    no copy, and is told so.

    Prints one line, AlreadyDeployed or Deployed, and exits 0; exits 1 with the reason otherwise.

.PARAMETER ModuleDigest
    Get-LabModuleDigest over the commit's src/JIM.PowerShell, as the runner computes it.

.PARAMETER TimeoutSeconds
    How long to wait for the update. Default 900.

.EXAMPLE
    .\Sync-LabModule.ps1 -ModuleDigest <digest>
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'One result line for the runner.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$ModuleDigest,

    [ValidateRange(60, 3600)]
    [int]$TimeoutSeconds = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$federationModule = @(
    (Join-Path $PSScriptRoot 'guest/LabFederationServer.psm1'),
    (Join-Path (Join-Path $PSScriptRoot '..') 'guest/LabFederationServer.psm1')
) | Where-Object { Test-Path -LiteralPath $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $federationModule) {
    [Console]::Error.WriteLine('Cannot find guest/LabFederationServer.psm1 beside or above this script; see the layout notes in README.md.')
    exit 1
}
Import-Module $federationModule -Force

$copy = Join-Path $PSScriptRoot 'module' $ModuleDigest 'JIM'
$taskPath = '\JIM\'
$taskName = 'Update AD lab scripts'

try {
    $outcome = Sync-LabModuleCopy -Digest $ModuleDigest -TimeoutSeconds $TimeoutSeconds `
        -GetDeployedDigest {
            if (Test-Path -LiteralPath $copy -PathType Container) { Get-LabModuleDigest -Entry @(Read-LabModuleFile -Root $copy) } else { $null }
        } `
        -StartUpdate { Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction Stop } `
        -GetUpdateState {
            $task = Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction Stop
            $info = $task | Get-ScheduledTaskInfo -ErrorAction Stop
            [pscustomobject]@{ Running = ("$($task.State)" -eq 'Running'); LastRunTime = $info.LastRunTime.ToUniversalTime(); LastResult = [int64]$info.LastTaskResult }
        }
    Write-Host $outcome
    exit 0
}
catch {
    [Console]::Error.WriteLine("Could not bring this host's copy of the JIM PowerShell module to $($ModuleDigest): $($_.Exception.Message)")
    exit 1
}
