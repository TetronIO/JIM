# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-ScenarioDirectoryTypes.

.DESCRIPTION
    Run-IntegrationTests.ps1 asks these two questions in several places: which directory types can
    a scenario run against (the interactive menu, the -DirectoryType All coercion, the -Scenario All
    sweep filter), and does a scenario use a directory at all (so a -DirectoryType All run doesn't
    repeat a directory-agnostic scenario once per directory type). These tests pin down the answers
    for the scenarios with special rules, plus a scenario with no restriction, so a future hand-copy
    of the wrong list is caught here rather than at the next Pre-Release run.
#>

BeforeAll {
    . "$PSScriptRoot/Get-ScenarioDirectoryTypes.ps1"
}

Describe 'Get-ScenarioSupportedDirectoryTypes' {
    It 'restricts Scenario 014 (Attribute Priority) to OpenLDAP' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 14 | Should -Be @('OpenLDAP')
    }

    It 'restricts Scenario 019 (Auxiliary Classes) to OpenLDAP' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 19 | Should -Be @('OpenLDAP')
    }

    It 'restricts Scenario 022 (OpenLDAP Password Policy) to OpenLDAP' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 22 | Should -Be @('OpenLDAP')
    }

    It 'restricts Scenario 017 (Initial Password) to Samba AD' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 17 | Should -Be @('SambaAD')
    }

    It 'restricts Scenario 023 (Unique Value Generation) to OpenLDAP and Samba AD, excluding 389 Directory Server' {
        $result = Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 23
        $result | Should -Contain 'SambaAD'
        $result | Should -Contain 'OpenLDAP'
        $result | Should -Not -Contain 'DirectoryServer389'
        $result.Count | Should -Be 2
    }

    It 'returns all three directory types for a scenario with no restriction' {
        $result = Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 1
        $result | Should -Be @('SambaAD', 'OpenLDAP', 'DirectoryServer389')
    }

    It 'returns all three directory types for the directory-agnostic scenarios too (011, 015, 016)' {
        # Directory-agnostic is a separate question (Test-ScenarioIsDirectoryAgnostic): these
        # scenarios are architecturally able to run against any directory, they just don't need one.
        foreach ($number in 11, 15, 16) {
            Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $number | Should -Be @('SambaAD', 'OpenLDAP', 'DirectoryServer389')
        }
    }

    It 'always returns an array, even for a single-directory-type restriction' {
        $result = @(Get-ScenarioSupportedDirectoryTypes -ScenarioNumber 17)
        $result.GetType().IsArray | Should -Be $true
        $result.Count | Should -Be 1
    }

    It 'returns all three directory types for $null (the "All" scenario has no restriction)' {
        Get-ScenarioSupportedDirectoryTypes -ScenarioNumber $null | Should -Be @('SambaAD', 'OpenLDAP', 'DirectoryServer389')
    }
}

Describe 'Test-ScenarioIsDirectoryAgnostic' {
    It 'is true for Scenario 011 (Scoping Criteria Matrix)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 11 | Should -Be $true
    }

    It 'is true for Scenario 015 (SCIM Connector)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 15 | Should -Be $true
    }

    It 'is true for Scenario 016 (JIM SQL Connector matrix)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 16 | Should -Be $true
    }

    It 'is false for a scenario that does use a directory' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 1 | Should -Be $false
    }

    It 'is false for a directory-restricted scenario (it uses a directory, just only one kind)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber 17 | Should -Be $false
    }

    It 'is false for $null (the "All" scenario)' {
        Test-ScenarioIsDirectoryAgnostic -ScenarioNumber $null | Should -Be $false
    }
}
