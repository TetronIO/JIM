# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Refuses a release commit that has not passed the Active Directory lab: exits 0 only when the commit's `jim-ad-lab`
    commit status is `success`.

.DESCRIPTION
    JIM's LDAP Connector is only ever verified against a real Windows Server domain controller by the Active Directory
    lab (test/integration/ad-lab/README.md), which .github/workflows/ad-lab.yml runs nightly on main and on demand, and
    which posts a commit status named `jim-ad-lab` on the SHA it tested. The release gate reads that status for the SHA
    that is about to be tagged and passes only when its state is `success`.

    It is called by the jim-ad-lab-gate job at the head of release.yml (which every other release job needs) and by the
    /release skill before it tags. Any other outcome exits 1 with a message that names what was found (the state, or
    "not reported"), the run that produced it, and the remedy: fix the cause, then dispatch ad-lab.yml so it runs on
    that commit.

    The status belongs to one commit. A green night on the commit before the release commit says nothing about the
    release commit, so the release commit itself needs a dispatched run before it is tagged; that is the expected
    cost of the gate, not a defect in it.

    THERE IS NO OVERRIDE in this script. No parameter, environment variable or file skips, softens or accepts a stale
    result, by design (whether release.yml and /release call it at all is the JIM_AD_LAB_GATE_ENFORCED repository
    variable, which is switched on once the lab has passed its first run): the remedy for a red night is a fix and a dispatched re-run. Failing to read the status (gh missing, no
    access, the commit unknown to GitHub) also exits 1, because a gate that opens when it cannot see is not a gate.

    The evaluation (Test-AdLabStatus) is separate from the gh call (Get-AdLabStatus) so it is tested without either;
    dot-sourcing this file defines the functions and runs nothing. Both are thin wrappers over the shared release-gate
    core in scripts/ReleaseGate.ps1, which scripts/Test-PreReleaseGate.ps1 uses too; this script supplies the context
    and the lab's remedy, as constants.

.PARAMETER Sha
    The full 40-character SHA of the commit that will be tagged (git rev-parse origin/main after the release pull
    request merges).

.PARAMETER Repository
    OWNER/NAME of the repository. Default TetronIO/JIM.

.EXAMPLE
    pwsh -File ./scripts/Test-AdLabReleaseGate.ps1 -Sha (git rev-parse origin/main)

.EXAMPLE
    gh workflow run ad-lab.yml --ref main
    # wait for the run to finish, then check again
    pwsh -File ./scripts/Test-AdLabReleaseGate.ps1 -Sha (git rev-parse origin/main)
#>

[CmdletBinding()]
param(
    [string]$Sha,

    [string]$Repository = 'TetronIO/JIM'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ReleaseGate.ps1')

# The status context the lab workflow posts. A constant, not a parameter: there is nothing to configure here.
$script:AdLabContext = 'jim-ad-lab'

function Get-AdLabRemedy {
    param([string]$Sha = 'the commit')
    return "There is no override: fix the cause, then dispatch ad-lab.yml so it runs on $Sha (gh workflow run ad-lab.yml --ref main, while $Sha is the head of main), wait for it to finish, and check again."
}

$script:AdLabNotReportedReason = 'the Active Directory lab has not run on this exact commit. The nightly run tests the head of main as it stood then, so a commit made since has none.'

function Test-AdLabStatus {
    <#
    .SYNOPSIS
        Decides whether a commit's statuses say the Active Directory lab passed: Test-ReleaseGateStatus for the
        `jim-ad-lab` context, with the lab's remedy. Returns Passed, State, Description, TargetUrl and Message.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Statuses,

        [string]$Sha = 'the commit'
    )

    return Test-ReleaseGateStatus -Statuses $Statuses -Context $script:AdLabContext -Sha $Sha -Remedy (Get-AdLabRemedy -Sha $Sha) -NotReportedReason $script:AdLabNotReportedReason
}

function Get-AdLabStatus {
    # Reads a commit's statuses through gh (Get-ReleaseGateStatus); throws, with what gh said, when gh fails.
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [string]$Repository,

        [Parameter(Mandatory)]
        [string]$Sha
    )

    return Get-ReleaseGateStatus -Repository $Repository -Sha $Sha
}

# Dot-sourced (by the tests): the functions above are all that is wanted.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

exit (Invoke-ReleaseGate -Context $script:AdLabContext -Sha $Sha -Repository $Repository -Remedy (Get-AdLabRemedy -Sha $Sha) -NotReportedReason $script:AdLabNotReportedReason)
