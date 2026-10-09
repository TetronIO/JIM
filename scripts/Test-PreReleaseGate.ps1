# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Refuses a release commit that has not passed the pre-release integration suite: exits 0 only when the commit's
    `jim-pre-release` commit status is `success`.

.DESCRIPTION
    The pre-release integration suite (Run-IntegrationTests.ps1 -PreRelease -Parallel: every scenario against Samba
    AD, OpenLDAP and 389 Directory Server) runs in .github/workflows/pre-release.yml, which starts by itself when a
    change to VERSION reaches main (that is, when a release pull request merges) and on demand, and posts a commit
    status named `jim-pre-release` on the SHA it tested. This gate reads that status for the SHA that is about to be
    tagged and passes only when its state is `success`.

    It is called by the jim-pre-release-gate job in release.yml (which validate needs) and by the /release skill
    before it tags. Any other outcome exits 1 with a message that names what was found (the state, or "not
    reported"), the run that produced it, and the remedy: fix the cause, then run pre-release.yml on that commit.

    The status belongs to one commit: the release commit itself, the merge commit of the release pull request, is the
    one that must carry it.

    THERE IS NO OVERRIDE in this script. No parameter, environment variable or file skips, softens or accepts a stale
    result, by design (whether release.yml and /release call it at all is the JIM_PRE_RELEASE_GATE_ENFORCED
    repository variable, switched on once the suite has had its first green run). Failing to read the status also
    exits 1, because a gate that opens when it cannot see is not a gate.

    The evaluation (Test-PreReleaseStatus) is separate from the gh call, and both are thin wrappers over the shared
    release-gate core in scripts/ReleaseGate.ps1 (which scripts/Test-AdLabReleaseGate.ps1 uses too); this script
    supplies the context and the remedy, as constants. Dot-sourcing this file defines the functions and runs nothing.

.PARAMETER Sha
    The full 40-character SHA of the commit that will be tagged (git rev-parse origin/main after the release pull
    request merges).

.PARAMETER Repository
    OWNER/NAME of the repository. Default TetronIO/JIM.

.EXAMPLE
    pwsh -File ./scripts/Test-PreReleaseGate.ps1 -Sha (git rev-parse origin/main)

.EXAMPLE
    gh workflow run pre-release.yml --ref main
    # wait for the run to finish, then check again
    pwsh -File ./scripts/Test-PreReleaseGate.ps1 -Sha (git rev-parse origin/main)
#>

[CmdletBinding()]
param(
    [string]$Sha,

    [string]$Repository = 'TetronIO/JIM'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ReleaseGate.ps1')

# The status context pre-release.yml posts. A constant, not a parameter: there is nothing to configure here.
$script:PreReleaseContext = 'jim-pre-release'

function Get-PreReleaseRemedy {
    param([string]$Sha = 'the commit')
    return "There is no override: fix the cause, then run pre-release.yml on $Sha (merging the release pull request starts it; otherwise gh workflow run pre-release.yml --ref main, while $Sha is the head of main), wait for it to finish (about two hours), and check again."
}

$script:PreReleaseNotReportedReason = 'the pre-release integration suite has not run on this exact commit. It starts by itself when a change to VERSION reaches main, or on demand, so a commit that was neither has none.'

function Test-PreReleaseStatus {
    <#
    .SYNOPSIS
        Decides whether a commit's statuses say the pre-release suite passed: Test-ReleaseGateStatus for the
        `jim-pre-release` context, with this gate's remedy. Returns Passed, State, Description, TargetUrl and Message.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Statuses,

        [string]$Sha = 'the commit'
    )

    return Test-ReleaseGateStatus -Statuses $Statuses -Context $script:PreReleaseContext -Sha $Sha -Remedy (Get-PreReleaseRemedy -Sha $Sha) -NotReportedReason $script:PreReleaseNotReportedReason
}

# Dot-sourced (by the tests): the functions above are all that is wanted.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

exit (Invoke-ReleaseGate -Context $script:PreReleaseContext -Sha $Sha -Repository $Repository -Remedy (Get-PreReleaseRemedy -Sha $Sha) -NotReportedReason $script:PreReleaseNotReportedReason)
