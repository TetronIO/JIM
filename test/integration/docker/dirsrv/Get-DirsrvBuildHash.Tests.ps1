# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the 389 lab's hash and tag contract in Get-DirsrvBuildHash.ps1.

.DESCRIPTION
    Get-DirsrvBuildHash: Build-DirsrvImage.ps1 labels the image with this hash and the runner
    compares it, so it must be stable for identical content, change for any fixture file, ignore
    the two scripts that consume it (so editing the build script does not force an image rebuild),
    and be insensitive to enumeration order.

    Get-DirsrvSnapshotHash, Get-DirsrvSnapshotImageTag and Test-DirsrvSnapshotCurrent: the
    snapshot side of the same contract, shared by Build-DirsrvSnapshots.ps1 and the runner. The
    snapshot hash must follow the populate script for its own scenario and ignore the other
    scenario's; the tag shape is fixed; a snapshot is current only when its image exists and both
    its snapshot-hash and base-hash labels match. docker is mocked (a function of that name is
    declared so Pester has something to replace), so nothing here needs a daemon.
#>

BeforeAll {
    . "$PSScriptRoot/Get-DirsrvBuildHash.ps1"

    # Declared so Mock has a command to replace; Test-DirsrvSnapshotCurrent calls docker as a
    # plain command, and a function of that name takes precedence over the executable.
    function docker { throw "docker was called without a mock: $($args -join ' ')" }

    function New-IntegrationRootCopy {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) "jim-dirsrv-snapshot-hash-$([System.Guid]::NewGuid())"
        New-Item -ItemType Directory -Path (Join-Path $root 'utils') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'utils' 'Test-Helpers.ps1') -Value '# helpers' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'utils' 'Test-GroupHelpers.ps1') -Value '# group helpers' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Build-DirsrvSnapshots.ps1') -Value '# snapshots' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Populate-OpenLDAP.ps1') -Value '# populate general' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Populate-OpenLDAP-Scenario8.ps1') -Value '# populate s8' -NoNewline
        return $root
    }

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

Describe 'Get-DirsrvSnapshotHash' {
    BeforeEach {
        $script:integrationRoot = New-IntegrationRootCopy
    }

    AfterEach {
        Remove-Item -LiteralPath $script:integrationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'returns a 16-character lower-case hex string' {
        Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Match '^[0-9a-f]{16}$'
    }

    It 'is stable for identical content' {
        $first = Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        $second = Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        $second | Should -Be $first
    }

    It 'changes when the General populate script changes' {
        $before = Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'Populate-OpenLDAP.ps1') -Value '# populate general, edited' -NoNewline
        Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Not -Be $before
    }

    It 'changes when the Scenario8 populate script changes' {
        $before = Get-DirsrvSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'Populate-OpenLDAP-Scenario8.ps1') -Value '# populate s8, edited' -NoNewline
        Get-DirsrvSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot | Should -Not -Be $before
    }

    It 'changes when a shared helper changes' {
        $before = Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'utils' 'Test-GroupHelpers.ps1') -Value '# group helpers, edited' -NoNewline
        Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Not -Be $before
    }

    It 'differs between General and Scenario8' {
        $general = Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        $scenario8 = Get-DirsrvSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot
        $scenario8 | Should -Not -Be $general
    }

    It 'ignores a change to the other scenario''s populate script' {
        $generalBefore = Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        $scenario8Before = Get-DirsrvSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'Populate-OpenLDAP-Scenario8.ps1') -Value '# populate s8, edited' -NoNewline
        Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Be $generalBefore
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'Populate-OpenLDAP.ps1') -Value '# populate general, edited' -NoNewline
        Get-DirsrvSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot | Should -Not -Be $scenario8Before
    }

    It 'skips a missing file rather than failing' {
        Remove-Item -LiteralPath (Join-Path $script:integrationRoot 'Build-DirsrvSnapshots.ps1')
        Get-DirsrvSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Match '^[0-9a-f]{16}$'
    }

    It 'hashes the real integration directory without error' {
        Get-DirsrvSnapshotHash -Scenario General | Should -Match '^[0-9a-f]{16}$'
    }
}

