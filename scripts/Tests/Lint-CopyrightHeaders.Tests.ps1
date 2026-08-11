# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Lint-CopyrightHeaders.ps1.

.DESCRIPTION
    Exercises the copyright-header rule from src/CLAUDE.md: every source file
    must carry the two-line Tetron notice in its leading preamble, spelled with
    the comment syntax of its own language.

    The motivating gap: .editorconfig only enforces the header for .cs (via
    IDE0073), so every other extension was convention-only, and JIM.psm1,
    JIM.psd1, three test/integration scripts, two .razor files, two test .cs
    files and the session-start hook had all drifted without anything noticing.

    All fixtures are written under $TestDrive; none of this depends on the
    live repo's source files.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Lint-CopyrightHeaders.ps1')).Path

    $script:CopyrightText = 'Copyright (c) Tetron Limited. All rights reserved.'
    $script:LicenceText = 'Licensed under the Tetron Commercial License. See LICENSE file in the project root.'

    # Writes a set of fixture files under a fresh, uniquely-named subdirectory
    # of $TestDrive, so each test scans an isolated tree. Keys are paths
    # relative to that root; values are the file content.
    function New-HeaderFixture {
        param([hashtable]$Files = @{})

        $root = Join-Path $TestDrive ("Tree_" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $root -Force | Out-Null

        foreach ($rel in $Files.Keys) {
            $full = Join-Path $root $rel
            New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
            # WriteAllText rather than Set-Content: Set-Content appends a
            # newline of its own, which would hide trailing-newline defects in
            # the -Fix path behind an artefact of the fixture.
            [System.IO.File]::WriteAllText($full, $Files[$rel], [System.Text.UTF8Encoding]::new($false))
        }

        return $root
    }

    # Invokes the script as CI does (a child pwsh process) and returns the exit
    # code plus merged output, so assertions test the real entry-point contract
    # rather than an internal function.
    function Invoke-Lint {
        param([string]$Root, [switch]$Fix)

        $arguments = @('-NoProfile', '-File', $script:ScriptPath, '-Path', $Root)
        if ($Fix) { $arguments += '-Fix' }

        $output = pwsh @arguments 2>&1
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    }

    function Get-FixtureContent {
        param([string]$Root, [string]$RelativePath)
        Get-Content -LiteralPath (Join-Path $Root $RelativePath) -Raw
    }
}

