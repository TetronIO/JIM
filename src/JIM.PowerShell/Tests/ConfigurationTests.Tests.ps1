# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the configuration test case cmdlets (#2055 Phase 4): the request each builds, inputs returned by
    default, partial updates, confirmation on removal, and running cases by Synchronisation Rule, Connected System or
    piped test case.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConfigurationTestCase' {

    It 'Lists the cases covering a Synchronisation Rule with their inputs' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; @([PSCustomObject]@{ id = 4; name = 'Surname flows' }) }

            $result = Get-JIMConfigurationTestCase -SyncRuleId 12

            $script:capturedEndpoint | Should -Be '/api/v1/configuration-tests?syncRuleId=12&include=inputs'
            $result.name | Should -Be 'Surname flows'
        }
    }

    It 'Lists the cases under a Connected System' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; @() }

            Get-JIMConfigurationTestCase -ConnectedSystemId 3

            $script:capturedEndpoint | Should -Be '/api/v1/configuration-tests?connectedSystemId=3&include=inputs'
        }
    }

    It 'Gets one case by id' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; [PSCustomObject]@{ id = 4 } }

            Get-JIMConfigurationTestCase -Id 4

            $script:capturedEndpoint | Should -Be '/api/v1/configuration-tests/4'
        }
    }

    It 'Filters by a wildcard name' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { @([PSCustomObject]@{ id = 4; name = 'Surname flows' }, [PSCustomObject]@{ id = 5; name = 'Sales in scope' }) }

            (Get-JIMConfigurationTestCase -SyncRuleId 12 -Name 'Surname*').id | Should -Be 4
        }
    }
}