Describe 'Get-DirsrvSnapshotImageTag' {
    It 'names the general snapshot jim-dirsrv:general-{template in lower case}' {
        Get-DirsrvSnapshotImageTag -Role general -Template Small | Should -Be 'jim-dirsrv:general-small'
    }

    It 'names the Scenario 8 snapshot jim-dirsrv:s8-{template in lower case}' {
        Get-DirsrvSnapshotImageTag -Role s8 -Template Nano | Should -Be 'jim-dirsrv:s8-nano'
    }

    It 'lower-cases a mixed-case template name' {
        Get-DirsrvSnapshotImageTag -Role general -Template Scale100k5kGroups | Should -Be 'jim-dirsrv:general-scale100k5kgroups'
    }
}

Describe 'Test-DirsrvSnapshotCurrent' {
    BeforeAll {
        # Answers docker image inspect for one image with the given labels; a $null label is
        # absent. The mock body runs in Pester's scope, not this function's, so its inputs are
        # held in script scope rather than captured from the parameters.
        function Set-DockerImageMock {
            param([string]$Tag, [string]$SnapshotHash, [string]$BaseHash)
            $script:mockImageTag = $Tag
            $script:mockImageLabels = @{ 'jim.dirsrv.snapshot-hash' = $SnapshotHash; 'jim.dirsrv.base-hash' = $BaseHash }
            Mock docker {
                if ($args[0] -ne 'image' -or $args[1] -ne 'inspect' -or $args[2] -ne $script:mockImageTag) {
                    $global:LASTEXITCODE = 1
                    return "Error: No such image: $($args[2])"
                }
                $format = "$($args[4])"
                $key = @($script:mockImageLabels.Keys | Where-Object { $format -like "*`"$_`"*" }) | Select-Object -First 1
                $global:LASTEXITCODE = 0
                # docker prints <no value> for a label the image does not carry.
                if ($null -eq $key -or [string]::IsNullOrEmpty($script:mockImageLabels[$key])) { return '<no value>' }
                return $script:mockImageLabels[$key]
            }
        }
    }

    It 'is false when the image does not exist' {
        Mock docker { $global:LASTEXITCODE = 1; return 'Error: No such image: jim-dirsrv:general-nano' }
        Test-DirsrvSnapshotCurrent -ImageTag 'jim-dirsrv:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
    }

    It 'is false when the snapshot-hash label is missing' {
        Set-DockerImageMock -Tag 'jim-dirsrv:general-nano' -SnapshotHash $null -BaseHash 'bbbbbbbbbbbbbbbb'
        Test-DirsrvSnapshotCurrent -ImageTag 'jim-dirsrv:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
    }

    It 'is false when the base-hash label is missing' {
        Set-DockerImageMock -Tag 'jim-dirsrv:general-nano' -SnapshotHash 'aaaaaaaaaaaaaaaa' -BaseHash $null
        Test-DirsrvSnapshotCurrent -ImageTag 'jim-dirsrv:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
        # The snapshot hash matched, so the base label was read: the false comes from it.
        Should -Invoke docker -Times 2 -Exactly
    }

    It 'is false when the snapshot hash differs' {
        Set-DockerImageMock -Tag 'jim-dirsrv:general-nano' -SnapshotHash '0000000000000000' -BaseHash 'bbbbbbbbbbbbbbbb'
        Test-DirsrvSnapshotCurrent -ImageTag 'jim-dirsrv:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
    }

    It 'is false when the base hash differs (a snapshot baked from a stale base is stale)' {
        Set-DockerImageMock -Tag 'jim-dirsrv:general-nano' -SnapshotHash 'aaaaaaaaaaaaaaaa' -BaseHash '0000000000000000'
        Test-DirsrvSnapshotCurrent -ImageTag 'jim-dirsrv:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
    }

    It 'is true when the image exists and both labels match' {
        Set-DockerImageMock -Tag 'jim-dirsrv:general-nano' -SnapshotHash 'aaaaaaaaaaaaaaaa' -BaseHash 'bbbbbbbbbbbbbbbb'
        Test-DirsrvSnapshotCurrent -ImageTag 'jim-dirsrv:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeTrue
        Should -Invoke docker -Times 2 -Exactly
    }
}

