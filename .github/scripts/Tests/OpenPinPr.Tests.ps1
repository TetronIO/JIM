# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for .github/scripts/open-pin-pr.ps1.

.DESCRIPTION
    The motivating defect (#1374): the apt-pin-check run of 2026-08-15 closed the
    very bump pull request it was updating (#1364), leaving a security pin
    unlandable while `main` could not build the Worker image at all. The script
    reset its bot branch to the tip of the base branch and then committed on top,
    so between those two calls the head branch carried no commits ahead of base.
    GitHub closes a pull request whose head branch is updated into that state, and
    the close landed in that window.

    These tests run the real script against a fake `gh` that models the small part
    of GitHub the script touches: refs, commits and pull requests, INCLUDING the
    auto-close rule above. That rule is what makes the tests meaningful; without
    it the sequence of API calls looks perfectly healthy.

    `gh` and `git` are replaced by PowerShell functions rather than shims on PATH:
    a function beats an external command in PowerShell's command resolution, and
    child scopes inherit it, so the script under test needs no seam of its own.
    The fakes report failure the way a native command does (a non-zero
    $LASTEXITCODE and a message on stdout), never as a PowerShell error record,
    because the script's $ErrorActionPreference = 'Stop' would turn the latter
    into a terminating error that `gh` itself would never raise.

    The fakes' state lives in $global: rather than $script:. A function's
    $script: scope resolves against the script that is *running* it, so a fake
    called from inside open-pin-pr.ps1 would read that script's scope and find
    nothing. $global: is the only scope both sides genuinely share.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'open-pin-pr.ps1')).Path

    $global:FakeRepository = 'TetronIO/JIM'
    $script:BotBranch = 'automation/apt-pin-updates'

    # --- the fake GitHub -----------------------------------------------------

    # Refs are keyed by branch name (no refs/heads/ prefix). Parents lets a test
    # assert what the bump commit was actually built on. RefWrites records every
    # ref update in order, which is how "the branch never sat at the base tip" is
    # asserted directly rather than inferred from the outcome.
    function New-FakeHub {
        param(
            [string]$BaseSha = 'basesha001',
            [hashtable]$Branches = @{},
            [array]$Prs = @()
        )

        $hub = [ordered]@{
            BaseSha    = $BaseSha
            Refs       = @{ 'main' = $BaseSha }
            Parents    = @{}
            Prs        = [System.Collections.ArrayList]::new()
            NextPr     = 1
            CommitSeq  = 0
            RefWrites  = [System.Collections.ArrayList]::new()
            Calls      = [System.Collections.ArrayList]::new()
            RefuseCreate = $false
        }

        foreach ($name in $Branches.Keys) { $hub.Refs[$name] = $Branches[$name] }
        foreach ($pr in $Prs) {
            $hub.Prs.Add([ordered]@{
                number   = $hub.NextPr
                head     = $pr.head
                base     = if ($pr.base) { $pr.base } else { 'main' }
                state    = $pr.state
                body     = if ($pr.body) { $pr.body } else { 'original body' }
                comments = [System.Collections.ArrayList]::new()
                closedBy = $null
            }) | Out-Null
            $hub.NextPr++
        }

        return $hub
    }

    # Every ref update goes through here so the auto-close rule is applied
    # uniformly, exactly as GitHub applies it however the ref was moved.
    function Set-FakeRef {
        param([string]$Branch, [string]$Sha)

        $global:FakeHub.Refs[$Branch] = $Sha
        $global:FakeHub.RefWrites.Add([ordered]@{ branch = $Branch; sha = $Sha }) | Out-Null

        foreach ($pr in $global:FakeHub.Prs) {
            if ($pr.state -ne 'OPEN' -or $pr.head -ne $Branch) { continue }
            # A head branch carrying no commits ahead of its base is an empty
            # pull request, and GitHub closes it. This is #1374.
            if ($global:FakeHub.Refs.ContainsKey($pr.base) -and $Sha -eq $global:FakeHub.Refs[$pr.base]) {
                $pr.state = 'CLOSED'
                $pr.closedBy = 'github-empty-range'
            }
        }
    }

    function script:git {
        $global:FakeHub.Calls.Add(@('git') + @($args)) | Out-Null
        $global:LASTEXITCODE = 0
        if (($args -join ' ') -eq 'diff --name-only') { return $global:FakeChangedFiles }
        return @()
    }

    function script:gh {
        $ghArgs = @($args)
        $global:FakeHub.Calls.Add(@('gh') + $ghArgs) | Out-Null
        $global:LASTEXITCODE = 0
        $stdin = $input

        switch ($ghArgs[0]) {
            'api' { return Invoke-FakeGhApi -GhArgs $ghArgs -Stdin $stdin }
            'pr'  { return Invoke-FakeGhPr  -GhArgs $ghArgs }
            default {
                $global:LASTEXITCODE = 1
                return "unknown gh command: $($ghArgs -join ' ')"
            }
        }
    }

    # Only the filters the pin scripts actually use. Anything else fails loudly:
    # a fake that quietly returned the whole document for an unrecognised filter
    # would let a script pass its tests and misread the real gh's output.
    function Invoke-FakeJq {
        param([string]$Filter, [string]$Json)

        if (-not $Filter) { return $Json }

        switch ($Filter) {
            '.object.sha'  { return (ConvertFrom-Json $Json).object.sha }
            '.[0].number'  {
                $first = @(ConvertFrom-Json $Json)[0]
                return $(if ($first) { "$($first.number)" } else { '' })
            }
            default { throw "The fake gh does not implement --jq '$Filter'." }
        }
    }

    function Invoke-FakeGhApi {
        param([string[]]$GhArgs, $Stdin)

        $method = 'GET'
        if ($GhArgs -contains '-X') { $method = $GhArgs[[array]::IndexOf($GhArgs, '-X') + 1] }

        # The endpoint is the first bare argument after 'api' (or after -X VERB).
        $endpoint = @($GhArgs | Select-Object -Skip 1 | Where-Object { $_ -notmatch '^-' -and $_ -ne $method })[0]

        # Field arguments (-f name=value / -F name=value).
        $fields = @{}
        for ($i = 0; $i -lt $GhArgs.Count; $i++) {
            if ($GhArgs[$i] -in @('-f', '-F') -and $GhArgs[$i + 1] -match '^([^=]+)=(.*)$') {
                $fields[$matches[1]] = $matches[2]
            }
        }

        $jq = if ($GhArgs -contains '--jq') { $GhArgs[[array]::IndexOf($GhArgs, '--jq') + 1] } else { $null }

        if ($endpoint -eq 'graphql') {
            $payload = ($Stdin | Out-String) | ConvertFrom-Json
            $mutationInput = $payload.variables.input
            $branch = $mutationInput.branch.branchName
            $expected = $mutationInput.expectedHeadOid

            if ($global:FakeHub.Refs[$branch] -ne $expected) {
                return '{"data":{"createCommitOnBranch":null},"errors":[{"message":"expectedHeadOid does not match the branch head"}]}'
            }

            $global:FakeHub.CommitSeq++
            $oid = "commit{0:d3}" -f $global:FakeHub.CommitSeq
            $global:FakeHub.Parents[$oid] = $expected
            Set-FakeRef -Branch $branch -Sha $oid
            return "{`"data`":{`"createCommitOnBranch`":{`"commit`":{`"oid`":`"$oid`",`"url`":`"https://example.invalid/$oid`"}}}}"
        }

        # The singular endpoint, repos/<owner>/<repo>/git/ref/heads/<branch>, is
        # an exact lookup and 404s when the ref does not exist.
        if ($endpoint -match "^repos/$([regex]::Escape($global:FakeRepository))/git/ref/heads/(?<branch>.+)$") {
            $branch = $matches['branch']
            if (-not $global:FakeHub.Refs.ContainsKey($branch)) {
                $global:LASTEXITCODE = 1
                return 'gh: Not Found (HTTP 404)'
            }
            $sha = $global:FakeHub.Refs[$branch]
            return (Invoke-FakeJq -Filter $jq -Json "{`"object`":{`"sha`":`"$sha`"}}")
        }

        # repos/<owner>/<repo>/git/refs/heads/<branch>, or .../git/refs for creates.
        if ($endpoint -match "^repos/$([regex]::Escape($global:FakeRepository))/git/refs(?:/heads/(?<branch>.+))?$") {
            $branch = $matches['branch']

            switch ($method) {
                'GET' {
                    # The PLURAL endpoint matches by PREFIX and returns every ref
                    # under it. This is the sharp edge that broke the first live
                    # run: probing "automation/x" found "automation/x-staging"
                    # and reported a branch that did not exist as existing.
                    $matched = @($global:FakeHub.Refs.Keys | Where-Object { $_ -eq $branch -or $_.StartsWith($branch) })
                    if ($matched.Count -eq 0) {
                        $global:LASTEXITCODE = 1
                        return 'gh: Not Found (HTTP 404)'
                    }
                    $refs = @($matched | ForEach-Object {
                        [pscustomobject]@{ ref = "refs/heads/$_"; object = [pscustomobject]@{ sha = $global:FakeHub.Refs[$_] } }
                    })
                    return ($refs | ConvertTo-Json -Depth 5 -AsArray)
                }
                'PATCH' {
                    if (-not $global:FakeHub.Refs.ContainsKey($branch)) {
                        $global:LASTEXITCODE = 1
                        return 'gh: Reference does not exist (HTTP 422)'
                    }
                    Set-FakeRef -Branch $branch -Sha $fields['sha']
                    return '{}'
                }
                'POST' {
                    $new = $fields['ref'] -replace '^refs/heads/', ''
                    if ($global:FakeHub.Refs.ContainsKey($new)) {
                        $global:LASTEXITCODE = 1
                        return 'gh: Reference already exists (HTTP 422)'
                    }
                    Set-FakeRef -Branch $new -Sha $fields['sha']
                    return '{}'
                }
                'DELETE' {
                    $global:FakeHub.Refs.Remove($branch)
                    return '{}'
                }
            }
        }

        $global:LASTEXITCODE = 1
        return "unknown gh api endpoint: $endpoint"
    }

    function Invoke-FakeGhPr {
        param([string[]]$GhArgs)

        $verb = $GhArgs[1]

        switch ($verb) {
            'list' {
                $head = $GhArgs[[array]::IndexOf($GhArgs, '--head') + 1]
                $wanted = @($global:FakeHub.Prs | Where-Object { $_.head -eq $head })
                if ($GhArgs -contains '--state') {
                    $state = $GhArgs[[array]::IndexOf($GhArgs, '--state') + 1]
                    if ($state -eq 'open') { $wanted = @($wanted | Where-Object { $_.state -eq 'OPEN' }) }
                }
                # gh emits the newest pull request first. Objects rather than
                # dictionaries so Sort-Object sees the property, and piped into
                # ConvertTo-Json rather than passed as -InputObject: the latter
                # treats the array as one value, which -AsArray then wraps again.
                $projected = @($wanted | ForEach-Object { [pscustomobject]@{ number = $_.number; state = $_.state } } |
                    Sort-Object number -Descending)
                $json = $projected | ConvertTo-Json -Depth 5 -AsArray
                $jq = if ($GhArgs -contains '--jq') { $GhArgs[[array]::IndexOf($GhArgs, '--jq') + 1] } else { $null }
                return (Invoke-FakeJq -Filter $jq -Json $json)
            }
            { $_ -in @('edit', 'reopen', 'close') } {
                $number = [int]$GhArgs[2]
                $pr = @($global:FakeHub.Prs | Where-Object { $_.number -eq $number })[0]
                if (-not $pr) { $global:LASTEXITCODE = 1; return "gh: no pull request #$number" }

                if ($verb -eq 'edit' -and $GhArgs -contains '--body') {
                    $pr.body = $GhArgs[[array]::IndexOf($GhArgs, '--body') + 1]
                }
                if ($verb -eq 'reopen') {
                    if ($pr.state -eq 'MERGED') { $global:LASTEXITCODE = 1; return 'gh: cannot reopen a merged pull request' }
                    $pr.state = 'OPEN'
                    $pr.closedBy = $null
                }
                if ($verb -eq 'close') {
                    $pr.state = 'CLOSED'
                    $pr.closedBy = 'script'
                    if ($GhArgs -contains '--comment') {
                        $pr.comments.Add($GhArgs[[array]::IndexOf($GhArgs, '--comment') + 1]) | Out-Null
                    }
                }
                return '{}'
            }
            'create' {
                $head = $GhArgs[[array]::IndexOf($GhArgs, '--head') + 1]
                if ($global:FakeHub.RefuseCreate -or
                    @($global:FakeHub.Prs | Where-Object { $_.head -eq $head -and $_.state -eq 'OPEN' }).Count -gt 0) {
                    $global:LASTEXITCODE = 1
                    return "gh: a pull request for branch $head already exists"
                }
                $global:FakeHub.Prs.Add([ordered]@{
                    number   = $global:FakeHub.NextPr
                    head     = $head
                    base     = $GhArgs[[array]::IndexOf($GhArgs, '--base') + 1]
                    state    = 'OPEN'
                    body     = $GhArgs[[array]::IndexOf($GhArgs, '--body') + 1]
                    comments = [System.Collections.ArrayList]::new()
                    closedBy = $null
                }) | Out-Null
                $global:FakeHub.NextPr++
                return "https://example.invalid/pr/$($global:FakeHub.NextPr - 1)"
            }
        }

        $global:LASTEXITCODE = 1
        return "unknown gh pr verb: $verb"
    }

    # --- fixture + invocation ------------------------------------------------

    # A working tree holding the file the -Apply step would have rewritten. The
    # script reads the file's bytes to build the commit, so it has to exist.
    function New-WorkingTree {
        param([string[]]$Files = @('src/JIM.Worker/Dockerfile'))

        $root = Join-Path $TestDrive ("Tree_" + [guid]::NewGuid().ToString('N'))
        foreach ($rel in $Files) {
            $full = Join-Path $root $rel
            New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
            Set-Content -Path $full -Value "libgssapi-krb5-2=1.20.1-6ubuntu2.8" -NoNewline
        }
        return $root
    }

    function Invoke-OpenPinPr {
        param([string]$WorkingTree, [hashtable]$ScriptArgs = @{})

        $arguments = @{
            Repository = $global:FakeRepository
            Branch     = $script:BotBranch
        } + $ScriptArgs

        Push-Location $WorkingTree
        try {
            # *>&1, not 2>&1: the script reports through Write-Host (the
            # information stream), which 2>&1 does not capture.
            $script:Output = & $script:ScriptPath @arguments *>&1 | Out-String
            $script:Failed = $false
        } catch {
            $script:Output = "$_"
            $script:Failed = $true
        } finally {
            Pop-Location
        }
        return $script:Output
    }

    function Get-FakePr {
        param([int]$Number)
        return @($global:FakeHub.Prs | Where-Object { $_.number -eq $Number })[0]
    }

    function Get-BotRefWrites {
        return @($global:FakeHub.RefWrites | Where-Object { $_.branch -eq $script:BotBranch } | ForEach-Object { $_.sha })
    }
}

