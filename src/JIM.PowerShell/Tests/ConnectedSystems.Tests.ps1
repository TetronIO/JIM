# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Connected System cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'New-JIMConnectedSystem' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command New-JIMConnectedSystem
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory ConnectorDefinitionId parameter' {
            $param = $command.Parameters['ConnectorDefinitionId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Description parameter' {
            $command.Parameters['Description'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { New-JIMConnectedSystem -Name "Test" -ConnectorDefinitionId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMConnectedSystem -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Set-JIMConnectedSystem' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMConnectedSystem
        }

        It 'Should have a mandatory Id parameter in ById set' {
            $param = $command.Parameters['Id']
            $paramAttr = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and
                $_.Mandatory -and
                $_.ParameterSetName -eq 'ById'
            }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Name parameter' {
            $command.Parameters['Name'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Description parameter' {
            $command.Parameters['Description'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a SettingValues parameter' {
            $command.Parameters['SettingValues'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a MaxExportParallelism parameter with ValidateRange' {
            $param = $command.Parameters['MaxExportParallelism']
            $param | Should -Not -BeNullOrEmpty
            $validateRange = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
            $validateRange | Should -Not -BeNullOrEmpty
        }

        It 'Should have an InitialPasswordTimeToLive parameter typed as a TimeSpan' {
            $param = $command.Parameters['InitialPasswordTimeToLive']
            $param | Should -Not -BeNullOrEmpty
            $param.ParameterType | Should -Be ([timespan])
        }

        It 'Should have an UnresolvedReferenceHandling parameter with ValidateSet' {
            $param = $command.Parameters['UnresolvedReferenceHandling']
            $param | Should -Not -BeNullOrEmpty
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Error'
            $validateSet.ValidValues | Should -Contain 'Warn'
            $validateSet.ValidValues | Should -Contain 'Ignore'
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have InputObject parameter that accepts pipeline input' {
            $param = $command.Parameters['InputObject']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Request body composition' {

        It 'Sends unresolvedReferenceHandling in the PUT body when -UnresolvedReferenceHandling is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                Set-JIMConnectedSystem -Id 1 -UnresolvedReferenceHandling 'Ignore' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.unresolvedReferenceHandling -eq 'Ignore'
                }
            }
        }

        It 'Omits unresolvedReferenceHandling from the PUT body when -UnresolvedReferenceHandling is not specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                Set-JIMConnectedSystem -Id 1 -Name 'Updated Name' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('unresolvedReferenceHandling')
                }
            }
        }

        It 'Rejects a value outside the ValidateSet' {
            { Set-JIMConnectedSystem -Id 1 -UnresolvedReferenceHandling 'Bogus' -Confirm:$false -ErrorAction Stop } | Should -Throw
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMConnectedSystem -Id 1 -Name "Test" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMConnectedSystem -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Set-JIMConnectedSystemAttribute' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMConnectedSystemAttribute
        }

        It 'Should have mandatory ConnectedSystemId parameter for both parameter sets' {
            $param = $command.Parameters['ConnectedSystemId']
            $mandatoryAttrs = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory
            }
            $mandatoryAttrs | Should -Not -BeNullOrEmpty
        }

        It 'Should have mandatory ObjectTypeId parameter for both parameter sets' {
            $param = $command.Parameters['ObjectTypeId']
            $mandatoryAttrs = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory
            }
            $mandatoryAttrs | Should -Not -BeNullOrEmpty
        }

        It 'Should have AttributeId parameter for Single parameter set' {
            $param = $command.Parameters['AttributeId']
            $paramAttr = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and
                $_.ParameterSetName -eq 'Single'
            }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It 'Should have AttributeUpdates parameter for Bulk parameter set' {
            $param = $command.Parameters['AttributeUpdates']
            $paramAttr = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and
                $_.ParameterSetName -eq 'Bulk'
            }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It 'Should have Selected parameter' {
            $command.Parameters['Selected'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have IsExternalId parameter' {
            $command.Parameters['IsExternalId'] | Should -Not -BeNullOrEmpty
        }

        # An Oracle NUMBER column arrives as a Decimal because Oracle has one numeric type, and an
        # Attribute Flow needs its source and target types to match. Overriding the type is what lets it
        # reach a built-in numeric Metaverse Attribute without an expression (#1354).
        It 'Should have a Type parameter on the Single parameter set only' {
            $param = $command.Parameters['Type']
            $param | Should -Not -BeNullOrEmpty

            $parameterAttributes = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute]
            }
            $parameterAttributes.ParameterSetName | Should -Be @('Single')
        }

        It 'Should offer the same data types as New-JIMMetaverseAttribute' {
            $param = $command.Parameters['Type']
            $validateSet = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ValidateSetAttribute]
            }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Be @(
                'Text', 'Integer', 'LongNumber', 'Decimal', 'DateTime', 'Boolean', 'Reference', 'Guid', 'Binary')
        }

        It 'Should reject a data type that is not a JIM attribute type' {
            { Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 1 -AttributeId 1 -Type 'NotSet' -ErrorAction Stop } |
                Should -Throw
        }

        It 'Should have IsSecondaryExternalId parameter' {
            $command.Parameters['IsSecondaryExternalId'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have Single as the default parameter set' {
            $command.DefaultParameterSet | Should -Be 'Single'
        }

        It 'Should have AttributeId with Id alias' {
            $param = $command.Parameters['AttributeId']
            $param.Aliases | Should -Contain 'Id'
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected (Single mode)' {
            { Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 1 -AttributeId 1 -Selected $true -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected (Bulk mode)' {
            $updates = @{ 1 = @{ selected = $true } }
            { Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 1 -AttributeUpdates $updates -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMConnectedSystemAttribute -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }

        It 'Should document the Bulk parameter set in description' {
            $help.Description.Text | Should -Match 'Bulk'
        }
    }
}

Describe 'Set-JIMConnectedSystemContainer' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMConnectedSystemContainer
        }

        It 'Should have a Scope parameter' {
            $command.Parameters['Scope'] | Should -Not -BeNullOrEmpty
        }

        It 'Should restrict Scope to the supported values' {
            $param = $command.Parameters['Scope']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('Subtree', 'OneLevel')
        }

        It 'Should have an Excluded parameter' {
            $command.Parameters['Excluded'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Request body composition' {

        It 'Sends scope in the PUT body when -Scope is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 10; name = 'OU=Users' } }

                Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Scope 'OneLevel' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.scope -eq 'OneLevel'
                }
            }
        }

        It 'Omits scope from the PUT body when -Scope is not specified' {
            # A caller toggling selection must not silently widen a OneLevel container back to Subtree.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 10; name = 'OU=Users' } }

                Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Selected $true -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('scope')
                }
            }
        }

        It 'Allows scope to be set without changing selection' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 10; name = 'OU=Users' } }

                Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Scope 'Subtree' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.scope -eq 'Subtree' -and -not $Body.ContainsKey('selected')
                }
            }
        }

        It 'Rejects a scope value outside the ValidateSet' {
            { Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Scope 'Bogus' -Confirm:$false -ErrorAction Stop } | Should -Throw
        }

        It 'Sends excluded in the PUT body when -Excluded is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 10; name = 'OU=Service Accounts' } }

                Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Excluded $true -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.excluded -eq $true -and -not $Body.ContainsKey('selected')
                }
            }
        }

        It 'Omits excluded from the PUT body when -Excluded is not specified' {
            # A caller changing scope must not silently hand an excluded branch back into scope.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 10; name = 'OU=Service Accounts' } }

                Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Scope 'OneLevel' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('excluded')
                }
            }
        }

        It 'Sends both halves when a selection is replaced with an exclusion' {
            # The API rejects a request that would leave a Container both selected and excluded, so stating both is
            # how a caller moves one from a selection to an exclusion.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 10; name = 'OU=Service Accounts' } }

                Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Selected $false -Excluded $true -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.selected -eq $false -and $Body.excluded -eq $true
                }
            }
        }
    }
}

