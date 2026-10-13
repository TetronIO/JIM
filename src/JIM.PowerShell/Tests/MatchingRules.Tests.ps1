# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Object Matching Rule cmdlets: a rule's source is a Connected System attribute or an
    expression (#2052), never both, and the request each cmdlet builds says which.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Object Matching Rule source parameters' {

    Context '<Name>' -ForEach @(
        @{ Name = 'New-JIMMatchingRule' }
        @{ Name = 'Set-JIMMatchingRule' }
        @{ Name = 'New-JIMSyncRuleMatchingRule' }
        @{ Name = 'Set-JIMSyncRuleMatchingRule' }
    ) {

        BeforeAll {
            $command = Get-Command $Name
        }

        It 'Offers -SourceExpression' {
            $command.Parameters['SourceExpression'] | Should -Not -BeNullOrEmpty
            $command.Parameters['SourceExpression'].ParameterType | Should -Be ([string])
        }

        It 'Keeps -SourceAttributeId and -SourceExpression in different parameter sets' {
            $attributeSets = $command.Parameters['SourceAttributeId'].ParameterSets.Keys
            $expressionSets = $command.Parameters['SourceExpression'].ParameterSets.Keys

            $attributeSets | Should -Contain 'Attribute'
            $expressionSets | Should -Contain 'Expression'
            $attributeSets | Should -Not -Contain 'Expression'
            $expressionSets | Should -Not -Contain 'Attribute'
        }

        It 'Requires the source that names its parameter set' {
            $command.Parameters['SourceAttributeId'].ParameterSets['Attribute'].IsMandatory | Should -BeTrue
            $command.Parameters['SourceExpression'].ParameterSets['Expression'].IsMandatory | Should -BeTrue
        }
    }

    Context 'Refusing both sources at once' {

        It '<Name> will not bind an attribute and an expression together' -ForEach @(
            @{ Name = 'New-JIMMatchingRule'; Arguments = @{ ConnectedSystemId = 1; ObjectTypeId = 10; MetaverseObjectTypeId = 1; TargetMetaverseAttributeId = 5 } }
            @{ Name = 'Set-JIMMatchingRule'; Arguments = @{ ConnectedSystemId = 1; Id = 7 } }
            @{ Name = 'New-JIMSyncRuleMatchingRule'; Arguments = @{ SyncRuleId = 3; TargetMetaverseAttributeId = 5 } }
            @{ Name = 'Set-JIMSyncRuleMatchingRule'; Arguments = @{ SyncRuleId = 3; Id = 7 } }
        ) {
            InModuleScope JIM -Parameters @{ Name = $Name; Arguments = $Arguments } {
                param($Name, $Arguments)
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 7 } }

                { & $Name @Arguments -SourceAttributeId 25 -SourceExpression 'Trim(cs["employeeId"])' -Confirm:$false } |
                    Should -Throw -ErrorId 'AmbiguousParameterSet,*'

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }
}

