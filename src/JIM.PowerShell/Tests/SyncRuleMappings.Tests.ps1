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

    Context 'Create disabled (#1485)' {

        BeforeAll {
            $command = Get-Command New-JIMSyncRuleMapping
        }

        It 'Should have an Enabled parameter of type bool' {
            $param = $command.Parameters['Enabled']
            $param | Should -Not -BeNullOrEmpty
            $param.ParameterType | Should -Be ([bool])
        }

        It 'Sends enabled=false when -Enabled $false is supplied, creating the mapping disabled' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -Enabled $false -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.ContainsKey('enabled') -and $Body.enabled -eq $false
                }
            }
        }

        It 'Sends enabled=true when -Enabled $true is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -Enabled $true -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.ContainsKey('enabled') -and $Body.enabled -eq $true
                }
            }
        }

        It 'Omits enabled when the parameter is not supplied, leaving the server default of enabled' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('enabled')
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMSyncRuleMapping -Full
        }

        It 'Should document the Enabled parameter' {
            ($help.Parameters.Parameter | Where-Object { $_.Name -eq 'Enabled' }) | Should -Not -BeNullOrEmpty
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

Describe 'Remove-JIMSyncRuleMapping' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Remove-JIMSyncRuleMapping
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a KeepContributedValues switch parameter' {
            $command.Parameters['KeepContributedValues'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Contributed-values recall choice (#1537)' {

        It 'Sends keepContributedValues=true on the DELETE when -KeepContributedValues is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { return }

                Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force -KeepContributedValues

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and
                    $Endpoint -match 'sync-rules/1/mappings/5\?keepContributedValues=true'
                }
            }
        }

        It 'Omits keepContributedValues from the DELETE when the switch is not supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { return }

                Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -notmatch 'keepContributedValues'
                }
            }
        }

        It 'Consults the mapping-scoped contributed-values summary before the confirmation' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'DELETE') { return }
                    [PSCustomObject]@{ Attributes = @(); TotalValues = 0; TotalObjects = 0 }
                }

                Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'sync-rules/1/mappings/5/contributed-values-summary'
                }
            }
        }

        It 'Skips the summary lookup when -Force suppresses the confirmation' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { return }

                Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter {
                    $Endpoint -match 'contributed-values-summary'
                }
            }
        }

        It 'Uses the deferred-recall wording, because a mapping recall happens at the next Full Synchronisation' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'DELETE') { return }
                    [PSCustomObject]@{
                        Attributes   = @([PSCustomObject]@{ AttributeId = 5; AttributeName = 'Department'; ValueCount = 96; ObjectCount = 96 })
                        TotalValues  = 96
                        TotalObjects = 96
                    }
                }
                Mock Get-JIMContributedValuesImpactText { 'IMPACT SENTENCE' }

                Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Confirm:$false

                Should -Invoke Get-JIMContributedValuesImpactText -Times 1 -Exactly -ParameterFilter {
                    $Summary.TotalValues -eq 96 -and $DeferredRecall -eq $true -and -not $KeepContributedValues
                }
            }
        }

        It 'Tells the impact text the values will be kept when -KeepContributedValues is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Method -eq 'DELETE') { return }
                    [PSCustomObject]@{
                        Attributes   = @([PSCustomObject]@{ AttributeId = 5; AttributeName = 'Department'; ValueCount = 96; ObjectCount = 96 })
                        TotalValues  = 96
                        TotalObjects = 96
                    }
                }
                Mock Get-JIMContributedValuesImpactText { 'IMPACT SENTENCE' }

                Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -KeepContributedValues -Confirm:$false

                Should -Invoke Get-JIMContributedValuesImpactText -Times 1 -Exactly -ParameterFilter {
                    $KeepContributedValues -eq $true
                }
            }
        }

        It 'Emits nothing: a mapping deletion always completes without queued work' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { return }

                $result = Remove-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 5 -Force -KeepContributedValues

                $result | Should -BeNullOrEmpty
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMSyncRuleMapping -Full
        }

        It 'Should document the KeepContributedValues parameter' {
            ($help.Parameters.Parameter | Where-Object { $_.Name -eq 'KeepContributedValues' }) | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'New-JIMSyncRuleMapping: Unique Value Generation (#242)' {

    Context 'Parameter sets' {

        BeforeAll {
            $command = Get-Command New-JIMSyncRuleMapping
        }

        It 'Has ImportGenerated and ExportGenerated parameter sets' {
            $command.ParameterSets.Name | Should -Contain 'ImportGenerated'
            $command.ParameterSets.Name | Should -Contain 'ExportGenerated'
        }

        It 'Should have a <Name> parameter' -ForEach @(
            @{ Name = 'Generate' }
            @{ Name = 'TokenKind' }
            @{ Name = 'SuffixStyle' }
            @{ Name = 'SuffixStart' }
            @{ Name = 'SequenceStart' }
            @{ Name = 'SequenceIncrement' }
            @{ Name = 'FixedWidth' }
            @{ Name = 'OnWidthExceeded' }
            @{ Name = 'RandomFormat' }
            @{ Name = 'RandomLength' }
            @{ Name = 'Separator' }
            @{ Name = 'AttemptLimit' }
            @{ Name = 'NeverReuse' }
        ) {
            $command.Parameters[$Name] | Should -Not -BeNullOrEmpty
        }

        It 'Generate is only available on the generated parameter sets' {
            $setNames = $command.Parameters['Generate'].ParameterSets.Keys
            $setNames | Should -Contain 'ImportGenerated'
            $setNames | Should -Contain 'ExportGenerated'
            $setNames | Should -Not -Contain 'ImportAttribute'
            $setNames | Should -Not -Contain 'ImportExpression'
            $setNames | Should -Not -Contain 'ExportAttribute'
            $setNames | Should -Not -Contain 'ExportExpression'
        }

        It 'Generate is mandatory on the generated parameter sets' {
            ($command.Parameters['Generate'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ParameterSetName -eq 'ImportGenerated' }).Mandatory | Should -BeTrue
        }

        It 'Expression is available on the generated parameter sets (the base expression is optional there)' {
            $setNames = $command.Parameters['Expression'].ParameterSets.Keys
            $setNames | Should -Contain 'ImportGenerated'
            $setNames | Should -Contain 'ExportGenerated'
        }

        It 'TokenKind should validate against OnlyIfTaken/Sequence/Random' {
            $validateSet = $command.Parameters['TokenKind'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('OnlyIfTaken', 'Sequence', 'Random')
        }

        It 'Does not add -ExcludeConnectedSystemId: exclusions are deferred from every surface in release 1' {
            $command.Parameters.Keys | Should -Not -Contain 'ExcludeConnectedSystemId'
        }
    }

    Context 'Request body composition: import' {

        It 'Creates a generated import mapping with no base expression' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 12 -Generate -TokenKind Sequence -SequenceStart 100000 -FixedWidth 6 -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.targetMetaverseAttributeId -eq 12 -and
                    $Body.sources.Count -eq 0 -and
                    $Body.generation.tokenKind -eq 'Sequence' -and
                    $Body.generation.sequenceStart -eq 100000 -and
                    $Body.generation.fixedWidth -eq 6
                }
            }
        }

        It 'Creates a generated import mapping with a base expression' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 `
                    -Expression 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"])' -Generate -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.sources.Count -eq 1 -and
                    $Body.sources[0].expression -eq 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"])' -and
                    $Body.generation -is [hashtable]
                }
            }
        }

        It 'Sends only the generation settings that were supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

                New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 12 -Generate -TokenKind Sequence -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.generation.ContainsKey('tokenKind') -and
                    -not $Body.generation.ContainsKey('sequenceStart') -and
                    -not $Body.generation.ContainsKey('fixedWidth') -and
                    -not $Body.generation.ContainsKey('neverReuse')
                }
            }
        }
    }

    Context 'Request body composition: export' {

        It 'Creates a generated export mapping with a Random token and no base expression' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 2 } }

                New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 40 -Generate -TokenKind Random -RandomFormat Hex -RandomLength 12 -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.targetConnectedSystemAttributeId -eq 40 -and
                    $Body.sources.Count -eq 0 -and
                    $Body.generation.tokenKind -eq 'Random' -and
                    $Body.generation.randomFormat -eq 'Hex' -and
                    $Body.generation.randomLength -eq 12
                }
            }
        }
    }

    Context 'Sequence skipped ahead warning' {

        It 'Warns naming the from and to positions when the response reports a skip' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Id         = 1
                        Generation = [PSCustomObject]@{
                            SequenceSkippedAhead = [PSCustomObject]@{ From = 5; To = 100000 }
                        }
                    }
                }

                $warnings = New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 12 -Generate -TokenKind Sequence -SequenceStart 100000 -Confirm:$false 3>&1 |
                    Where-Object { $_ -is [System.Management.Automation.WarningRecord] }

                $warnings.Count | Should -Be 1
                $warnings[0].Message | Should -Match '5'
                $warnings[0].Message | Should -Match '100000'
            }
        }

        It 'Warns nothing when the response reports no skip' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 1; Generation = [PSCustomObject]@{ SequenceSkippedAhead = $null } } }

                $warnings = New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 12 -Generate -TokenKind Sequence -Confirm:$false 3>&1 |
                    Where-Object { $_ -is [System.Management.Automation.WarningRecord] }

                $warnings.Count | Should -Be 0
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMSyncRuleMapping -Full
        }

        It 'Should document the <Name> parameter' -ForEach @(
            @{ Name = 'Generate' }
            @{ Name = 'TokenKind' }
            @{ Name = 'SequenceStart' }
            @{ Name = 'RandomFormat' }
            @{ Name = 'NeverReuse' }
        ) {
            ($help.Parameters.Parameter | Where-Object { $_.Name -eq $Name }) | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Set-JIMSyncRuleMapping: Unique Value Generation (#242)' {

    Context 'Parameters' {

        BeforeAll {
            $command = Get-Command Set-JIMSyncRuleMapping
        }

        It 'Should have a <Name> parameter' -ForEach @(
            @{ Name = 'TokenKind' }
            @{ Name = 'SuffixStyle' }
            @{ Name = 'SuffixStart' }
            @{ Name = 'SequenceStart' }
            @{ Name = 'SequenceIncrement' }
            @{ Name = 'FixedWidth' }
            @{ Name = 'OnWidthExceeded' }
            @{ Name = 'RandomFormat' }
            @{ Name = 'RandomLength' }
            @{ Name = 'Separator' }
            @{ Name = 'AttemptLimit' }
            @{ Name = 'NeverReuse' }
        ) {
            $command.Parameters[$Name] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Request body composition' {

        It 'PATCHes only the generation settings that were supplied, nested under generation' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 12 } }

                Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -SequenceStart 500000 -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'PATCH' -and
                    $Body.generation.sequenceStart -eq 500000 -and
                    -not $Body.generation.ContainsKey('tokenKind') -and
                    -not $Body.ContainsKey('expression')
                }
            }
        }

        It 'Sends fixedWidth=0 to clear the padding' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 12 } }

                Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -FixedWidth 0 -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.generation.ContainsKey('fixedWidth') -and $Body.generation.fixedWidth -eq 0
                }
            }
        }

        It 'Warns naming the from and to positions when the response reports a skip' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        Id         = 12
                        Generation = [PSCustomObject]@{ SequenceSkippedAhead = [PSCustomObject]@{ From = 7; To = 500000 } }
                    }
                }

                $warnings = Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -SequenceStart 500000 -Confirm:$false 3>&1 |
                    Where-Object { $_ -is [System.Management.Automation.WarningRecord] }

                $warnings.Count | Should -Be 1
                $warnings[0].Message | Should -Match '7'
                $warnings[0].Message | Should -Match '500000'
            }
        }

        It 'Refuses a call naming no setting, generation settings included' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 12 } }

                Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -Confirm:$false -ErrorAction SilentlyContinue -ErrorVariable err

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
                $err | Should -Not -BeNullOrEmpty
            }
        }
    }
}

Describe 'Get-JIMGeneratedValueSequence' {

    Context 'Parameters' {

        BeforeAll {
            $command = Get-Command Get-JIMGeneratedValueSequence
        }

        It 'Should require SyncRuleId and MappingId' {
            $command.Parameters['SyncRuleId'] | Should -Not -BeNullOrEmpty
            $command.Parameters['MappingId'] | Should -Not -BeNullOrEmpty
        }

        It 'Should alias SyncRuleId to Id, matching the mapping cmdlets around it' {
            $command.Parameters['SyncRuleId'].Aliases | Should -Contain 'Id'
        }
    }

    Context 'Request composition' {

        It 'Requests the mapping-scoped sequence endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AttributeName = 'Employee Number'; NextNumber = 42 } }

                Get-JIMGeneratedValueSequence -SyncRuleId 1 -MappingId 12 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/sync-rules/1/mappings/12/sequence'
                }
            }
        }

        It 'Requires a connection' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                Mock Invoke-JIMApi { [PSCustomObject]@{} }

                Get-JIMGeneratedValueSequence -SyncRuleId 1 -MappingId 12 -ErrorAction SilentlyContinue -ErrorVariable err | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
                $err | Should -Not -BeNullOrEmpty
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Get-JIMGeneratedValueSequence -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }
    }
}

Describe 'Restart-JIMGeneratedValues' {

    Context 'Parameters' {

        BeforeAll {
            $command = Get-Command Restart-JIMGeneratedValues
        }

        It 'Should require SyncRuleId and MappingId' {
            $command.Parameters['SyncRuleId'] | Should -Not -BeNullOrEmpty
            $command.Parameters['MappingId'] | Should -Not -BeNullOrEmpty
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should confirm by default (ConfirmImpact High)' {
            $command.ScriptBlock.Attributes |
                Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] -and $_.SupportsShouldProcess -and $_.ConfirmImpact -eq 'High' } |
                Should -Not -BeNullOrEmpty
        }
    }

    Context 'Request composition' {

        It 'Posts to the mapping-scoped restart endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ RetiredValuesForgotten = 0; CounterFrom = 42; CounterTo = 1 } }

                Restart-JIMGeneratedValues -SyncRuleId 1 -MappingId 12 -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'POST' -and
                    $Endpoint -eq '/api/v1/synchronisation/sync-rules/1/mappings/12/generation/restart'
                }
            }
        }

        It 'Does not call the API when -WhatIf is supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ RetiredValuesForgotten = 0; CounterFrom = 42; CounterTo = 1 } }

                Restart-JIMGeneratedValues -SyncRuleId 1 -MappingId 12 -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }

        It 'Returns the restart result (RetiredValuesForgotten always 0 in this release)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ RetiredValuesForgotten = 0; CounterFrom = 42; CounterTo = 1 } }

                $result = Restart-JIMGeneratedValues -SyncRuleId 1 -MappingId 12 -Confirm:$false

                $result.RetiredValuesForgotten | Should -Be 0
                $result.CounterFrom | Should -Be 42
                $result.CounterTo | Should -Be 1
            }
        }

        It 'Requires a connection' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                Mock Invoke-JIMApi { [PSCustomObject]@{} }

                Restart-JIMGeneratedValues -SyncRuleId 1 -MappingId 12 -Confirm:$false -ErrorAction SilentlyContinue -ErrorVariable err | Out-Null

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
                $err | Should -Not -BeNullOrEmpty
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll { $help = Get-Help Restart-JIMGeneratedValues -Full }

        It 'Should have a synopsis' { $help.Synopsis | Should -Not -BeNullOrEmpty }
        It 'Should have examples' { $help.Examples.Example.Count | Should -BeGreaterThan 0 }

        It 'States plainly that no existing value changes and nothing exports' {
            $description = ($help.Description.Text -join ' ')
            $description | Should -Match 'exports? nothing|nothing is exported|no existing generated value'
        }

        It 'States that there are no retired values until a later release' {
            $description = ($help.Description.Text -join ' ')
            $description | Should -Match 'retired values'
            $description | Should -Match 'release 2'
        }
    }
}
