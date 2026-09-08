# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Lint-Changelog.ps1.

.DESCRIPTION
    Covers the rules that have no false positives and therefore fail the build:
    the canonical-emoji whitelist, identical entries within a subsection, and
    an [Unreleased] entry that has already shipped in a released section.

    That last one is the failure mode CHANGELOG.md's `merge=union` driver
    produces. Union merging is what stops parallel branches conflicting on
    every [Unreleased] edit, and the cost is that it re-adds lines rather than
    reconciling them: a branch that merges or rebases across a release brings
    the entries it contributed back into [Unreleased], where they read as
    unshipped work and would ship a second time in the next release notes. It
    has happened, and nothing caught it, because the within-section duplicate
    check only ever compared an entry against its own section.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Lint-Changelog.ps1')).Path

    # Writes a CHANGELOG with the given [Unreleased] and released entries.
    function New-Changelog {
        param(
            [string]$Path,
            [string[]]$UnreleasedEntries = @(),
            [string[]]$ReleasedEntries = @('- ✨ A previously released feature nobody has touched since. (#1)')
        )
        $lines = @('# Changelog', '', '## [Unreleased]', '', '### Added', '')
        $lines += $UnreleasedEntries
        $lines += @('', '## [0.14.0] - 2026-07-25', '', '### Added', '')
        $lines += $ReleasedEntries
        Set-Content -Path $Path -Value $lines -Encoding utf8
    }

    # Invokes the script as CI does (a child pwsh process) so the assertions
    # test the real entry-point contract, exit code included.
    function Invoke-Lint {
        param(
            [string[]]$UnreleasedEntries = @(),
            [string[]]$ReleasedEntries = @('- ✨ A previously released feature nobody has touched since. (#1)')
        )
        $path = Join-Path $TestDrive 'CHANGELOG.md'
        New-Changelog -Path $path -UnreleasedEntries $UnreleasedEntries -ReleasedEntries $ReleasedEntries
        $output = pwsh -NoProfile -File $script:ScriptPath -Path $path 2>&1
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = ($output | Out-String)
        }
    }
}

Describe 'Lint-Changelog' {

    Context 'canonical emoji' {

        It 'passes an entry leading with a canonical emoji' {
            $result = Invoke-Lint -UnreleasedEntries @('- ✨ Something a customer can see. (#2)')
            $result.ExitCode | Should -Be 0
        }

        It 'fails an entry leading with an off-list emoji' {
            $result = Invoke-Lint -UnreleasedEntries @('- 🧪 Added a test harness. (#2)')
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'canonical emoji'
        }
    }

    Context 'duplicates within a subsection' {

        It 'fails two identical entries' {
            $entry = '- ✨ Something a customer can see. (#2)'
            $result = Invoke-Lint -UnreleasedEntries @($entry, $entry)
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'identical'
        }

        It 'passes two entries that merely share a subsection' {
            $result = Invoke-Lint -UnreleasedEntries @(
                '- ✨ Something a customer can see. (#2)',
                '- 🐛 A different thing entirely, fixed. (#3)'
            )
            $result.ExitCode | Should -Be 0
        }
    }

    Context 'an entry that has already shipped' {

        It 'fails an [Unreleased] entry identical to one in a released section' {
            # Exactly what a union merge across a release produces.
            $shipped = '- ✨ A previously released feature nobody has touched since. (#1)'
            $result = Invoke-Lint -UnreleasedEntries @($shipped) -ReleasedEntries @($shipped)
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match '0\.14\.0'
        }

        It 'names the released version so the entry can be found and deleted' {
            $shipped = '- 🐛 A bug that was fixed a release ago. (#4)'
            $result = Invoke-Lint -UnreleasedEntries @($shipped) -ReleasedEntries @($shipped)
            $result.Output | Should -Match 'already shipped'
        }

        It 'passes when [Unreleased] and the released section describe different changes' {
            $result = Invoke-Lint `
                -UnreleasedEntries @('- ✨ Something new and unshipped. (#2)') `
                -ReleasedEntries @('- ✨ A previously released feature nobody has touched since. (#1)')
            $result.ExitCode | Should -Be 0
        }

        It 'tolerates an entry whose wording was reworked after shipping, as a warning only' {
            # A follow-up that genuinely revisits shipped work opens the same way
            # and then diverges. That is a judgement call, not a certainty, so it
            # must not fail the build.
            $result = Invoke-Lint `
                -UnreleasedEntries @('- ✨ A previously released feature nobody has touched since, now with an extra option. (#2)') `
                -ReleasedEntries @('- ✨ A previously released feature nobody has touched since. (#1)')
            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'WARNING'
        }
    }

    Context 'near-identical entries (a re-add after a reword)' {

        # The union driver's other signature: an entry was reworded on one
        # branch while another still carried the original, and the merge kept
        # both. The pair differs in a handful of words out of dozens, which is
        # not how two genuinely different changes read. The original wording of
        # the #1119 retention entry came back beside its #1547 rewrite this way
        # and only warned, because it opened the same way, so it shipped as
        # far as main.
        BeforeAll {
            $script:Rewritten = '- ✨ Password Synchronisation history is now kept under its own retention period, defaulting to a year. It governs the Activities recording what happened to each password change, and the queued changes that finished (parked, expired or cancelled). A parked or cancelled change still carries its encrypted password, so shortening the period is how you shorten that. (#1119)'
            $script:Original  = '- ✨ Password Synchronisation history is now kept under its own retention period, defaulting to a year. It governs the Activities recording what happened to each password change, and the queue rows that finished (parked, expired or cancelled). A parked or cancelled row still carries its encrypted value, so shortening the period is how you shorten that. (#1119)'
        }

        It 'fails two entries in one subsection that differ in only a few words' {
            $result = Invoke-Lint -UnreleasedEntries @($script:Rewritten, $script:Original)
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'near-identical'
        }

        It 'still passes two entries that open the same way but describe different changes' {
            # Two real fixes from [Unreleased] that share their first eight
            # words and nothing much after: the opening warns, the pair must
            # not fail.
            $result = Invoke-Lint -UnreleasedEntries @(
                '- 🐛 Outlined and text buttons in the primary colour are now legible on the Black Dark theme, which took the accent straight where the other dark themes lighten it. (#5)',
                '- 🐛 Outlined and text buttons in the primary colour are now legible on the light themes, whose pale page left the darkest accent below the contrast an accessible reading needs. (#6)'
            )
            $result.ExitCode | Should -Be 0
            $result.Output | Should -Match 'WARNING'
        }

        It 'fails an [Unreleased] entry near-identical to one that already shipped' {
            $result = Invoke-Lint -UnreleasedEntries @($script:Original) -ReleasedEntries @($script:Rewritten)
            $result.ExitCode | Should -Be 1
            $result.Output | Should -Match 'near-identical'
            $result.Output | Should -Match '0\.14\.0'
        }
    }
}