Describe 'New-JIMMatchingRule' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
        }
    }

    It 'Sends an expression source with no attribute' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

            New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -MetaverseObjectTypeId 1 -TargetMetaverseAttributeId 5 `
                -SourceExpression '"E" + Trim(cs["employeeId"])' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/1/matching-rules' -and
                $Method -eq 'POST' -and
                @($Body.sources).Count -eq 1 -and
                $Body.sources[0].expression -eq '"E" + Trim(cs["employeeId"])' -and
                -not $Body.sources[0].ContainsKey('connectedSystemAttributeId')
            }
        }
    }

    It 'Sends an attribute source with no expression' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

            New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -MetaverseObjectTypeId 1 -TargetMetaverseAttributeId 5 `
                -SourceAttributeId 25 -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Body.sources[0].connectedSystemAttributeId -eq 25 -and -not $Body.sources[0].ContainsKey('expression')
            }
        }
    }

    It 'Refuses an empty expression rather than sending a rule with nothing to match on' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

            { New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -MetaverseObjectTypeId 1 -TargetMetaverseAttributeId 5 `
                -SourceExpression ' ' -Confirm:$false } | Should -Throw

            Should -Invoke Invoke-JIMApi -Times 0 -Exactly
        }
    }
}

Describe 'Set-JIMMatchingRule' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
        }
    }

    It 'Replaces the source with an expression' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 7 } }

            Set-JIMMatchingRule -ConnectedSystemId 1 -Id 7 -SourceExpression 'Trim(cs["employeeId"])' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/1/matching-rules/7' -and
                $Method -eq 'PUT' -and
                $Body.sources[0].expression -eq 'Trim(cs["employeeId"])' -and
                -not $Body.sources[0].ContainsKey('connectedSystemAttributeId')
            }
        }
    }

    It 'Leaves the source alone when neither source parameter is given' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 7 } }

            Set-JIMMatchingRule -ConnectedSystemId 1 -Id 7 -CaseSensitive $true -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                -not $Body.ContainsKey('sources') -and $Body.caseSensitive -eq $true
            }
        }
    }
}

Describe 'New-JIMSyncRuleMatchingRule' {

    It 'Sends an expression source with no attribute' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

            New-JIMSyncRuleMatchingRule -SyncRuleId 3 -TargetMetaverseAttributeId 5 -SourceExpression 'Lower(cs["mail"])' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/sync-rules/3/matching-rules' -and
                $Body.sources[0].expression -eq 'Lower(cs["mail"])' -and
                -not $Body.sources[0].ContainsKey('connectedSystemAttributeId')
            }
        }
    }
}

Describe 'Set-JIMSyncRuleMatchingRule' {

    It 'Replaces the source with an expression' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 7 } }

            Set-JIMSyncRuleMatchingRule -SyncRuleId 3 -Id 7 -SourceExpression 'Lower(cs["mail"])' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/sync-rules/3/matching-rules/7' -and
                $Body.sources[0].expression -eq 'Lower(cs["mail"])'
            }
        }
    }

    It 'Replaces the source with an attribute' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 7 } }

            Set-JIMSyncRuleMatchingRule -SyncRuleId 3 -Id 7 -SourceAttributeId 25 -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Body.sources[0].connectedSystemAttributeId -eq 25 -and -not $Body.sources[0].ContainsKey('expression')
            }
        }
    }
}

Describe 'Test-JIMMatchingRule' {
    It 'Posts the samples to the Connected System rule test endpoint, without a lookup by default' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedEndpoint = $null
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; outcome = 'Values'; valueLines = @('48213') }
            }

            Test-JIMMatchingRule -ConnectedSystemId 3 -Id 40 -Attributes @{ employeeNumber = 'EMP-48213' } | Out-Null

            $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/connected-systems/3/matching-rules/40/test'
            $script:capturedBody.attributes.employeeNumber | Should -Be 'EMP-48213'
            $script:capturedBody.lookUp | Should -BeFalse
        }
    }

    It 'Posts to the Synchronisation Rule endpoint with the object and the lookup' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedEndpoint = $null
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; outcome = 'Values' }
            }

            Test-JIMMatchingRule -SyncRuleId 12 -Id 7 -ConnectedSystemObjectId '0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01' -LookUp | Out-Null

            $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/sync-rules/12/matching-rules/7/test'
            $script:capturedBody.lookUp | Should -BeTrue
            $script:capturedBody.connectedSystemObjectId | Should -Be ([guid]'0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01')
        }
    }

    It 'Refuses a Connected System and a Synchronisation Rule together' {
        { Test-JIMMatchingRule -ConnectedSystemId 3 -SyncRuleId 12 -Id 7 } | Should -Throw -ErrorId 'AmbiguousParameterSet,Test-JIMMatchingRule'
    }

    It 'Returns the API object, so the lookup reaches the caller' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    IsValid = $true
                    Outcome = 'Values'
                    Lookup  = [PSCustomObject]@{ Outcome = 'WouldJoin'; MetaverseObjectName = 'Jo Bloggs' }
                }
            }

            $result = Test-JIMMatchingRule -ConnectedSystemId 3 -Id 40 -LookUp

            $result.Lookup.Outcome | Should -Be 'WouldJoin'
            $result.Lookup.MetaverseObjectName | Should -Be 'Jo Bloggs'
        }
    }
}
