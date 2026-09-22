# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the OpenLDAP lab's hash and tag contract in Get-OpenLDAPBuildHash.ps1.

.DESCRIPTION
    Get-OpenLDAPBuildHash: Build-OpenLdapImage.ps1 labels the image with this hash and the runner
    compares it, so it must be stable for identical content, change for any fixture file, ignore
    the two scripts that consume it (so editing the build script does not force an image rebuild),
    and be insensitive to enumeration order.

    Get-OpenLDAPSnapshotHash, Get-OpenLDAPSnapshotImageTag and Test-OpenLDAPSnapshotCurrent: the
    snapshot side of the same contract, shared by Build-OpenLDAPSnapshots.ps1 and the runner. The
    snapshot hash must follow the populate script for its own scenario and ignore the other
    scenario's; the tag shape is fixed; a snapshot is current only when its image exists and both
    its snapshot-hash and base-hash labels match, and a rejection says why. docker is mocked (a
    function of that name is declared so Pester has something to replace), so nothing here needs
    a daemon.
#>

BeforeAll {
    . "$PSScriptRoot/Get-OpenLDAPBuildHash.ps1"

    # Declared so Mock has a command to replace; Test-OpenLDAPSnapshotCurrent calls docker as a
    # plain command, and a function of that name takes precedence over the executable.
    function docker { throw "docker was called without a mock: $($args -join ' ')" }

    function New-IntegrationRootCopy {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) "jim-openldap-snapshot-hash-$([System.Guid]::NewGuid())"
        New-Item -ItemType Directory -Path (Join-Path $root 'utils') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'utils' 'Test-Helpers.ps1') -Value '# helpers' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'utils' 'Test-GroupHelpers.ps1') -Value '# group helpers' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Build-OpenLDAPSnapshots.ps1') -Value '# snapshots' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Populate-OpenLDAP.ps1') -Value '# populate general' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Populate-OpenLDAP-Scenario8.ps1') -Value '# populate s8' -NoNewline
        return $root
    }

    function New-FixtureCopy {
        $root = Join-Path ([System.IO.Path]::GetTempPath()) "jim-openldap-hash-$([System.Guid]::NewGuid())"
        New-Item -ItemType Directory -Path (Join-Path $root 'acl') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'Dockerfile') -Value 'FROM scratch' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'acl' 'a.ldif') -Value 'dn: cn=a' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Build-OpenLdapImage.ps1') -Value '# build' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Get-OpenLDAPBuildHash.ps1') -Value '# hash' -NoNewline
        Set-Content -LiteralPath (Join-Path $root 'Get-OpenLDAPBuildHash.Tests.ps1') -Value '# tests' -NoNewline
        return $root
    }
}

