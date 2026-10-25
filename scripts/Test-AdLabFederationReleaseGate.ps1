# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Refuses a release commit that has not passed the Active Directory lab's AD FS sign-in checks: exits 0 only when the
    commit's `jim-ad-lab-adfs` commit status is `success`.

.DESCRIPTION
    JIM's single sign-on is only ever verified against a real AD FS by the Active Directory lab's AD FS server
    (test/integration/ad-lab/README.md, "The AD FS server"). The same run of .github/workflows/ad-lab.yml that posts
    `jim-ad-lab` for the LDAP scenarios posts `jim-ad-lab-adfs` for the sign-in checks, on the SHA it tested, so a
    failure in one never colours the other (#2441). The release gate reads that status for the SHA that is about to be
    tagged and passes only when its state is `success`.

    It is called by the jim-ad-lab-adfs-gate job at the head of release.yml (which validate needs) and by the /release
    skill before it tags, beside scripts/Test-AdLabReleaseGate.ps1. Any other outcome exits 1 with a message that names
    what was found (the state, or "not reported"), the run that produced it, and the remedy: fix the cause, then
    dispatch ad-lab.yml so it runs on that commit. One dispatched run answers both gates.

    THERE IS NO OVERRIDE in this script. No parameter, environment variable or file skips, softens or accepts a stale
    result, by design (whether release.yml and /release call it at all is the JIM_AD_LAB_ADFS_GATE_ENFORCED repository
    variable, which is switched on once the checks have passed their first run). Failing to read the status (gh
    missing, no access, the commit unknown to GitHub) also exits 1, because a gate that opens when it cannot see is not
    a gate.

    The evaluation (Test-AdLabFederationStatus) is separate from the gh call (Get-AdLabFederationStatus) so it is tested
    without either; dot-sourcing this file defines the functions and runs nothing. Both are thin wrappers over the
    shared release-gate core in scripts/ReleaseGate.ps1; this script supplies the context and the remedy, as constants.

.PARAMETER Sha
    The full 40-character SHA of the commit that will be tagged (git rev-parse origin/main after the release pull
    request merges).

.PARAMETER Repository
    OWNER/NAME of the repository. Default TetronIO/JIM.

.EXAMPLE
    pwsh -File ./scripts/Test-AdLabFederationReleaseGate.ps1 -Sha (git rev-parse origin/main)
#>

[CmdletBinding()]
param(
    [string]$Sha,

    [string]$Repository = 'TetronIO/JIM'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ReleaseGate.ps1')

# The status context the lab workflow posts for the AD FS sign-in checks. A constant, not a parameter.
$script:AdLabFederationContext = 'jim-ad-lab-adfs'

function Get-AdLabFederationRemedy {
    param([string]$Sha = 'the commit')
    return "There is no override: fix the cause, then dispatch ad-lab.yml so it runs on $Sha (gh workflow run ad-lab.yml --ref main, while $Sha is the head of main), wait for it to finish, and check again."
}

$script:AdLabFederationNotReportedReason = 'the Active Directory lab''s AD FS sign-in checks have not run on this exact commit. The nightly run tests the head of main as it stood then, so a commit made since has none.'

function Test-AdLabFederationStatus {
    <#
    .SYNOPSIS
        Decides whether a commit's statuses say the AD FS sign-in checks passed: Test-ReleaseGateStatus for the
        `jim-ad-lab-adfs` context, with its remedy. Returns Passed, State, Description, TargetUrl and Message.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Statuses,

        [string]$Sha = 'the commit'
    )

    return Test-ReleaseGateStatus -Statuses $Statuses -Context $script:AdLabFederationContext -Sha $Sha -Remedy (Get-AdLabFederationRemedy -Sha $Sha) -NotReportedReason $script:AdLabFederationNotReportedReason
}

function Get-AdLabFederationStatus {
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

exit (Invoke-ReleaseGate -Context $script:AdLabFederationContext -Sha $Sha -Repository $Repository -Remedy (Get-AdLabFederationRemedy -Sha $Sha) -NotReportedReason $script:AdLabFederationNotReportedReason)