Describe 'Import-JIMConnectedSystemSchema' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Import-JIMConnectedSystemSchema
        }

        It 'Should have a Preview switch parameter' {
            $command.Parameters['Preview'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a DisableDependents switch parameter' {
            $command.Parameters['DisableDependents'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a RemoveDependents switch parameter' {
            $command.Parameters['RemoveDependents'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Preview behaviour' {

        It 'Calls the preview endpoint and returns the result when -Preview is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        success                        = $true
                        hasChanges                     = $true
                        hasRemovalsOrDefinitionChanges = $true
                        removedObjectTypes             = @('computer')
                    }
                }

                $result = Import-JIMConnectedSystemSchema -Id 5 -Preview

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/5/import-schema/preview' -and $Method -eq 'POST'
                }
                $result.HasRemovalsOrDefinitionChanges | Should -BeTrue
                $result.RemovedObjectTypes | Should -Contain 'computer'
            }
        }

        It 'Previews without persisting even though PassThru is not specified' {
            # The result IS the point of a preview; gating it behind -PassThru would return nothing at all.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ success = $true; hasChanges = $false } }

                $result = Import-JIMConnectedSystemSchema -Id 5 -Preview

                $result | Should -Not -BeNullOrEmpty
            }
        }

        It 'Sends disableDependents in the body when -DisableDependents is specified' {
            # The Apply and Disable Dependents flavour (#1485): the schema is applied and everything the
            # removals invalidate is disabled with a recorded reason.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ objectTypes = @() } }

                Import-JIMConnectedSystemSchema -Id 5 -DisableDependents -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/5/import-schema' -and
                    $Body.disableDependents -eq $true
                }
            }
        }

        It 'Sends removeDependents in the body when -RemoveDependents is specified' {
            # The Apply and Remove flavour (#1485): the schema is applied, the invalidated configuration is
            # deleted and the data removal is queued as a worker task.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ objectTypes = @() } }

                Import-JIMConnectedSystemSchema -Id 5 -RemoveDependents -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/5/import-schema' -and
                    $Body.removeDependents -eq $true
                }
            }
        }

        It 'Refuses -DisableDependents together with -RemoveDependents without calling the API' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ objectTypes = @() } }

                { Import-JIMConnectedSystemSchema -Id 5 -DisableDependents -RemoveDependents -Confirm:$false -ErrorAction Stop } |
                    Should -Throw '*mutually exclusive*'

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }

        It 'Calls the import endpoint, not the preview endpoint, when -Preview is absent' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ objectTypes = @() } }

                Import-JIMConnectedSystemSchema -Id 5 -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/5/import-schema'
                }
            }
        }
    }
}
