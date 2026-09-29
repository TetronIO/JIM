# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for scripts/Get-PostgresImageReference.ps1.

.DESCRIPTION
    The bundled database's image is pinned by digest in docker-compose.yml, as the default of JIM_DB_IMAGE so that an
    air-gapped installation on Docker's classic image store can run the loaded image by its ID instead. The release
    bundle and the Podman files read that default; these tests check they get the pinned reference itself, never
    the variable syntax around it.
#>

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' 'Get-PostgresImageReference.ps1')).Path
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $script:Pinned = 'docker.io/library/postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722'

    function New-ComposeFile {
        param([string]$ImageLine)
        $path = Join-Path ([System.IO.Path]::GetTempPath()) "$([Guid]::NewGuid().ToString('N')).yml"
        "services:`n  jim.database:`n    container_name: jim.database`n    $ImageLine`n    profiles:`n      - with-db`n" |
            Set-Content -NoNewline $path
        $path
    }
}

Describe 'Get-PostgresImageReference' {
    It 'returns the default of JIM_DB_IMAGE, without the variable syntax' {
        $compose = New-ComposeFile "image: `${JIM_DB_IMAGE:-$($script:Pinned)}"

        & $script:ScriptPath -ComposePath $compose | Should -Be $script:Pinned
    }

    It 'returns a reference given without a variable' {
        $compose = New-ComposeFile "image: $($script:Pinned)"

        & $script:ScriptPath -ComposePath $compose | Should -Be $script:Pinned
    }

    It 'refuses a compose file that does not pin PostgreSQL by digest' {
        $compose = New-ComposeFile 'image: docker.io/library/postgres:18.6'

        { & $script:ScriptPath -ComposePath $compose } | Should -Throw '*digest*'
    }

    It 'refuses a compose file with no PostgreSQL image' {
        $compose = New-ComposeFile 'image: docker.io/library/redis:7'

        { & $script:ScriptPath -ComposePath $compose } | Should -Throw '*PostgreSQL*'
    }

    It 'reads docker-compose.yml by default, where JIM_DB_IMAGE can override the pinned image' {
        $compose = Get-Content (Join-Path $script:RepoRoot 'docker-compose.yml') -Raw

        $reference = & $script:ScriptPath

        $reference | Should -Match '^docker\.io/library/postgres:[^@\s]+@sha256:[0-9a-f]{64}$'
        $compose | Should -Match ([regex]::Escape("image: `${JIM_DB_IMAGE:-$reference}"))
    }
}
