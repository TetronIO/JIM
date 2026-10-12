# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Get-DocsDueForValidation.ps1.

.DESCRIPTION
    /release runs the script to list the priority pages due for re-validation
    against the release being cut (PRI-1 in engineering/DOCUMENTATION_STANDARD.md,
    #1454). A page missing from the list is a page nobody re-checks before it
    ships, so these tests pin down what counts as due: no validation, an older
    one, or one that cannot be read. A page validated against this release is
    not due.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Get-DocsDueForValidation.ps1')).Path

    function New-Fixture {
        param([hashtable]$Pages)
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $docs = Join-Path $root 'docs'
        New-Item -ItemType Directory -Path $docs -Force | Out-Null
        foreach ($rel in $Pages.Keys) {
            $full = Join-Path $docs $rel
            New-Item -ItemType Directory -Path (Split-Path $full -Parent) -Force | Out-Null
            Set-Content -Path $full -Value $Pages[$rel] -Encoding utf8
        }
        $list = ($Pages.Keys | Sort-Object | ForEach-Object { "'$_'" }) -join ', '
        $config = Join-Path $root 'config.psd1'
        Set-Content -Path $config -Value "@{ PriorityPages = @($list) }" -Encoding utf8
        [pscustomobject]@{ Docs = $docs; Config = $config }
    }

    function New-Page {
        param([string]$Validated)
        $line = if ($Validated) { "validated: `"$Validated`"`n" } else { '' }
        "---`ntitle: `"A Page`"`n$line---`n`n# A Page`n"
    }

    function Invoke-Due {
        param($Fixture, [string]$Version)
        @(& $script:ScriptPath -Version $Version -DocsPath $Fixture.Docs -ConfigPath $Fixture.Config -PassThru)
    }
}

Describe 'Get-DocsDueForValidation' {
    BeforeAll {
        $script:Fixture = New-Fixture -Pages @{
            'current.md'    = (New-Page -Validated 'v0.17.0')
            'older.md'      = (New-Page -Validated 'v0.15.2')
            'never.md'      = (New-Page)
            'unreadable.md' = (New-Page -Validated 'last week')
        }
        $script:Due = Invoke-Due -Fixture $script:Fixture -Version '0.17.0'
    }

    It 'lists every priority page not validated against the release, and no other' {
        ($script:Due.Page | Sort-Object) | Should -Be @('never.md', 'older.md', 'unreadable.md')
    }

    It 'says why each page is due' {
        ($script:Due | Where-Object Page -eq 'older.md').Reason | Should -Match 'v0\.15\.2'
        ($script:Due | Where-Object Page -eq 'never.md').Reason | Should -Match 'never'
        ($script:Due | Where-Object Page -eq 'unreadable.md').Reason | Should -Match "last week"
    }

    It 'takes the version with or without its leading v' {
        (Invoke-Due -Fixture $script:Fixture -Version 'v0.17.0').Count | Should -Be 3
    }

    It 'lists nothing once every page is validated against the release' {
        $f = New-Fixture -Pages @{ 'a.md' = (New-Page -Validated 'v0.17.0') }
        Invoke-Due -Fixture $f -Version '0.17.0' | Should -BeNullOrEmpty
    }

    It 'fails on a priority page that does not exist, rather than leaving it off the list' {
        $f = New-Fixture -Pages @{ 'a.md' = (New-Page -Validated 'v0.17.0') }
        Set-Content -Path $f.Config -Value "@{ PriorityPages = @('a.md', 'gone.md') }" -Encoding utf8
        { & $script:ScriptPath -Version '0.17.0' -DocsPath $f.Docs -ConfigPath $f.Config 6>$null } | Should -Throw '*gone.md*'
    }

    It 'prints each page due and a summary when not asked for objects' {
        $printed = & $script:ScriptPath -Version '0.17.0' -DocsPath $script:Fixture.Docs -ConfigPath $script:Fixture.Config 6>&1
        ($printed | Out-String) | Should -Match 'older\.md\s+last validated against v0\.15\.2'
        ($printed | Out-String) | Should -Match '3 of 4 priority pages due for re-validation against v0\.17\.0'
    }

    It 'refuses a version it cannot read' {
        { & $script:ScriptPath -Version 'next' -DocsPath $script:Fixture.Docs -ConfigPath $script:Fixture.Config 6>$null } | Should -Throw '*next*'
    }
}