Describe 'open-pin-pr.ps1' {

    BeforeEach {
        $global:FakeChangedFiles = @('src/JIM.Worker/Dockerfile')
        $global:FakeHub = $null
    }

    Context 'when a bump pull request is already open (#1374)' {

        BeforeEach {
            # The state the 2026-08-15 run found: an open PR raised two days
            # earlier, its branch carrying the previous bump commit.
            $global:FakeHub = New-FakeHub -BaseSha 'basesha001' `
                -Branches @{ $script:BotBranch = 'oldbump01' } `
                -Prs @(@{ head = $script:BotBranch; state = 'OPEN' })
            $global:FakeHub.Parents['oldbump01'] = 'basesha000'
        }

        It 'leaves the pull request open' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            (Get-FakePr -Number 1).state | Should -Be 'OPEN'
            (Get-FakePr -Number 1).closedBy | Should -BeNullOrEmpty
        }

        It 'never points the bump branch at the base branch tip' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            # The empty-commit-range window is the defect itself; asserting on it
            # directly pins the mechanism, not just the symptom.
            Get-BotRefWrites | Should -Not -Contain 'basesha001'
        }

        It 'survives consecutive runs' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            (Get-FakePr -Number 1).state | Should -Be 'OPEN'
            $global:FakeHub.Prs.Count | Should -Be 1
        }

        It 'leaves the branch carrying exactly one commit on top of the base tip' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            # The branch is a single fresh commit off the current base, never a
            # pile of superseded bumps, which is what the reset bought before.
            $head = $global:FakeHub.Refs[$script:BotBranch]
            $global:FakeHub.Parents[$head] | Should -Be 'basesha001'
        }

        It 'updates the pull request body with the new bump table' {
            $bodyFile = Join-Path $TestDrive 'pr-body.md'
            Set-Content -Path $bodyFile -Value '| libgssapi-krb5-2 | 2.7 | 2.8 |'

            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) -ScriptArgs @{ BodyFile = $bodyFile } | Out-Null

            (Get-FakePr -Number 1).body | Should -Match 'libgssapi-krb5-2'
        }

        It 'leaves no staging branch behind' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            @($global:FakeHub.Refs.Keys | Where-Object { $_ -ne 'main' -and $_ -ne $script:BotBranch }) | Should -BeNullOrEmpty
        }
    }

    Context 'when the bump pull request was closed by an earlier run' {

        BeforeEach {
            $global:FakeHub = New-FakeHub -BaseSha 'basesha001' `
                -Branches @{ $script:BotBranch = 'oldbump01' } `
                -Prs @(@{ head = $script:BotBranch; state = 'CLOSED' })
        }

        It 'reopens it rather than leaving the bump unlandable' {
            $output = Invoke-OpenPinPr -WorkingTree (New-WorkingTree)

            (Get-FakePr -Number 1).state | Should -Be 'OPEN'
            $output | Should -Match 'reopen'
        }

        It 'does not raise a second pull request for the same branch' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            $global:FakeHub.Prs.Count | Should -Be 1
        }
    }

    Context 'when no bump pull request exists' {

        BeforeEach {
            $global:FakeHub = New-FakeHub -BaseSha 'basesha001'
        }

        It 'creates the branch and opens a pull request' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            $global:FakeHub.Prs.Count | Should -Be 1
            (Get-FakePr -Number 1).state | Should -Be 'OPEN'
            $global:FakeHub.Parents[$global:FakeHub.Refs[$script:BotBranch]] | Should -Be 'basesha001'
        }

        It 'creates the bump branch even though the staging ref shares its name as a prefix' {
            # The first live run died here. The bot branch did not exist, the
            # staging ref did, and the existence probe used GitHub's plural refs
            # endpoint, which matches by prefix: it saw "<branch>-staging",
            # reported the branch as existing, and the update of a ref that was
            # never created failed with a 422.
            $global:FakeHub.Refs["$($script:BotBranch)-staging"] = 'leftover01'

            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            $global:FakeHub.Refs.ContainsKey($script:BotBranch) | Should -BeTrue
            $global:FakeHub.Parents[$global:FakeHub.Refs[$script:BotBranch]] | Should -Be 'basesha001'
            (Get-FakePr -Number 1).state | Should -Be 'OPEN'
        }

        It 'fails the run when the pull request cannot be opened' {
            # The tooling bot did exactly this in production, weekly, from 21
            # July: it created the branch, pushed the bump commit, logged
            # "Opening new PR ...", had `gh pr create` refused, and reported
            # success because nothing checked the exit code. A bot that raises
            # nothing must not report a green run.
            $global:FakeHub.RefuseCreate = $true

            $output = Invoke-OpenPinPr -WorkingTree (New-WorkingTree)

            $script:Failed | Should -BeTrue
            $output | Should -Match 'Failed to open a PR'
            $global:FakeHub.Prs.Count | Should -Be 0
        }

        It 'opens a fresh pull request when the previous one was merged' {
            $global:FakeHub.Prs.Add([ordered]@{
                number = 1; head = $script:BotBranch; base = 'main'; state = 'MERGED'
                body = 'landed'; comments = [System.Collections.ArrayList]::new(); closedBy = $null
            }) | Out-Null
            $global:FakeHub.NextPr = 2
            $global:FakeHub.Refs[$script:BotBranch] = 'oldbump01'

            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            $global:FakeHub.Prs.Count | Should -Be 2
            (Get-FakePr -Number 2).state | Should -Be 'OPEN'
        }
    }

    Context 'when there is nothing left to propose' {

        BeforeEach {
            $global:FakeChangedFiles = @()
            $global:FakeHub = New-FakeHub -BaseSha 'basesha001' `
                -Branches @{ $script:BotBranch = 'oldbump01' } `
                -Prs @(@{ head = $script:BotBranch; state = 'OPEN' })
        }

        It 'closes the stale pull request deliberately, stating why' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) -ScriptArgs @{ CloseStalePr = $true } | Out-Null

            $pr = Get-FakePr -Number 1
            $pr.state | Should -Be 'CLOSED'
            $pr.closedBy | Should -Be 'script'
            $pr.comments.Count | Should -Be 1
            $pr.comments[0] | Should -Match 'no longer'
        }

        It 'leaves the pull request alone unless the caller asked for the close' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) | Out-Null

            (Get-FakePr -Number 1).state | Should -Be 'OPEN'
        }

        It 'never touches the branch' {
            Invoke-OpenPinPr -WorkingTree (New-WorkingTree) -ScriptArgs @{ CloseStalePr = $true } | Out-Null

            Get-BotRefWrites | Should -BeNullOrEmpty
        }
    }
}
