# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConfigurationChangeHistory' {
    BeforeAll {
        $command = Get-Command Get-JIMConfigurationChangeHistory
    }

    Context 'Parameter sets' {
        It 'Defaults to the Page parameter set' {
            $command.DefaultParameterSet | Should -Be 'Page'
        }

        It 'Exposes the Page, All, Version and Compare parameter sets' {
            $names = $command.ParameterSets.Name
            $names | Should -Contain 'Page'
            $names | Should -Contain 'All'
            $names | Should -Contain 'Version'
            $names | Should -Contain 'Compare'
        }
    }

    Context 'Parameter validation' {
        It 'Requires Type and restricts it to the covered configuration object kinds' {
            $type = $command.Parameters['Type']
            ($type.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }) | Should -Not -BeNullOrEmpty
            $validateSet = $type.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Contain 'SynchronisationRule'
            $validateSet.ValidValues | Should -Contain 'ConnectedSystem'
            $validateSet.ValidValues | Should -Contain 'Schedule'
            $validateSet.ValidValues | Should -Contain 'ServiceSetting'
            $validateSet.ValidValues | Should -Contain 'MetaverseObjectType'
            $validateSet.ValidValues | Should -Contain 'MetaverseAttribute'
            $validateSet.ValidValues | Should -Contain 'TrustedCertificate'
            $validateSet.ValidValues | Should -Contain 'ApiKey'
            $validateSet.ValidValues | Should -Contain 'Role'
            $validateSet.ValidValues | Should -Contain 'PredefinedSearch'
            $validateSet.ValidValues | Should -Contain 'ConnectorDefinition'
            $validateSet.ValidValues | Should -Contain 'ExampleDataTemplate'
            $validateSet.ValidValues | Should -Contain 'ExampleDataSet'
        }

        It 'Accepts Id from the pipeline by property name' {
            $id = $command.Parameters['Id']
            ($id.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName }) | Should -Not -BeNullOrEmpty
        }

        It 'Types Id as string so it can carry an integer or a GUID' {
            $command.Parameters['Id'].ParameterType | Should -Be ([string])
        }

        It 'Rejects a non-GUID Id for -Type <_>' -ForEach @('Schedule', 'TrustedCertificate', 'ApiKey') {
            { Get-JIMConfigurationChangeHistory -Type $_ -Id 5 -ErrorAction Stop } | Should -Throw '*GUID*'
        }

        It 'Rejects a non-integer Id for -Type SynchronisationRule' {
            { Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Id ([Guid]::NewGuid().ToString()) -ErrorAction Stop } | Should -Throw '*integer*'
        }

        It 'Rejects a non-integer Id for -Type <_>' -ForEach @('MetaverseObjectType', 'MetaverseAttribute', 'Role', 'PredefinedSearch', 'ConnectorDefinition', 'ExampleDataTemplate', 'ExampleDataSet') {
            { Get-JIMConfigurationChangeHistory -Type $_ -Id ([Guid]::NewGuid().ToString()) -ErrorAction Stop } | Should -Throw '*integer*'
        }

        It 'Accepts a dot-notation string key for -Type ServiceSetting (no id-shape rejection)' {
            # Service Settings are string-keyed, so any non-empty key passes shape validation; disconnected, the
            # next failure must be the connect-first error, not an id-shape error.
            Disconnect-JIM
            { Get-JIMConfigurationChangeHistory -Type ServiceSetting -Id 'History.RetentionPeriod' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Exposes Version, CompareFrom, CompareTo, AsDiff and Raw' {
            foreach ($p in 'Version', 'CompareFrom', 'CompareTo', 'AsDiff', 'Raw') {
                $command.Parameters.ContainsKey($p) | Should -BeTrue
            }
        }
    }

    Context 'Requires connection' {
        BeforeEach {
            Disconnect-JIM
        }

        It 'Throws a connect-first error when not connected' {
            { Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Id 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Endpoint mapping' {
        It 'Maps -Type ConnectorDefinition -Id 3 to the connector-definitions change-history route' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMConfigurationChangeHistory -Type ConnectorDefinition -Id 3 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '/api/v1/synchronisation/connector-definitions/3/change-history*'
                }
            }
        }

        It 'Maps -Type ExampleDataSet -Id 5 to the example-data-sets change-history route' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMConfigurationChangeHistory -Type ExampleDataSet -Id 5 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '/api/v1/example-data/example-data-sets/5/change-history*'
                }
            }
        }

        It 'Maps -Type ExampleDataTemplate -Id 1 to the templates change-history route' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMConfigurationChangeHistory -Type ExampleDataTemplate -Id 1 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '/api/v1/example-data/templates/1/change-history*'
                }
            }
        }
    }

    Context 'Help documentation' {
        BeforeAll {
            $help = Get-Help Get-JIMConfigurationChangeHistory -Full
        }

        It 'Has a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Has examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'ChangeReason on configuration write cmdlets' {
    It '<_> exposes an optional ChangeReason parameter' -ForEach @(
        'New-JIMSyncRule', 'Set-JIMSyncRule', 'Remove-JIMSyncRule', 'New-JIMConnectedSystem', 'Set-JIMConnectedSystem', 'Remove-JIMConnectedSystem',
        'Set-JIMServiceSetting', 'Reset-JIMServiceSetting',
        'New-JIMMetaverseObjectType', 'Set-JIMMetaverseObjectType',
        'New-JIMMetaverseAttribute', 'Set-JIMMetaverseAttribute', 'Remove-JIMMetaverseAttribute',
        'Add-JIMCertificate', 'Set-JIMCertificate', 'Remove-JIMCertificate',
        'New-JIMApiKey', 'Set-JIMApiKey', 'Remove-JIMApiKey',
        'Add-JIMRoleMember', 'Remove-JIMRoleMember',
        'Set-JIMPredefinedSearch',
        'New-JIMPredefinedSearchCriteriaGroup', 'Set-JIMPredefinedSearchCriteriaGroup', 'Remove-JIMPredefinedSearchCriteriaGroup',
        'New-JIMPredefinedSearchCriterion', 'Set-JIMPredefinedSearchCriterion', 'Remove-JIMPredefinedSearchCriterion',
        'New-JIMExampleDataSet', 'Set-JIMExampleDataSet', 'Remove-JIMExampleDataSet'
    ) {
        $param = (Get-Command $_).Parameters['ChangeReason']
        $param | Should -Not -BeNullOrEmpty
        # Optional in every parameter set.
        ($param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }) | Should -BeNullOrEmpty
    }
}

