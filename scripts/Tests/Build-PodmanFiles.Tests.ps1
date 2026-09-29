# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Build-PodmanFiles.ps1.

.DESCRIPTION
    Renders the real deploy/podman files and checks what an administrator receives: image references
    carrying the release's registry and version, PostgreSQL's digest-pinned image read from
    docker-compose.yml (its single source of truth), no placeholder left behind, the Quadlet units copied
    unchanged, and Linux line endings throughout.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Build-PodmanFiles.ps1')).Path
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function New-OutputPath {
        Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
    }

    function Get-ImageReferences {
        param([string]$Path)
        @(Get-Content $Path | Where-Object { $_ -match '^\s*image:\s*(\S+)' } | ForEach-Object { ($_ -split 'image:\s*')[1].Trim() })
    }
}

Describe 'Build-PodmanFiles' {
    It 'names JIM''s images with the registry and version given' {
        $output = New-OutputPath

        & $script:ScriptPath -Version 1.2.3 -Registry 'registry.example.com/jim/' -OutputPath $output

        Get-ImageReferences (Join-Path $output 'jim.yaml') | Should -Be @(
            'registry.example.com/jim/jim-web:1.2.3'
            'registry.example.com/jim/jim-worker:1.2.3'
            'registry.example.com/jim/jim-scheduler:1.2.3'
        )
    }

    It 'defaults to the GitHub Container Registry' {
        $output = New-OutputPath

        & $script:ScriptPath -Version 1.2.3 -OutputPath $output

        Get-ImageReferences (Join-Path $output 'jim.yaml') | Should -Contain 'ghcr.io/tetronio/jim-web:1.2.3'
    }

    It 'adds the separator a registry given without one needs' {
        $output = New-OutputPath

        & $script:ScriptPath -Version 1.2.3 -Registry 'localhost' -OutputPath $output

        Get-ImageReferences (Join-Path $output 'jim.yaml') | Should -Contain 'localhost/jim-web:1.2.3'
    }

    It 'uses the PostgreSQL image docker-compose.yml pins' {
        $output = New-OutputPath
        $compose = Get-Content (Join-Path $script:RepoRoot 'docker-compose.yml') -Raw
        # The default of JIM_DB_IMAGE, which lets an air-gapped Docker installation run the loaded image by its ID.
        $expected = [regex]::Match($compose, 'image:\s+\$\{JIM_DB_IMAGE:-(docker\.io/library/postgres:[^}\s]+)\}').Groups[1].Value

        & $script:ScriptPath -Version 1.2.3 -OutputPath $output

        $expected | Should -Match '^docker\.io/library/postgres:[^@]+@sha256:[0-9a-f]{64}$'
        Get-ImageReferences (Join-Path $output 'jim-database.yaml') | Should -Be @($expected)
    }

    It 'leaves no placeholder in any file' {
        $output = New-OutputPath

        & $script:ScriptPath -Version 1.2.3 -OutputPath $output

        $leftovers = Get-ChildItem $output -Recurse -File | Select-String -CaseSensitive -Pattern '__[A-Z_]+__'
        $leftovers | Should -BeNullOrEmpty
    }

    It 'copies every file, with the Quadlet units unchanged' {
        $output = New-OutputPath
        $source = Join-Path $script:RepoRoot 'deploy/podman'

        & $script:ScriptPath -Version 1.2.3 -OutputPath $output

        $expected = Get-ChildItem $source -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($source, $_.FullName) } | Sort-Object
        $actual = Get-ChildItem $output -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($output, $_.FullName) } | Sort-Object
        $actual | Should -Be $expected

        foreach ($unit in 'jim.kube', 'jim-database.kube', 'jim.network') {
            (Get-Content (Join-Path $output "quadlet/$unit") -Raw) -replace "`r`n", "`n" |
                Should -Be ((Get-Content (Join-Path $source "quadlet/$unit") -Raw) -replace "`r`n", "`n")
        }
    }

    It 'writes Linux line endings, whatever the checkout uses' {
        $output = New-OutputPath

        & $script:ScriptPath -Version 1.2.3 -OutputPath $output

        foreach ($file in Get-ChildItem $output -Recurse -File) {
            [IO.File]::ReadAllText($file.FullName) | Should -Not -Match "`r" -Because $file.Name
        }
    }

    It 'refuses a version that is not a release version' {
        $output = New-OutputPath

        { & $script:ScriptPath -Version '1.2.3; rm -rf /' -OutputPath $output } | Should -Throw '*not a release version*'
    }
}
