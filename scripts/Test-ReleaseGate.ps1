# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Refuses a release commit that has not passed one release gate: exits 0 only when the commit's commit status for
    that gate is `success`.

.DESCRIPTION
    Every release gate is a row of scripts/ReleaseGates.psd1: the commit status a test run posts on the SHA it tested
    (`jim-ad-lab`, `jim-ad-lab-adfs`, `jim-pre-release`, `jim-deploy-lab`), the workflow that posts it, and the remedy
    for a refusal. This script reads that status for the SHA that is about to be tagged and passes only when its state
    is `success`. Any other outcome exits 1 with a message that names what was found (the state, or "not reported"),
    the run that produced it, and the remedy.

    It is called by release.yml's release-gates job, once for every row (which validate needs), and by the /release
    skill before it tags.

    THERE IS NO OVERRIDE in this script. -Context accepts only the table's gates, and release.yml checks every one of
    them, so it can only point the script at another gate a release already needs. No parameter, environment variable
    or file skips, softens or accepts a stale result, by design (whether release.yml and /release call a gate at all
    is its repository variable, switched on once its run has passed for the first time). Failing to read the status
    (gh missing, no access, the commit unknown to GitHub) also exits 1, because a gate that opens when it cannot see is
    not a gate.

    The evaluation (Test-ReleaseGateResult) is separate from the gh call so it is tested without either; dot-sourcing
    this file defines the functions and runs nothing. Both are thin wrappers over the shared core in
    scripts/ReleaseGate.ps1.

.PARAMETER Context
    The gate: a context in scripts/ReleaseGates.psd1, such as jim-ad-lab.

.PARAMETER Sha
    The full 40-character SHA of the commit that will be tagged (git rev-parse origin/main after the release pull
    request merges).

.PARAMETER Repository
    OWNER/NAME of the repository. Default TetronIO/JIM.

.EXAMPLE
    pwsh -File ./scripts/Test-ReleaseGate.ps1 -Context jim-ad-lab -Sha (git rev-parse origin/main)
#>

[CmdletBinding()]
param(
    [string]$Context,

    [string]$Sha,

    [string]$Repository = 'TetronIO/JIM'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ReleaseGate.ps1')

function Test-ReleaseGateResult {
    <#
    .SYNOPSIS
        Decides whether a commit's statuses say one gate passed: Test-ReleaseGateStatus for the gate's context, with its
        remedy. Returns Passed, State, Description, TargetUrl and Message.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$Context,

        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Statuses,

        [string]$Sha = 'the commit'
    )

    $gate = Get-ReleaseGateDefinition -Context $Context
    return Test-ReleaseGateStatus -Statuses $Statuses -Context $gate.Context -Sha $Sha -Remedy ($gate.Remedy -f $Sha) -NotReportedReason $gate.NotReportedReason
}

# Dot-sourced (by the tests): the functions above are all that is wanted.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

try {
    $gate = Get-ReleaseGateDefinition -Context $Context
}
catch {
    Write-Host "Release gate: $($_.Exception.Message)" -ForegroundColor Red
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host "::error title=Release gate::$($_.Exception.Message)"
    }
    exit 1
}

exit (Invoke-ReleaseGate -Context $gate.Context -Sha $Sha -Repository $Repository -Remedy ($gate.Remedy -f $Sha) -NotReportedReason $gate.NotReportedReason)