Describe 'Format-JIMConfigurationDiff (git-style renderer)' {
    It 'Renders modifications and never discloses a secret value' {
        InModuleScope JIM {
            $diff = [pscustomobject]@{
                objectType    = 'SynchronisationRule'
                objectName    = 'HR Inbound'
                oldVersion    = 6
                newVersion    = 7
                addedCount    = 0
                removedCount  = 0
                modifiedCount = 2
                root          = [pscustomobject]@{
                    key        = 'synchronisationRule'; label = 'Synchronisation Rule'; nodeType = 'Object'; changeType = 'Modified'
                    children   = @(
                        [pscustomobject]@{ key = 'enabled'; label = 'Enabled'; nodeType = 'Scalar'; changeType = 'Modified'; isSecret = $false; oldValue = 'true'; newValue = 'false' }
                        [pscustomobject]@{ key = 'name'; label = 'Name'; nodeType = 'Scalar'; changeType = 'Unchanged'; oldValue = 'HR Inbound'; newValue = 'HR Inbound' }
                        [pscustomobject]@{
                            key = 'settingValues'; label = 'Settings'; nodeType = 'Collection'; changeType = 'Modified'
                            children = @(
                                # The API never sends a secret value, but even if it did the renderer must not print it.
                                [pscustomobject]@{ key = 'Bind password'; label = 'Bind password'; nodeType = 'Scalar'; changeType = 'Modified'; isSecret = $true; oldValue = 'SHOULD_NOT_APPEAR_OLD'; newValue = 'SHOULD_NOT_APPEAR_NEW' }
                            )
                        }
                    )
                }
            }

            $text = (Format-JIMConfigurationDiff -Diff $diff) -join "`n"

            $text | Should -Match 'Enabled: true'
            $text | Should -Match 'Enabled: false'
            $text | Should -Match 'secret changed; value hidden'
            $text | Should -Not -Match 'SHOULD_NOT_APPEAR'
            # The unchanged 'name' scalar is omitted (git-style); the object name still appears in the header.
            $text | Should -Not -Match 'Name:'
        }
    }

    It 'Reports no changes when the diff is empty' {
        InModuleScope JIM {
            $diff = [pscustomobject]@{
                objectType = 'ConnectedSystem'; objectName = 'AD'; oldVersion = 1; newVersion = 1
                addedCount = 0; removedCount = 0; modifiedCount = 0
                root = [pscustomobject]@{ key = 'connectedSystem'; label = 'Connected System'; nodeType = 'Object'; changeType = 'Unchanged'; children = @() }
            }

            $text = (Format-JIMConfigurationDiff -Diff $diff) -join "`n"
            $text | Should -Match '\(no changes\)'
        }
    }
}
