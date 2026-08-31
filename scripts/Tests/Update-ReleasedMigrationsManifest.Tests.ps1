# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Update-ReleasedMigrationsManifest.ps1.

.DESCRIPTION
    Exercises the manifest writer over a fabricated migrations directory: freezing unlisted
    migrations, idempotency, the CRLF/LF hash normalisation that keeps Windows checkouts from
    reading as edits, and the refusal to re-freeze a released migration whose files have changed
    (the violation the manifest exists to prevent; legitimising it here would defeat
    ReleasedMigrationImmutabilityTests).
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Update-ReleasedMigrationsManifest.ps1')).Path

    # Builds a migrations directory holding the given migrations (as .cs + .Designer.cs pairs) and a
    # header-only manifest, returning the directory path.
    function New-MigrationsDirectory {
        param([string[]]$MigrationIds)
        $directory = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $directory | Out-Null
        foreach ($id in $MigrationIds) {
            Set-Content -Path (Join-Path $directory "$id.cs") -Value "// migration $id" -Encoding utf8
            Set-Content -Path (Join-Path $directory "$id.Designer.cs") -Value "// designer $id" -Encoding utf8
        }
        Set-Content -Path (Join-Path $directory 'released-migrations.lock') -Value '# test manifest' -Encoding utf8
        return $directory
    }

    function Get-ManifestEntries {
        param([string]$Directory)
        return @(Get-Content (Join-Path $Directory 'released-migrations.lock') |
            Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith('#') })
    }
}

Describe 'Update-ReleasedMigrationsManifest' {
    It 'freezes every unlisted migration with four fields and the release version' {
        $directory = New-MigrationsDirectory -MigrationIds @('20260101000000_First', '20260201000000_Second')

        & $script:ScriptPath -Version 1.0.0 -MigrationsPath $directory

        $entries = Get-ManifestEntries -Directory $directory
        $entries | Should -HaveCount 2
        $fields = $entries[0] -split ' '
        $fields | Should -HaveCount 4
        $fields[0] | Should -Be '20260101000000_First'
        $fields[1] | Should -Match '^[0-9a-f]{64}$'
        $fields[2] | Should -Match '^[0-9a-f]{64}$'
        $fields[3] | Should -Be '1.0.0'
    }

    It 'is idempotent: a second run for a later version appends nothing' {
        $directory = New-MigrationsDirectory -MigrationIds @('20260101000000_First')

        & $script:ScriptPath -Version 1.0.0 -MigrationsPath $directory
        & $script:ScriptPath -Version 1.1.0 -MigrationsPath $directory

        Get-ManifestEntries -Directory $directory | Should -HaveCount 1
    }

    It 'freezes only migrations added since the previous release, at the new version' {
        $directory = New-MigrationsDirectory -MigrationIds @('20260101000000_First')
        & $script:ScriptPath -Version 1.0.0 -MigrationsPath $directory
        Set-Content -Path (Join-Path $directory '20260301000000_Second.cs') -Value '// migration two' -Encoding utf8
        Set-Content -Path (Join-Path $directory '20260301000000_Second.Designer.cs') -Value '// designer two' -Encoding utf8

        & $script:ScriptPath -Version 1.1.0 -MigrationsPath $directory

        $entries = Get-ManifestEntries -Directory $directory
        $entries | Should -HaveCount 2
        $entries[0] | Should -Match ' 1\.0\.0$'
        $entries[1] | Should -Match '^20260301000000_Second .* 1\.1\.0$'
    }

    It 'hashes CRLF and LF content identically, so a Windows checkout does not read as an edit' {
        $lfDirectory = New-MigrationsDirectory -MigrationIds @()
        $crlfDirectory = New-MigrationsDirectory -MigrationIds @()
        [System.IO.File]::WriteAllText((Join-Path $lfDirectory '20260101000000_First.cs'), "line one`nline two`n")
        [System.IO.File]::WriteAllText((Join-Path $lfDirectory '20260101000000_First.Designer.cs'), "designer`n")
        [System.IO.File]::WriteAllText((Join-Path $crlfDirectory '20260101000000_First.cs'), "line one`r`nline two`r`n")
        [System.IO.File]::WriteAllText((Join-Path $crlfDirectory '20260101000000_First.Designer.cs'), "designer`r`n")

        & $script:ScriptPath -Version 1.0.0 -MigrationsPath $lfDirectory
        & $script:ScriptPath -Version 1.0.0 -MigrationsPath $crlfDirectory

        (Get-ManifestEntries -Directory $lfDirectory)[0] | Should -Be (Get-ManifestEntries -Directory $crlfDirectory)[0]
    }

    It 'refuses to run when a frozen migration has been edited' {
        $directory = New-MigrationsDirectory -MigrationIds @('20260101000000_First')
        & $script:ScriptPath -Version 1.0.0 -MigrationsPath $directory
        Set-Content -Path (Join-Path $directory '20260101000000_First.cs') -Value '// edited after release' -Encoding utf8

        { & $script:ScriptPath -Version 1.1.0 -MigrationsPath $directory } | Should -Throw '*immutable*'
    }

    It 'refuses to run when the manifest is missing' {
        $directory = New-MigrationsDirectory -MigrationIds @('20260101000000_First')
        Remove-Item (Join-Path $directory 'released-migrations.lock')

        { & $script:ScriptPath -Version 1.0.0 -MigrationsPath $directory } | Should -Throw '*append-only*'
    }
}
