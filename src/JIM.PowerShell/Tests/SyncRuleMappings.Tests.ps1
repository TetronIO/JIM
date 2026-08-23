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

    Context 'Null is a value (#91)' {

        BeforeAll {
            $command = Get-Command New-JIMSyncRuleMapping
        }

        It 'NullIsValue should be available only on import parameter sets' {
            $setNames = $command.Parameters['NullIsValue'].ParameterSets.Keys
            $setNames | Should -Contain 'ImportAttribute'
            $setNames | Should -Contain 'ImportExpression'
            $setNames | Should -Not -Contain 'ExportAttribute'
            $setNames | Should -Not -Contain 'ExportExpression'
        }

        It 'Sends nullIsValue when the switch is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -NullIsValue -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.nullIsValue -eq $true
                }
            }
        }

        It 'Omits nullIsValue when the switch is not supplied, leaving the server default' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('nullIsValue')
                }
            }
        }
    }

    Context 'Missing Input Behaviour (#1361)' {

        BeforeAll {
            $command = Get-Command New-JIMSyncRuleMapping
        }

        It 'MissingInputBehaviour should be available only on expression parameter sets' {
            # It governs what happens when an attribute the Expression reads has no value, so it means nothing
            # on a direct attribute mapping.
            $setNames = $command.Parameters['MissingInputBehaviour'].ParameterSets.Keys
            $setNames | Should -Contain 'ImportExpression'
            $setNames | Should -Contain 'ExportExpression'
            $setNames | Should -Not -Contain 'ImportAttribute'
            $setNames | Should -Not -Contain 'ExportAttribute'
        }

        It 'MissingInputBehaviour should validate against the four behaviours' {
            $param = $command.Parameters['MissingInputBehaviour']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Be @('EvaluateAnyway', 'ContributeNoValue', 'FailMapping', 'FailObject')
        }

        It 'Sends missingInputBehaviour on the source for an export Expression mapping' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -Expression 'mv["Display Name"]' `
                    -MissingInputBehaviour FailObject -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.sources[0].missingInputBehaviour -eq 'FailObject'
                }
            }
        }

        It 'Sends missingInputBehaviour on the source for an import Expression mapping' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -Expression 'cs["sn"]' `
                    -MissingInputBehaviour FailMapping -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.sources[0].missingInputBehaviour -eq 'FailMapping'
                }
            }
        }

        It 'Omits missingInputBehaviour when not supplied, leaving the server default' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -Expression 'mv["Display Name"]' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.sources[0].ContainsKey('missingInputBehaviour')
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

        It 'Should document the NullIsValue parameter' {
            ($help.Parameters.Parameter | Where-Object { $_.Name -eq 'NullIsValue' }) | Should -Not -BeNullOrEmpty
        }

        It 'Should document the MissingInputBehaviour parameter' {
            ($help.Parameters.Parameter | Where-Object { $_.Name -eq 'MissingInputBehaviour' }) | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Set-JIMSyncRuleMapping' {

    Context 'Parameters' {

        BeforeAll {
            $command = Get-Command Set-JIMSyncRuleMapping
        }

        It 'Should have a <Name> parameter' -ForEach @(
            @{ Name = 'Expression' }
            @{ Name = 'MissingInputBehaviour' }
            @{ Name = 'NullIsValue' }
            @{ Name = 'InboundValueProcessing' }
            @{ Name = 'CaseNormalisation' }
            @{ Name = 'InitialExportOnly' }
            @{ Name = 'Enabled' }
            @{ Name = 'PassThru' }
        ) {
            $command.Parameters[$Name] | Should -Not -BeNullOrEmpty
        }

        It 'MissingInputBehaviour should validate against the four behaviours' {
            $validateSet = $command.Parameters['MissingInputBehaviour'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('EvaluateAnyway', 'ContributeNoValue', 'FailMapping', 'FailObject')
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Request body composition' {

        It 'PATCHes only the settings that were supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 15 } }

                Set-JIMSyncRuleMapping -SyncRuleId 2 -MappingId 15 -MissingInputBehaviour FailObject -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'PATCH' -and
                    $Endpoint -eq '/api/v1/synchronisation/sync-rules/2/mappings/15' -and
                    $Body.missingInputBehaviour -eq 'FailObject' -and
                    -not $Body.ContainsKey('expression') -and
                    -not $Body.ContainsKey('nullIsValue') -and
                    -not $Body.ContainsKey('initialExportOnly')
                }
            }
        }

        It 'Sends a false switch value rather than omitting it' {
            # -NullIsValue:$false is how an administrator turns the setting off; omitting it from the body
            # would make that call a silent no-op.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 8 } }

                Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 8 -NullIsValue $false -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.ContainsKey('nullIsValue') -and $Body.nullIsValue -eq $false
                }
            }
        }

        It 'Disables a mapping by sending enabled=false' {
            # Enabled applies to both directions (#1485); a disabled mapping is skipped by synchronisation
            # until it is re-enabled.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 8 } }

                Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 8 -Enabled $false -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'PATCH' -and
                    $Endpoint -eq '/api/v1/synchronisation/sync-rules/1/mappings/8' -and
                    $Body.ContainsKey('enabled') -and $Body.enabled -eq $false
                }
            }
        }

        It 'Re-enables a mapping by sending enabled=true' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 8 } }

                Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 8 -Enabled $true -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.ContainsKey('enabled') -and $Body.enabled -eq $true
                }
            }
        }

        It 'Refuses a call that names no setting rather than PATCHing nothing' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 8 } }

                Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 8 -Confirm:$false -ErrorAction SilentlyContinue -ErrorVariable err

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
                $err | Should -Not -BeNullOrEmpty
            }
        }

        It 'Returns nothing unless -PassThru is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 15 } }

                $quiet = Set-JIMSyncRuleMapping -SyncRuleId 2 -MappingId 15 -InitialExportOnly $true -Confirm:$false
                $passed = Set-JIMSyncRuleMapping -SyncRuleId 2 -MappingId 15 -InitialExportOnly $true -PassThru -Confirm:$false

                $quiet | Should -BeNullOrEmpty
                $passed.id | Should -Be 15
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMSyncRuleMapping -Full
        }

        It 'Should document the MissingInputBehaviour parameter' {
            ($help.Parameters.Parameter | Where-Object { $_.Name -eq 'MissingInputBehaviour' }) | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
