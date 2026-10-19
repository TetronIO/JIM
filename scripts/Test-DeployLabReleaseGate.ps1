# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Refuses a release commit that has not passed the deployment lab: exits 0 only when the commit's `jim-deploy-lab`
    commit status is `success`.

.DESCRIPTION
    The deployment lab (test/ci/deployment/lab/README.md) installs and upgrades a bundle built from the commit the way
    customers do, on fresh air-gapped VMs: Docker with either image store, rootful and rootless Podman, Ubuntu and a
    RHEL rebuild with SELinux enforcing, upgrades with a rollback, the installer's refusals and certificate options,
    an external database, and a soak of restarts and reboots. It runs in .github/workflows/deploy-lab.yml, which
    starts by itself nightly on main and when a change to VERSION reaches main (that is, when a release pull request
    merges), and on demand, and posts a commit status named `jim-deploy-lab` on the SHA it tested. This gate reads
    that status for the SHA that is about to be tagged and passes only when its state is `success`.

    It is called by the jim-deploy-lab-gate job in release.yml (which validate needs) and by the /release skill
    before it tags. Any other outcome exits 1 with a message that names what was found (the state, or "not
    reported"), the run that produced it, and the remedy: fix the cause, then run deploy-lab.yml on that commit.

    The status belongs to one commit: the release commit itself, the merge commit of the release pull request, is the
    one that must carry it.

    THERE IS NO OVERRIDE in this script. No parameter, environment variable or file skips, softens or accepts a stale
    result, by design (whether release.yml and /release call it at all is the JIM_DEPLOY_LAB_GATE_ENFORCED
    repository variable, switched on once the lab has had its first green run in Actions). Failing to read the status
    also exits 1, because a gate that opens when it cannot see is not a gate.

    The evaluation (Test-DeployLabStatus) is separate from the gh call, and both are thin wrappers over the shared
    release-gate core in scripts/ReleaseGate.ps1 (which the other two release gates use too); this script supplies
    the context and the remedy, as constants. Dot-sourcing this file defines the functions and runs nothing.

.PARAMETER Sha
    The full 40-character SHA of the commit that will be tagged (git rev-parse origin/main after the release pull
    request merges).

.PARAMETER Repository
    OWNER/NAME of the repository. Default TetronIO/JIM.

.EXAMPLE
    pwsh -File ./scripts/Test-DeployLabReleaseGate.ps1 -Sha (git rev-parse origin/main)

.EXAMPLE
    gh workflow run deploy-lab.yml --ref main
    # wait for the run to finish, then check again
    pwsh -File ./scripts/Test-DeployLabReleaseGate.ps1 -Sha (git rev-parse origin/main)
#>

[CmdletBinding()]
param(
    [string]$Sha,

    [string]$Repository = 'TetronIO/JIM'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ReleaseGate.ps1')

# The status context deploy-lab.yml posts. A constant, not a parameter: there is nothing to configure here.
$script:DeployLabContext = 'jim-deploy-lab'

function Get-DeployLabRemedy {
    param([string]$Sha = 'the commit')
    return "There is no override: fix the cause, then run deploy-lab.yml on $Sha (merging the release pull request starts it; otherwise gh workflow run deploy-lab.yml --ref main, while $Sha is the head of main), wait for it to finish (about 45 minutes), and check again."
}

$script:DeployLabNotReportedReason = 'the deployment lab has not run on this exact commit. It starts by itself nightly on main and when a change to VERSION reaches main, or on demand, so a commit that was none of these has none.'

function Test-DeployLabStatus {
    <#
    .SYNOPSIS
        Decides whether a commit's statuses say the deployment lab passed: Test-ReleaseGateStatus for the
        `jim-deploy-lab` context, with this gate's remedy. Returns Passed, State, Description, TargetUrl and Message.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Statuses,

        [string]$Sha = 'the commit'
    )

    return Test-ReleaseGateStatus -Statuses $Statuses -Context $script:DeployLabContext -Sha $Sha -Remedy (Get-DeployLabRemedy -Sha $Sha) -NotReportedReason $script:DeployLabNotReportedReason
}

# Dot-sourced (by the tests): the functions above are all that is wanted.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

exit (Invoke-ReleaseGate -Context $script:DeployLabContext -Sha $Sha -Repository $Repository -Remedy (Get-DeployLabRemedy -Sha $Sha) -NotReportedReason $script:DeployLabNotReportedReason)