Describe 'Get-OpenLDAPBuildHash' {
    BeforeEach {
        $script:fixture = New-FixtureCopy
    }

    AfterEach {
        Remove-Item -LiteralPath $script:fixture -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'returns a 16-character lower-case hex string' {
        Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture | Should -Match '^[0-9a-f]{16}$'
    }

    It 'is stable for identical content' {
        $first = Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture
        $second = Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture
        $second | Should -Be $first
    }

    It 'changes when a fixture file changes' {
        $before = Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture
        Set-Content -LiteralPath (Join-Path $script:fixture 'acl' 'a.ldif') -Value 'dn: cn=b' -NoNewline
        Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture | Should -Not -Be $before
    }

    It 'changes when a fixture file is added' {
        $before = Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture
        Set-Content -LiteralPath (Join-Path $script:fixture 'acl' 'z.ldif') -Value 'dn: cn=z' -NoNewline
        Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture | Should -Not -Be $before
    }

    It 'changes when a fixture file is renamed' {
        $before = Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture
        Rename-Item -LiteralPath (Join-Path $script:fixture 'acl' 'a.ldif') -NewName 'renamed.ldif'
        Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture | Should -Not -Be $before
    }

    It 'ignores the build script, the hash script and these tests' {
        $before = Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture
        Set-Content -LiteralPath (Join-Path $script:fixture 'Build-OpenLdapImage.ps1') -Value '# build, edited' -NoNewline
        Set-Content -LiteralPath (Join-Path $script:fixture 'Get-OpenLDAPBuildHash.ps1') -Value '# hash, edited' -NoNewline
        Set-Content -LiteralPath (Join-Path $script:fixture 'Get-OpenLDAPBuildHash.Tests.ps1') -Value '# tests, edited' -NoNewline
        Get-OpenLDAPBuildHash -FixtureDirectory $script:fixture | Should -Be $before
    }

    It 'hashes the real fixture directory without error' {
        Get-OpenLDAPBuildHash -FixtureDirectory $PSScriptRoot | Should -Match '^[0-9a-f]{16}$'
    }
}

Describe 'Get-OpenLDAPSnapshotHash' {
    BeforeEach {
        $script:integrationRoot = New-IntegrationRootCopy
    }

    AfterEach {
        Remove-Item -LiteralPath $script:integrationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'returns a 16-character lower-case hex string' {
        Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Match '^[0-9a-f]{16}$'
    }

    It 'is stable for identical content' {
        $first = Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        $second = Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        $second | Should -Be $first
    }

    It 'changes when the General populate script changes' {
        $before = Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'Populate-OpenLDAP.ps1') -Value '# populate general, edited' -NoNewline
        Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Not -Be $before
    }

    It 'changes when the Scenario8 populate script changes' {
        $before = Get-OpenLDAPSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'Populate-OpenLDAP-Scenario8.ps1') -Value '# populate s8, edited' -NoNewline
        Get-OpenLDAPSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot | Should -Not -Be $before
    }

    It 'changes when a shared helper changes' {
        $before = Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'utils' 'Test-GroupHelpers.ps1') -Value '# group helpers, edited' -NoNewline
        Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Not -Be $before
    }

    It 'differs between General and Scenario8' {
        $general = Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        $scenario8 = Get-OpenLDAPSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot
        $scenario8 | Should -Not -Be $general
    }

    It 'ignores a change to the other scenario''s populate script' {
        $generalBefore = Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot
        $scenario8Before = Get-OpenLDAPSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'Populate-OpenLDAP-Scenario8.ps1') -Value '# populate s8, edited' -NoNewline
        Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Be $generalBefore
        Set-Content -LiteralPath (Join-Path $script:integrationRoot 'Populate-OpenLDAP.ps1') -Value '# populate general, edited' -NoNewline
        Get-OpenLDAPSnapshotHash -Scenario Scenario8 -IntegrationRoot $script:integrationRoot | Should -Not -Be $scenario8Before
    }

    It 'skips a missing file rather than failing' {
        Remove-Item -LiteralPath (Join-Path $script:integrationRoot 'Build-OpenLDAPSnapshots.ps1')
        Get-OpenLDAPSnapshotHash -Scenario General -IntegrationRoot $script:integrationRoot | Should -Match '^[0-9a-f]{16}$'
    }

    It 'hashes the real integration directory without error' {
        Get-OpenLDAPSnapshotHash -Scenario General | Should -Match '^[0-9a-f]{16}$'
    }
}

Describe 'Get-OpenLDAPSnapshotImageTag' {
    It 'names the general snapshot jim-openldap:general-{template in lower case}' {
        Get-OpenLDAPSnapshotImageTag -Role general -Template Small | Should -Be 'jim-openldap:general-small'
    }

    It 'names the Scenario 8 snapshot jim-openldap:s8-{template in lower case}' {
        Get-OpenLDAPSnapshotImageTag -Role s8 -Template Nano | Should -Be 'jim-openldap:s8-nano'
    }

    It 'lower-cases a mixed-case template name' {
        Get-OpenLDAPSnapshotImageTag -Role general -Template Scale100k5kGroups | Should -Be 'jim-openldap:general-scale100k5kgroups'
    }

    It 'prefixes the registry when one is given' {
        Get-OpenLDAPSnapshotImageTag -Role general -Template Small -Registry 'ghcr.io/tetronio/' | Should -Be 'ghcr.io/tetronio/jim-openldap:general-small'
    }
}

