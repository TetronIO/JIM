# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Lint-DocsCoupling.ps1.

.DESCRIPTION
    Exercises the docs-coupling rule, in particular that the requirement only
    applies to user-facing (✨/🔄) [Unreleased] entries that the PR under test
    actually ADDED, not entries already present in the base branch. The latter
    is the #865 regression: an unrelated PR (e.g. a Dependabot bump) inherited a
    ✨ entry left in [Unreleased] by an already-merged feature PR and was forced
    to update docs/ for a change it had nothing to do with.

    The base section is supplied via -BasePath so the tests need no git repo.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Lint-DocsCoupling.ps1')).Path

    # Writes a CHANGELOG with the given top-level [Unreleased] entries, plus a
    # released section beneath it so section termination is exercised.
    function New-Changelog {
        param([string]$Path, [string[]]$UnreleasedEntries)
        $lines = @('## [Unreleased]', '', '### Added', '')
        $lines += ($UnreleasedEntries | ForEach-Object { "- $_" })
        $lines += @('', '## [0.12.0] - 2026-06-01', '', '### Added', '- ✨ A previously released feature.')
        Set-Content -Path $Path -Value $lines -Encoding utf8
    }

    # Invokes the script as CI does (a child pwsh process) and returns the exit
    # code plus merged output, so assertions test the real entry-point contract.
    function Invoke-Lint {
        param(
            [string[]]$BaseEntries,
            [string[]]$HeadEntries,
            [string]$ChangedFiles = '',
            [string]$PrBody = '',
            [string]$PrLabels = ''
        )
        $base = Join-Path $TestDrive 'base-CHANGELOG.md'
        $head = Join-Path $TestDrive 'head-CHANGELOG.md'
        New-Changelog -Path $base -UnreleasedEntries $BaseEntries
        New-Changelog -Path $head -UnreleasedEntries $HeadEntries
        $output = pwsh -NoProfile -File $script:ScriptPath `
            -Path $head -BasePath $base `
            -ChangedFiles $ChangedFiles -PrBody $PrBody -PrLabels $PrLabels 2>&1
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    }

    $script:Feature = '✨ Expression-based example data generation can derive a value from other attributes.'
    $script:Changed = '🔄 The default Run Profile now resolves references across pages.'
}

Describe 'Lint-DocsCoupling docs coupling' {

    It 'passes when the only user-facing entry pre-exists in base and the PR changes no docs (#865)' {
        $r = Invoke-Lint -BaseEntries @($script:Feature) -HeadEntries @($script:Feature) `
            -ChangedFiles 'src/JIM.Web/Dockerfile'
        $r.ExitCode | Should -Be 0
    }

    It 'fails when the PR adds a new user-facing entry without touching docs' {
        $r = Invoke-Lint -BaseEntries @() -HeadEntries @($script:Feature) `
            -ChangedFiles 'src/JIM.Application/Servers/ExampleDataServer.cs'
        $r.ExitCode | Should -Be 1
    }

    It 'passes when a newly added user-facing entry ships docs in the same PR' {
        $r = Invoke-Lint -BaseEntries @() -HeadEntries @($script:Feature) `
            -ChangedFiles "docs/example-data.md`nsrc/JIM.Application/Servers/ExampleDataServer.cs"
        $r.ExitCode | Should -Be 0
    }

    It 'passes a newly added entry when the docs-not-needed label is set' {
        $r = Invoke-Lint -BaseEntries @() -HeadEntries @($script:Feature) `
            -ChangedFiles 'src/x.cs' -PrLabels 'dependencies,docs-not-needed'
        $r.ExitCode | Should -Be 0
    }

    It 'passes a newly added entry when the PR body opts out with a reason' {
        $r = Invoke-Lint -BaseEntries @() -HeadEntries @($script:Feature) `
            -ChangedFiles 'src/x.cs' -PrBody "Some description.`nDocs: n/a - internal-only plumbing"
        $r.ExitCode | Should -Be 0
    }

    It 'ignores newly added non-user-facing entries (a bug fix needs no docs)' {
        $r = Invoke-Lint -BaseEntries @() -HeadEntries @('🐛 A completed Activity no longer shows a stale progress line.') `
            -ChangedFiles 'src/x.cs'
        $r.ExitCode | Should -Be 0
    }

    It 'requires docs for a 🔄 changed-behaviour entry the PR adds' {
        $r = Invoke-Lint -BaseEntries @() -HeadEntries @($script:Changed) `
            -ChangedFiles 'src/x.cs'
        $r.ExitCode | Should -Be 1
    }

    It 'treats a reworded pre-existing entry as newly added' {
        $r = Invoke-Lint -BaseEntries @($script:Feature) `
            -HeadEntries @('✨ Expression-based example data generation can derive a value from other attributes, with circular references detected up front.') `
            -ChangedFiles 'src/x.cs'
        $r.ExitCode | Should -Be 1
    }

    It 'passes when there are no user-facing entries at all' {
        $r = Invoke-Lint -BaseEntries @('🐛 A fix.') -HeadEntries @('🐛 A fix.', '⚡ A perf win.') `
            -ChangedFiles 'src/x.cs'
        $r.ExitCode | Should -Be 0
    }
}