Describe 'Lint-CopyrightHeaders' {

    Context 'A compliant tree' {

        It 'passes when every supported file carries the header at the top' {
            $root = New-HeaderFixture -Files @{
                'Widget.cs'  = "// $script:CopyrightText`n// $script:LicenceText`n`nnamespace JIM.Fixture;`n"
                'Run.ps1'    = "# $script:CopyrightText`n# $script:LicenceText`n`nWrite-Host 'hi'`n"
                'Page.razor' = "@* $script:CopyrightText *@`n@* $script:LicenceText *@`n`n<div />`n"
                'boot.sh'    = "#!/bin/bash`n# $script:CopyrightText`n# $script:LicenceText`nset -e`n"
            }

            $r = Invoke-Lint -Root $root
            $r.ExitCode | Should -Be 0
        }

        It 'accepts a PowerShell header that follows a #Requires directive' {
            $root = New-HeaderFixture -Files @{
                'Module.psm1' = "#Requires -Version 7.0`n# $script:CopyrightText`n# $script:LicenceText`n`nfunction Get-Thing {}`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 0
        }

        It 'accepts a manifest header above the hashtable' {
            $root = New-HeaderFixture -Files @{
                'JIM.psd1' = "# $script:CopyrightText`n# $script:LicenceText`n`n@{`n    ModuleVersion = '1.0.0'`n}`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 0
        }

        It 'accepts a Razor header that follows the leading directive block' {
            # The dominant style in JIM.Web: @page/@inject first, notice after.
            $root = New-HeaderFixture -Files @{
                'Detail.razor' = "@page `"/detail`"`n@attribute [Authorize]`n@inject NavigationManager Nav`n`n@* $script:CopyrightText *@`n@* $script:LicenceText *@`n`n<h1>Detail</h1>`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 0
        }

        It 'accepts a header that follows a PowerShell block comment' {
            $root = New-HeaderFixture -Files @{
                'Helper.ps1' = "<#`n.SYNOPSIS`n    A helper.`n#>`n# $script:CopyrightText`n# $script:LicenceText`n`nWrite-Host 'hi'`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 0
        }

        It 'accepts a header preceded by a UTF-8 BOM' {
            $root = New-HeaderFixture -Files @{ 'Placeholder.cs' = 'placeholder' }
            $path = Join-Path $root 'Bom.cs'
            $content = "// $script:CopyrightText`n// $script:LicenceText`n`nnamespace JIM.Fixture;`n"
            [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($true))
            [System.IO.File]::WriteAllText(
                (Join-Path $root 'Placeholder.cs'),
                "// $script:CopyrightText`n// $script:LicenceText`n",
                [System.Text.UTF8Encoding]::new($false))

            (Invoke-Lint -Root $root).ExitCode | Should -Be 0
        }
    }

    Context 'A file missing the header' {

        It 'fails and names the offending file' {
            $root = New-HeaderFixture -Files @{
                'Good.cs' = "// $script:CopyrightText`n// $script:LicenceText`n`nnamespace JIM.Fixture;`n"
                'Bad.cs'  = "namespace JIM.Fixture;`n"
            }

            $r = Invoke-Lint -Root $root
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'Bad\.cs'
            $r.Output | Should -Not -Match 'Good\.cs'
        }

        It 'fails an empty file' {
            $root = New-HeaderFixture -Files @{ 'Empty.cs' = '' }
            (Invoke-Lint -Root $root).ExitCode | Should -Be 1
        }

        It 'fails when only the copyright line is present' {
            $root = New-HeaderFixture -Files @{
                'Half.ps1' = "# $script:CopyrightText`n`nWrite-Host 'hi'`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 1
        }

        It 'fails when the two lines are not consecutive' {
            $root = New-HeaderFixture -Files @{
                'Split.ps1' = "# $script:CopyrightText`n# Some unrelated remark.`n# $script:LicenceText`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 1
        }

        It 'fails when the wording drifts from the mandated text' {
            # e.g. the JIM.psd1 manifest key, which says '(c) Tetron Limited.
            # All rights reserved.' and nothing about the licence at all.
            $root = New-HeaderFixture -Files @{
                'Drift.ps1' = "# (c) Tetron Limited. All rights reserved.`n# $script:LicenceText`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 1
        }

        It 'fails when the header uses the wrong comment syntax for the language' {
            $root = New-HeaderFixture -Files @{
                'Wrong.razor' = "// $script:CopyrightText`n// $script:LicenceText`n`n<div />`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 1
        }

        It 'fails when the notice sits below the preamble, after real content' {
            $root = New-HeaderFixture -Files @{
                'Late.cs' = "namespace JIM.Fixture;`n`n// $script:CopyrightText`n// $script:LicenceText`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 1
        }

        It 'reports every offending file, not just the first' {
            $root = New-HeaderFixture -Files @{
                'One.cs'   = "namespace A;`n"
                'Two.ps1'  = "Write-Host 'hi'`n"
                'Three.sh' = "#!/bin/bash`nset -e`n"
            }

            $r = Invoke-Lint -Root $root
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'One\.cs'
            $r.Output | Should -Match 'Two\.ps1'
            $r.Output | Should -Match 'Three\.sh'
        }
    }

    Context 'Files outside the rule' {

        It 'ignores extensions the rule does not cover' {
            $root = New-HeaderFixture -Files @{
                'README.md'      = "# A readme with no notice`n"
                'settings.json'  = "{}`n"
                'styles.css'     = "body { color: red; }`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 0
        }

        It 'scans dot-directories, which are not build output' {
            # .claude/hooks/session-start.sh lives in one, and was missed for
            # exactly this reason: Get-ChildItem hides them without -Force.
            $root = New-HeaderFixture -Files @{ '.claude/hooks/session-start.sh' = "#!/bin/bash`nset -e`n" }

            $r = Invoke-Lint -Root $root
            $r.ExitCode | Should -Be 1
            $r.Output | Should -Match 'session-start\.sh'
        }

        It 'skips build output directories' {
            $root = New-HeaderFixture -Files @{
                'bin/Debug/Generated.cs' = "namespace Build;`n"
                'obj/Debug/Temp.cs'      = "namespace Build;`n"
                'node_modules/pkg/x.cs'  = "namespace Vendor;`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 0
        }

        It 'skips _Imports.razor, which src/CLAUDE.md carves out by name' {
            $root = New-HeaderFixture -Files @{ 'src/JIM.Web/_Imports.razor' = "@using JIM.Application`n" }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 0
        }

        It 'skips EF Core migrations and designer files, which are tool-generated' {
            $root = New-HeaderFixture -Files @{
                'src/JIM.PostgresData/Migrations/20260101_Init.cs'          = "namespace JIM.PostgresData.Migrations;`n"
                'src/JIM.PostgresData/Migrations/JimDbContextModelSnapshot.cs' = "namespace JIM.PostgresData.Migrations;`n"
                'src/JIM.Application/Properties/Resources.Designer.cs'      = "namespace JIM.Application.Properties;`n"
            }

            (Invoke-Lint -Root $root).ExitCode | Should -Be 0
        }
    }

    Context '-Fix' {

        It 'inserts the header and leaves the tree clean' {
            $root = New-HeaderFixture -Files @{
                'Bad.cs'     = "namespace JIM.Fixture;`n"
                'Bad.ps1'    = "Write-Host 'hi'`n"
                'Bad.razor'  = "<div />`n"
            }

            (Invoke-Lint -Root $root -Fix).ExitCode | Should -Be 0
            (Invoke-Lint -Root $root).ExitCode | Should -Be 0

            (Get-FixtureContent -Root $root -RelativePath 'Bad.cs') | Should -Match "^// $([regex]::Escape($script:CopyrightText))"
            (Get-FixtureContent -Root $root -RelativePath 'Bad.razor') | Should -Match "^@\* $([regex]::Escape($script:CopyrightText)) \*@"
        }

        It 'keeps a shebang on the first line' {
            $root = New-HeaderFixture -Files @{ 'boot.sh' = "#!/bin/bash`nset -e`n" }

            (Invoke-Lint -Root $root -Fix).ExitCode | Should -Be 0

            $lines = (Get-FixtureContent -Root $root -RelativePath 'boot.sh') -split "`r?`n"
            $lines[0] | Should -Be '#!/bin/bash'
            $lines[1] | Should -Be "# $script:CopyrightText"
            $lines[2] | Should -Be "# $script:LicenceText"
        }

        It 'keeps a #Requires directive above the inserted header' {
            $root = New-HeaderFixture -Files @{ 'Module.psm1' = "#Requires -Version 7.0`n`nfunction Get-Thing {}`n" }

            (Invoke-Lint -Root $root -Fix).ExitCode | Should -Be 0

            $lines = (Get-FixtureContent -Root $root -RelativePath 'Module.psm1') -split "`r?`n"
            $lines[0] | Should -Be '#Requires -Version 7.0'
            $lines[1] | Should -Be "# $script:CopyrightText"
            $lines[2] | Should -Be "# $script:LicenceText"
        }

        It 'preserves the original body verbatim' {
            $body = "namespace JIM.Fixture;`n`npublic sealed class Widget`n{`n    public int Id { get; set; }`n}`n"
            $root = New-HeaderFixture -Files @{ 'Widget.cs' = $body }

            Invoke-Lint -Root $root -Fix | Out-Null

            $fixed = Get-FixtureContent -Root $root -RelativePath 'Widget.cs'
            $fixed | Should -BeLike "*public sealed class Widget*"
            $fixed | Should -BeLike "*public int Id { get; set; }*"
        }

        It 'places a Razor header after the leading directive block' {
            # src/CLAUDE.md: "For .razor files: place the header after all @
            # directives, followed by a blank line before the markup". That is
            # also the dominant style in JIM.Web, so -Fix must not prepend.
            $root = New-HeaderFixture -Files @{
                'Detail.razor' = "@page `"/detail`"`n@inject NavigationManager Nav`n`n<h1>Detail</h1>`n"
            }

            (Invoke-Lint -Root $root -Fix).ExitCode | Should -Be 0

            $lines = (Get-FixtureContent -Root $root -RelativePath 'Detail.razor') -split "`r?`n"
            $lines[0] | Should -Be '@page "/detail"'
            $lines[1] | Should -Be '@inject NavigationManager Nav'
            $lines[2] | Should -Be ''
            $lines[3] | Should -Be "@* $script:CopyrightText *@"
            $lines[4] | Should -Be "@* $script:LicenceText *@"
        }

        It 'relocates a misplaced Razor header rather than duplicating it' {
            # The real defect in 12 JIM.Web components: the notice was injected
            # between an @if condition and its opening brace, so the file both
            # carried the notice and had it in the wrong place. A -Fix that
            # simply inserted would leave two copyright notices in the file.
            $misplaced = "@using JIM.Models.Activities`n@inject NavigationManager Nav`n`n@if (_ready)`n`n@* $script:CopyrightText *@`n@* $script:LicenceText *@`n{`n    <div />`n}`n"
            $root = New-HeaderFixture -Files @{ 'Paginator.razor' = $misplaced }

            (Invoke-Lint -Root $root -Fix).ExitCode | Should -Be 0
            (Invoke-Lint -Root $root).ExitCode | Should -Be 0

            $fixed = Get-FixtureContent -Root $root -RelativePath 'Paginator.razor'
            ([regex]::Matches($fixed, [regex]::Escape($script:CopyrightText))).Count | Should -Be 1
            # The @if must be reunited with its brace.
            $fixed | Should -Match "@if \(_ready\)\s*\r?\n\{"
        }

        It 'preserves the file"s original BOM state' {
            # Five JIM.Web components are stored with a UTF-8 BOM. Adding a
            # header is not a licence to re-encode the file, in either
            # direction, so -Fix leaves the byte-order mark exactly as found.
            $root = New-HeaderFixture -Files @{ 'Plain.cs' = "namespace A;`n" }
            $withBom = Join-Path $root 'Bom.razor'
            [System.IO.File]::WriteAllText($withBom, "<div />`n", [System.Text.UTF8Encoding]::new($true))

            Invoke-Lint -Root $root -Fix | Out-Null

            $bomBytes = [System.IO.File]::ReadAllBytes($withBom)[0..2]
            $bomBytes | Should -Be @(0xEF, 0xBB, 0xBF)

            $plainBytes = [System.IO.File]::ReadAllBytes((Join-Path $root 'Plain.cs'))[0..2]
            $plainBytes | Should -Not -Be @(0xEF, 0xBB, 0xBF)
        }

        It 'does not add a trailing blank line to a file that already ended with a newline' {
            $root = New-HeaderFixture -Files @{ 'Widget.cs' = "namespace JIM.Fixture;`n" }

            Invoke-Lint -Root $root -Fix | Out-Null

            $fixed = Get-FixtureContent -Root $root -RelativePath 'Widget.cs'
            $fixed | Should -Not -Match "`n`n$"
            $fixed | Should -Match "namespace JIM\.Fixture;`n$"
        }

        It 'leaves already-compliant files untouched' {
            $original = "// $script:CopyrightText`n// $script:LicenceText`n`nnamespace JIM.Fixture;`n"
            $root = New-HeaderFixture -Files @{ 'Good.cs' = $original }
            $before = Get-FixtureContent -Root $root -RelativePath 'Good.cs'

            Invoke-Lint -Root $root -Fix | Out-Null

            (Get-FixtureContent -Root $root -RelativePath 'Good.cs') | Should -Be $before
        }
    }
}