Describe 'Test-OpenLDAPSnapshotCurrent' {
    BeforeAll {
        # Answers docker image inspect for one image with the given labels; a $null label is
        # absent. The mock body runs in Pester's scope, not this function's, so its inputs are
        # held in script scope rather than captured from the parameters.
        function Set-DockerImageMock {
            param([string]$Tag, [string]$SnapshotHash, [string]$BaseHash)
            $script:mockImageTag = $Tag
            $script:mockImageLabels = @{ 'jim.openldap.snapshot-hash' = $SnapshotHash; 'jim.openldap.base-hash' = $BaseHash }
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
        Mock docker { $global:LASTEXITCODE = 1; return 'Error: No such image: jim-openldap:general-nano' }
        Test-OpenLDAPSnapshotCurrent -ImageTag 'jim-openldap:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
    }

    It 'is false when the snapshot-hash label is missing' {
        Set-DockerImageMock -Tag 'jim-openldap:general-nano' -SnapshotHash $null -BaseHash 'bbbbbbbbbbbbbbbb'
        Test-OpenLDAPSnapshotCurrent -ImageTag 'jim-openldap:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
    }

    It 'is false when the base-hash label is missing' {
        Set-DockerImageMock -Tag 'jim-openldap:general-nano' -SnapshotHash 'aaaaaaaaaaaaaaaa' -BaseHash $null
        Test-OpenLDAPSnapshotCurrent -ImageTag 'jim-openldap:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
        # The snapshot hash matched, so the base label was read: the false comes from it.
        Should -Invoke docker -Times 2 -Exactly
    }

    It 'is false when the snapshot hash differs' {
        Set-DockerImageMock -Tag 'jim-openldap:general-nano' -SnapshotHash '0000000000000000' -BaseHash 'bbbbbbbbbbbbbbbb'
        Test-OpenLDAPSnapshotCurrent -ImageTag 'jim-openldap:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
    }

    It 'is false when the base hash differs (a snapshot baked from a stale base is stale)' {
        Set-DockerImageMock -Tag 'jim-openldap:general-nano' -SnapshotHash 'aaaaaaaaaaaaaaaa' -BaseHash '0000000000000000'
        Test-OpenLDAPSnapshotCurrent -ImageTag 'jim-openldap:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
    }

    It 'is true when the image exists and both labels match' {
        Set-DockerImageMock -Tag 'jim-openldap:general-nano' -SnapshotHash 'aaaaaaaaaaaaaaaa' -BaseHash 'bbbbbbbbbbbbbbbb'
        Test-OpenLDAPSnapshotCurrent -ImageTag 'jim-openldap:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeTrue
        Should -Invoke docker -Times 2 -Exactly
    }

    It 'says why it rejected a snapshot whose hash differs' {
        Mock Write-Host {}
        Set-DockerImageMock -Tag 'jim-openldap:general-nano' -SnapshotHash '0000000000000000' -BaseHash 'bbbbbbbbbbbbbbbb'
        Test-OpenLDAPSnapshotCurrent -ImageTag 'jim-openldap:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
        Should -Invoke Write-Host -Times 1 -Exactly -ParameterFilter { "$Object" -like "*snapshot hash '0000000000000000' != aaaaaaaaaaaaaaaa*" }
    }

    It 'says why it rejected a snapshot baked from a stale base' {
        Mock Write-Host {}
        Set-DockerImageMock -Tag 'jim-openldap:general-nano' -SnapshotHash 'aaaaaaaaaaaaaaaa' -BaseHash '0000000000000000'
        Test-OpenLDAPSnapshotCurrent -ImageTag 'jim-openldap:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeFalse
        Should -Invoke Write-Host -Times 1 -Exactly -ParameterFilter { "$Object" -like "*baked from base '0000000000000000', current base is bbbbbbbbbbbbbbbb*" }
    }

    It 'says nothing when the snapshot is current' {
        Mock Write-Host {}
        Set-DockerImageMock -Tag 'jim-openldap:general-nano' -SnapshotHash 'aaaaaaaaaaaaaaaa' -BaseHash 'bbbbbbbbbbbbbbbb'
        Test-OpenLDAPSnapshotCurrent -ImageTag 'jim-openldap:general-nano' -ExpectedSnapshotHash 'aaaaaaaaaaaaaaaa' -ExpectedBaseHash 'bbbbbbbbbbbbbbbb' | Should -BeTrue
        Should -Invoke Write-Host -Times 0 -Exactly
    }
}

