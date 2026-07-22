# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Synchronisation Rule Mapping cmdlets, including inbound value processing (#843).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'New-JIMSyncRuleMapping' {

    Context 'Inbound value processing parameters' {

        BeforeAll {
            $command = Get-Command New-JIMSyncRuleMapping
        }

        It 'Should have a <Name> parameter' -ForEach @(
            @{ Name = 'PreserveWhitespace' }
            @{ Name = 'TrimWhitespace' }
            @{ Name = 'CollapseInternalWhitespace' }
            @{ Name = 'CaseNormalisation' }
        ) {
            $command.Parameters[$Name] | Should -Not -BeNullOrEmpty
        }

        It 'CaseNormalisation should validate against None/Upper/Lower/Title' {
            $param = $command.Parameters['CaseNormalisation']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Be @('None', 'Upper', 'Lower', 'Title')
        }

        It 'TrimWhitespace should be available only on import parameter sets' {
            $setNames = $command.Parameters['TrimWhitespace'].ParameterSets.Keys
            $setNames | Should -Contain 'ImportAttribute'
            $setNames | Should -Contain 'ImportExpression'
            $setNames | Should -Not -Contain 'ExportAttribute'
            $setNames | Should -Not -Contain 'ExportExpression'
        }
    }

    Context 'Request body composition' {

        It 'Treats whitespace as no value by default' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.inboundValueProcessing -eq 'TreatWhitespaceAsNoValue' -and $Body.caseNormalisation -eq 'None'
                }
            }
        }

        It 'Composes the flags set and case from the switches' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 `
                    -TrimWhitespace -CollapseInternalWhitespace -CaseNormalisation Lower -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.inboundValueProcessing -eq 'TreatWhitespaceAsNoValue, TrimWhitespace, CollapseInternalWhitespace' -and
                    $Body.caseNormalisation -eq 'Lower'
                }
            }
        }

        It 'Sends None when -PreserveWhitespace is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -PreserveWhitespace -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.inboundValueProcessing -eq 'None'
                }
            }
        }
    }

    Context 'Initial Export Only (#223)' {

        BeforeAll {
            $command = Get-Command New-JIMSyncRuleMapping
        }

        It 'InitialExportOnly should be available only on export parameter sets' {
            $setNames = $command.Parameters['InitialExportOnly'].ParameterSets.Keys
            $setNames | Should -Contain 'ExportAttribute'
            $setNames | Should -Contain 'ExportExpression'
            $setNames | Should -Not -Contain 'ImportAttribute'
            $setNames | Should -Not -Contain 'ImportExpression'
        }

        It 'Sends initialExportOnly when the switch is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -SourceMetaverseAttributeId 8 -InitialExportOnly -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.initialExportOnly -eq $true
                }
            }
        }

        It 'Omits initialExportOnly when the switch is not supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -SourceMetaverseAttributeId 8 -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('initialExportOnly')
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMSyncRuleMapping -Full
        }

        It 'Should document the CaseNormalisation parameter' {
            ($help.Parameters.Parameter | Where-Object { $_.Name -eq 'CaseNormalisation' }) | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
