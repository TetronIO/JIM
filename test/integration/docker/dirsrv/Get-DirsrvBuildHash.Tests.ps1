# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-DirsrvBuildHash, the single content hash behind the 389 lab image.

.DESCRIPTION
    Build-DirsrvImage.ps1 labels the image with this hash and the runner compares it, so it must
    be stable for identical content, change for any fixture file, ignore the two scripts that
    consume it (so editing the build script does not force an image rebuild), and be insensitive
    to enumeration order.
#>

BeforeAll {
    . "$PSScriptRoot/Get-DirsrvBuildHash.ps1"

    function New-FixtureCopy {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) "jim-dirsrv-hash-$([System.Guid]::NewGuid())"
        New-Item -ItemType Directory -Path (Join-Path $root 'aci') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'Dockerfile') -Value 'FROM scratch' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'aci' 'a.ldif') -Value 'dn: cn=a' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Build-DirsrvImage.ps1') -Value '# build' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Get-DirsrvBuildHash.ps1') -Value '# hash' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Get-DirsrvBuildHash.Tests.ps1') -Value '# tests' -NoNewline
        return $root
    }
}

Describe 'Get-DirsrvBuildHash' {
    BeforeEach {
        $script:fixture = New-FixtureCopy
    }

    AfterEach {
        Remove-Item -LiteralPath $script:fixture -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'returns a 16-character lower-case hex string' {
        Get-DirsrvBuildHash -FixtureDirectory $script:fixture | Should -Match '^[0-9a-f]{16}$'
    }

    It 'is stable for identical content' {
        $first = Get-DirsrvBuildHash -FixtureDirectory $script:fixture
        $second = Get-DirsrvBuildHash -FixtureDirectory $script:fixture
        $second | Should -Be $first
    }

    It 'changes when a fixture file changes' {
        $before = Get-DirsrvBuildHash -FixtureDirectory $script:fixture
        Set-Content -LiteralPath (Join-Path $script:fixture 'aci' 'a.ldif') -Value 'dn: cn=b' -NoNewline
        Get-DirsrvBuildHash -FixtureDirectory $script:fixture | Should -Not -Be $before
    }

    It 'changes when a fixture file is added' {
        $before = Get-DirsrvBuildHash -FixtureDirectory $script:fixture
        Set-Content -LiteralPath (Join-Path $script:fixture 'aci' 'z.ldif') -Value 'dn: cn=z' -NoNewline
        Get-DirsrvBuildHash -FixtureDirectory $script:fixture | Should -Not -Be $before
    }

    It 'changes when a fixture file is renamed' {
        $before = Get-DirsrvBuildHash -FixtureDirectory $script:fixture
        Rename-Item -LiteralPath (Join-Path $script:fixture 'aci' 'a.ldif') -NewName 'renamed.ldif'
        Get-DirsrvBuildHash -FixtureDirectory $script:fixture | Should -Not -Be $before
    }

    It 'ignores the build script, the hash script and these tests' {
        $before = Get-DirsrvBuildHash -FixtureDirectory $script:fixture
        Set-Content -LiteralPath (Join-Path $script:fixture 'Build-DirsrvImage.ps1') -Value '# build, edited' -NoNewline
        Set-Content -LiteralPath (Join-Path $script:fixture 'Get-DirsrvBuildHash.ps1') -Value '# hash, edited' -NoNewline
        Set-Content -LiteralPath (Join-Path $script:fixture 'Get-DirsrvBuildHash.Tests.ps1') -Value '# tests, edited' -NoNewline
        Get-DirsrvBuildHash -FixtureDirectory $script:fixture | Should -Be $before
    }

    It 'hashes the real fixture directory without error' {
        Get-DirsrvBuildHash -FixtureDirectory $PSScriptRoot | Should -Match '^[0-9a-f]{16}$'
    }
}
