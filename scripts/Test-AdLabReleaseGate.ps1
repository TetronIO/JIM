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
    dot-sourcing this file defines the functions and runs nothing.

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

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Console output for a person or a workflow log; the exit code is the contract.')]
[CmdletBinding()]
param(
    [string]$Sha,

    [string]$Repository = 'TetronIO/JIM'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The status context the lab workflow posts. A constant, not a parameter: there is nothing to configure here.
$script:AdLabContext = 'jim-ad-lab'

function Get-AdLabField {
    # One field of a status, from a parsed JSON object or a hashtable, or $null when it is not there.
    param(
        [AllowNull()]
        [object]$Object,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Test-AdLabStatus {
    <#
    .SYNOPSIS
        Decides whether a commit's statuses say the Active Directory lab passed.

    .DESCRIPTION
        Pure. Takes the commit statuses (the objects GitHub returns: context, state, description, target_url,
        created_at, updated_at) and looks only at those whose context is exactly `jim-ad-lab`. When there is more than
        one (a re-run on the same commit), the newest wins, so a later failure is not hidden by an earlier success
        nor a later success by an earlier failure. Passes only for state `success`.

        Returns Passed, State (the state, or "not reported"), Description, TargetUrl and Message (what to tell the
        person, which for a refusal names the remedy).

    .PARAMETER Statuses
        The commit's statuses; may be empty.

    .PARAMETER Sha
        The commit, for the message.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Statuses,

        [string]$Sha = 'the commit'
    )

    $matching = @($Statuses | Where-Object { ($null -ne $_) -and ((Get-AdLabField -Object $_ -Name 'context') -ceq $script:AdLabContext) })

    $newest = $null
    $newestAt = [DateTimeOffset]::MinValue
    foreach ($status in $matching) {
        $stamp = [DateTimeOffset]::MinValue
        foreach ($field in @('updated_at', 'created_at')) {
            $raw = Get-AdLabField -Object $status -Name $field
            $parsed = [DateTimeOffset]::MinValue
            if (($null -ne $raw) -and [DateTimeOffset]::TryParse([string]$raw, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) {
                $stamp = $parsed
                break
            }
        }
        if (($null -eq $newest) -or ($stamp -gt $newestAt)) {
            $newest = $status
            $newestAt = $stamp
        }
    }

    $remedy = "There is no override: fix the cause, then dispatch ad-lab.yml so it runs on $Sha (gh workflow run ad-lab.yml --ref main, while $Sha is the head of main), wait for it to finish, and check again."

    if ($null -eq $newest) {
        return [pscustomobject][ordered]@{
            Passed      = $false
            State       = 'not reported'
            Description = $null
            TargetUrl   = $null
            Message     = "The jim-ad-lab status is not reported for ${Sha}: the Active Directory lab has not run on this exact commit. The nightly run tests the head of main as it stood then, so a commit made since has none. $remedy"
        }
    }

    $state = [string](Get-AdLabField -Object $newest -Name 'state')
    $description = [string](Get-AdLabField -Object $newest -Name 'description')
    $url = [string](Get-AdLabField -Object $newest -Name 'target_url')

    if ($state -ceq 'success') {
        $detail = $description
        if ($url) {
            $detail = "$description ($url)"
        }
        return [pscustomobject][ordered]@{
            Passed      = $true
            State       = $state
            Description = $description
            TargetUrl   = $url
            Message     = "jim-ad-lab passed on ${Sha}: $detail"
        }
    }

    $shownState = $state
    if ([string]::IsNullOrEmpty($shownState)) {
        $shownState = 'unknown'
    }
    $run = 'no run link was recorded'
    if ($url) {
        $run = "the run: $url"
    }
    $said = ''
    if ($description) {
        $said = " It said: $description."
    }
    return [pscustomobject][ordered]@{
        Passed      = $false
        State       = $shownState
        Description = $description
        TargetUrl   = $url
        Message     = "The jim-ad-lab status for $Sha is $shownState, not success.$said See $run. $remedy"
    }
}

function Get-AdLabStatus {
    <#
    .SYNOPSIS
        Reads a commit's statuses through gh: GET repos/<repository>/commits/<sha>/status, every page of it.

    .DESCRIPTION
        The combined-status endpoint already holds only the latest status per context, and pages its statuses 100 at
        a time, so this follows the pages (up to ten). Throws, with what gh said, when gh fails.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [string]$Repository,

        [Parameter(Mandatory)]
        [string]$Sha
    )

    $all = New-Object System.Collections.Generic.List[object]
    for ($page = 1; $page -le 10; $page++) {
        $stderrPath = [System.IO.Path]::GetTempFileName()
        try {
            $global:LASTEXITCODE = 0
            $stdout = & gh api "repos/$Repository/commits/$Sha/status?per_page=100&page=$page" 2>$stderrPath
            $exitCode = $LASTEXITCODE
            $stderr = Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue
        }
        finally {
            Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
        }
        if ($exitCode -ne 0) {
            $said = (@($stdout) + @($stderr) | Where-Object { $_ } | ForEach-Object { ([string]$_).Trim() }) -join ' '
            throw "gh could not read the commit status of $Sha in ${Repository} (exit code $exitCode): $said"
        }

        $document = ((@($stdout) | ForEach-Object { [string]$_ }) -join "`n") | ConvertFrom-Json
        $statuses = @(Get-AdLabField -Object $document -Name 'statuses')
        foreach ($status in $statuses) {
            if ($null -ne $status) {
                $all.Add($status)
            }
        }
        if ($statuses.Count -lt 100) {
            break
        }
    }
    return $all.ToArray()
}

# Dot-sourced (by the tests): the functions above are all that is wanted.
if ($MyInvocation.InvocationName -eq '.') {
    return
}

try {
    if ($Sha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "-Sha must be the full 40-character SHA of the commit that will be tagged (git rev-parse origin/main), not '$Sha'."
    }
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "-Repository must be OWNER/NAME, not '$Repository'."
    }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'The GitHub CLI (gh) is not on the PATH, so the jim-ad-lab status cannot be read. The gate stays closed.'
    }

    $result = Test-AdLabStatus -Statuses (Get-AdLabStatus -Repository $Repository -Sha $Sha) -Sha $Sha
    if ($result.Passed) {
        Write-Host $result.Message -ForegroundColor Green
        exit 0
    }
    Write-Host $result.Message -ForegroundColor Red
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host ('::error title=Release gate: jim-ad-lab::' + (($result.Message -replace '%', '%25') -replace "`r?`n", '%0A'))
    }
    exit 1
}
catch {
    Write-Host "Release gate: $($_.Exception.Message)" -ForegroundColor Red
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host ('::error title=Release gate: jim-ad-lab::' + (($_.Exception.Message -replace '%', '%25') -replace "`r?`n", '%0A'))
    }
    exit 1
}
