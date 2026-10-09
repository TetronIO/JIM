# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The shared core of the release gates: reads a commit's statuses and decides whether one named status says
    `success`. Dot-source it; it defines functions and runs nothing.

.DESCRIPTION
    A release gate refuses a release commit unless a test run posted a `success` commit status of one fixed name on
    that exact SHA. Each gate is its own script with its own constant context and its own remedy, and calls these
    functions with them:

      scripts/Test-AdLabReleaseGate.ps1      context jim-ad-lab        (.github/workflows/ad-lab.yml)
      scripts/Test-PreReleaseGate.ps1        context jim-pre-release   (.github/workflows/pre-release.yml)

    The context is a parameter here, never on a gate script: a gate that could be pointed at another context would be
    a gate with an override. Nothing in this file skips, softens or accepts a stale result either; failing to read the
    statuses throws, and the gate scripts turn that into exit 1, because a gate that opens when it cannot see is not a
    gate.

    The decision (Test-ReleaseGateStatus) is separate from the gh call (Get-ReleaseGateStatus) so it is tested without
    either; see scripts/Tests/Test-AdLabReleaseGate.Tests.ps1 and Test-PreReleaseGate.Tests.ps1.
#>

Set-StrictMode -Version Latest

function Get-ReleaseGateField {
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

function Test-ReleaseGateStatus {
    <#
    .SYNOPSIS
        Decides whether a commit's statuses say the run named by -Context passed.

    .DESCRIPTION
        Pure. Takes the commit statuses (the objects GitHub returns: context, state, description, target_url,
        created_at, updated_at) and looks only at those whose context is exactly -Context. When there is more than one
        (a re-run on the same commit), the newest wins, so a later failure is not hidden by an earlier success nor a
        later success by an earlier failure. Passes only for state `success`.

        Returns Passed, State (the state, or "not reported"), Description, TargetUrl and Message (what to tell the
        person, which for a refusal names the remedy).

    .PARAMETER Statuses
        The commit's statuses; may be empty.

    .PARAMETER Context
        The exact, case-sensitive status context the gate requires.

    .PARAMETER Sha
        The commit, for the message.

    .PARAMETER Remedy
        What to do about a refusal; appended to every refusal message.

    .PARAMETER NotReportedReason
        Why a commit might carry no such status at all; goes between "is not reported for <sha>:" and the remedy.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Statuses,

        [Parameter(Mandatory)]
        [string]$Context,

        [string]$Sha = 'the commit',

        [Parameter(Mandatory)]
        [string]$Remedy,

        [Parameter(Mandatory)]
        [string]$NotReportedReason
    )

    $matching = @($Statuses | Where-Object { ($null -ne $_) -and ((Get-ReleaseGateField -Object $_ -Name 'context') -ceq $Context) })

    $newest = $null
    $newestAt = [DateTimeOffset]::MinValue
    foreach ($status in $matching) {
        $stamp = [DateTimeOffset]::MinValue
        foreach ($field in @('updated_at', 'created_at')) {
            $raw = Get-ReleaseGateField -Object $status -Name $field
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

    if ($null -eq $newest) {
        return [pscustomobject][ordered]@{
            Passed      = $false
            State       = 'not reported'
            Description = $null
            TargetUrl   = $null
            Message     = "The $Context status is not reported for ${Sha}: $NotReportedReason $Remedy"
        }
    }

    $state = [string](Get-ReleaseGateField -Object $newest -Name 'state')
    $description = [string](Get-ReleaseGateField -Object $newest -Name 'description')
    $url = [string](Get-ReleaseGateField -Object $newest -Name 'target_url')

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
            Message     = "$Context passed on ${Sha}: $detail"
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
        Message     = "The $Context status for $Sha is $shownState, not success.$said See $run. $Remedy"
    }
}

function Get-ReleaseGateStatus {
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
        $statuses = @(Get-ReleaseGateField -Object $document -Name 'statuses')
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

function Invoke-ReleaseGate {
    <#
    .SYNOPSIS
        A gate script's whole run: validates the inputs, reads the statuses, decides, prints, and returns the exit
        code (0 only for success). The gate script exits with it.

    .DESCRIPTION
        Any failure (a bad -Sha or -Repository, gh missing, gh failing) returns 1 with the reason, and in GitHub
        Actions also an ::error annotation titled "Release gate: <context>".
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Console output for a person or a workflow log; the exit code is the contract.')]
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)]
        [string]$Context,

        [AllowEmptyString()]
        [string]$Sha,

        [AllowEmptyString()]
        [string]$Repository,

        [Parameter(Mandatory)]
        [string]$Remedy,

        [Parameter(Mandatory)]
        [string]$NotReportedReason
    )

    $title = "::error title=Release gate: ${Context}::"
    try {
        if ($Sha -notmatch '^[0-9a-fA-F]{40}$') {
            throw "-Sha must be the full 40-character SHA of the commit that will be tagged (git rev-parse origin/main), not '$Sha'."
        }
        if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
            throw "-Repository must be OWNER/NAME, not '$Repository'."
        }
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
            throw "The GitHub CLI (gh) is not on the PATH, so the $Context status cannot be read. The gate stays closed."
        }

        $result = Test-ReleaseGateStatus -Statuses (Get-ReleaseGateStatus -Repository $Repository -Sha $Sha) -Context $Context -Sha $Sha -Remedy $Remedy -NotReportedReason $NotReportedReason
        if ($result.Passed) {
            Write-Host $result.Message -ForegroundColor Green
            return 0
        }
        Write-Host $result.Message -ForegroundColor Red
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-Host ($title + (($result.Message -replace '%', '%25') -replace "`r?`n", '%0A'))
        }
        return 1
    }
    catch {
        Write-Host "Release gate: $($_.Exception.Message)" -ForegroundColor Red
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-Host ($title + (($_.Exception.Message -replace '%', '%25') -replace "`r?`n", '%0A'))
        }
        return 1
    }
}