Describe 'New-JIMConfigurationTestCase' {

    It 'Creates an Attribute Flow case from the values given, a date as ISO 8601' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; $script:capturedMethod = $Method; $script:capturedBody = $Body; [PSCustomObject]@{ id = 4 } }

            New-JIMConfigurationTestCase -Name 'Hire date as ISO' -SyncRuleMappingId 9 `
                -CsAttributes @{ hireDate = [datetime]::new(2026, 3, 2, 0, 0, 0, [DateTimeKind]::Utc); manager = $null } `
                -Expect Values -ExpectedValues '2026-03-02' -Confirm:$false

            $script:capturedEndpoint | Should -Be '/api/v1/configuration-tests'
            $script:capturedMethod | Should -Be 'POST'
            $script:capturedBody.kind | Should -Be 'AttributeFlow'
            $script:capturedBody.syncRuleMappingId | Should -Be 9
            $script:capturedBody.csAttributes.hireDate | Should -Be '2026-03-02T00:00:00Z'
            $script:capturedBody.csAttributes.ContainsKey('manager') | Should -BeTrue
            $script:capturedBody.csAttributes.manager | Should -BeNullOrEmpty
            $script:capturedBody.expectation.kind | Should -Be 'Values'
            @($script:capturedBody.expectation.values) | Should -Be @('2026-03-02')
            $script:capturedBody.expectation.inOrder | Should -BeFalse
        }
    }

    It 'Creates a Generated Value case with -Kind' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedBody = $Body; [PSCustomObject]@{ id = 4 } }

            New-JIMConfigurationTestCase -Name 'Account name base' -SyncRuleMappingId 9 -Kind GeneratedValue `
                -MvAttributes @{ 'First Name' = 'Joe' } -Expect Values -ExpectedValues 'joe' -Confirm:$false

            $script:capturedBody.kind | Should -Be 'GeneratedValue'
            $script:capturedBody.mvAttributes.'First Name' | Should -Be 'Joe'
        }
    }

    It 'Creates a Scope case, which expects in or out of scope' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedBody = $Body; [PSCustomObject]@{ id = 4 } }

            New-JIMConfigurationTestCase -Name 'Sales in scope' -SyncRuleId 12 -Attributes @{ department = 'Sales' } `
                -Expect InScope -RunAsOf '2027-01-31' -Confirm:$false

            $script:capturedBody.kind | Should -Be 'Scope'
            $script:capturedBody.syncRuleId | Should -Be 12
            $script:capturedBody.attributes.department | Should -Be 'Sales'
            $script:capturedBody.expectation.kind | Should -Be 'InScope'
            $script:capturedBody.runAsOf | Should -Be '2027-01-31T00:00:00'
        }
    }

    It 'Creates an Object Matching Rule case with values in order' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedBody = $Body; [PSCustomObject]@{ id = 4 } }

            New-JIMConfigurationTestCase -Name 'Matches on employee number' -ObjectMatchingRuleId 6 `
                -Attributes @{ employeeNumber = '00123' } -Expect Values -ExpectedValues '00123', '123' -InOrder -Confirm:$false

            $script:capturedBody.kind | Should -Be 'ObjectMatchingRule'
            $script:capturedBody.objectMatchingRuleId | Should -Be 6
            @($script:capturedBody.expectation.values) | Should -Be @('00123', '123')
            $script:capturedBody.expectation.inOrder | Should -BeTrue
        }
    }

    It 'Will not bind two things to cover' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { }

            { New-JIMConfigurationTestCase -Name 'x' -SyncRuleId 1 -ObjectMatchingRuleId 2 -Expect AnyValue -Confirm:$false } |
                Should -Throw -ErrorId 'AmbiguousParameterSet,New-JIMConfigurationTestCase'
            Should -Invoke Invoke-JIMApi -Times 0 -Exactly
        }
    }
}

Describe 'Set-JIMConfigurationTestCase' {

    It 'Sends only what is given' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; $script:capturedMethod = $Method; $script:capturedBody = $Body; [PSCustomObject]@{ id = 4 } }

            Set-JIMConfigurationTestCase -Id 4 -Name 'Renamed' -Confirm:$false

            $script:capturedEndpoint | Should -Be '/api/v1/configuration-tests/4'
            $script:capturedMethod | Should -Be 'PUT'
            $script:capturedBody.name | Should -Be 'Renamed'
            $script:capturedBody.Keys | Should -Be @('name')
        }
    }

    It 'Clears the description with <Value>' -ForEach @(
        @{ Value = $null; Label = '$null' }
        @{ Value = ''; Label = 'an empty string' }
    ) {
        InModuleScope JIM -Parameters @{ Value = $Value } {
            param($Value)
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedBody = $Body; [PSCustomObject]@{ id = 4 } }

            Set-JIMConfigurationTestCase -Id 4 -Description $Value -Confirm:$false

            $script:capturedBody.ContainsKey('description') | Should -BeTrue
            $script:capturedBody.description | Should -Be ''
        }
    }

    It 'Clears the run-as-of time with -ClearRunAsOf' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedBody = $Body; [PSCustomObject]@{ id = 4 } }

            Set-JIMConfigurationTestCase -Id 4 -ClearRunAsOf -Confirm:$false

            $script:capturedBody.clearRunAsOf | Should -BeTrue
            $script:capturedBody.ContainsKey('runAsOf') | Should -BeFalse
        }
    }

    It 'Replaces the expectation when -Expect is given' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedBody = $Body; [PSCustomObject]@{ id = 4 } }

            Set-JIMConfigurationTestCase -Id 4 -Expect NoValue -Confirm:$false

            $script:capturedBody.expectation.kind | Should -Be 'NoValue'
            @($script:capturedBody.expectation.values).Count | Should -Be 0
        }
    }

    It 'Takes the case from the pipeline and returns it with -PassThru' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; [PSCustomObject]@{ id = 4; name = 'Renamed' } }

            $result = [PSCustomObject]@{ id = 4 } | Set-JIMConfigurationTestCase -Name 'Renamed' -PassThru -Confirm:$false

            $script:capturedEndpoint | Should -Be '/api/v1/configuration-tests/4'
            $result.name | Should -Be 'Renamed'
        }
    }
}

Describe 'Remove-JIMConfigurationTestCase' {

    It 'Deletes the case with -Force' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; $script:capturedMethod = $Method }

            Remove-JIMConfigurationTestCase -Id 4 -Force

            $script:capturedEndpoint | Should -Be '/api/v1/configuration-tests/4'
            $script:capturedMethod | Should -Be 'DELETE'
        }
    }

    It 'Deletes nothing with -WhatIf, even with -Force' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { }

            Remove-JIMConfigurationTestCase -Id 4 -Force -WhatIf

            Should -Invoke Invoke-JIMApi -Times 0 -Exactly
        }
    }
}

Describe 'Invoke-JIMConfigurationTest' {

    It 'Runs the cases covering a Synchronisation Rule' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; $script:capturedBody = $Body; @([PSCustomObject]@{ testCaseId = 4; status = 'Passed' }) }

            $result = Invoke-JIMConfigurationTest -SyncRuleId 12

            $script:capturedEndpoint | Should -Be '/api/v1/configuration-tests/run'
            $script:capturedBody.syncRuleId | Should -Be 12
            $result.status | Should -Be 'Passed'
        }
    }

    It 'Runs the cases under a Connected System' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedBody = $Body; @() }

            Invoke-JIMConfigurationTest -ConnectedSystemId 3

            $script:capturedBody.connectedSystemId | Should -Be 3
        }
    }

    It 'Runs every piped case in one request' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { $script:capturedBody = $Body; @() }

            @([PSCustomObject]@{ id = 4 }, [PSCustomObject]@{ id = 5 }) | Invoke-JIMConfigurationTest

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly
            @($script:capturedBody.ids) | Should -Be @(4, 5)
        }
    }

    It 'Writes an error for each case that did not pass, with -FailOnUnsuccessful' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                @(
                    [PSCustomObject]@{ testCaseId = 4; testCaseName = 'Surname flows'; status = 'Passed'; summary = 'Passed.' }
                    [PSCustomObject]@{ testCaseId = 5; testCaseName = 'Sales in scope'; status = 'Failed'; summary = 'Expected the object in scope.' }
                )
            }

            $result = Invoke-JIMConfigurationTest -SyncRuleId 12 -FailOnUnsuccessful -ErrorVariable failures -ErrorAction SilentlyContinue

            @($result).Count | Should -Be 2
            @($failures).Count | Should -Be 1
            "$($failures[0])" | Should -BeLike "*Sales in scope*Failed*"
        }
    }
}
