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

Describe 'Get-JIMConnectedSystemObjectSyncPreview' {
    It 'Reads the preview from the Connected System Object sync-preview endpoint' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $id = [guid]::NewGuid()
            $script:capturedEndpoint = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                [PSCustomObject]@{
                    OutcomeTree       = @()
                    Inbound           = [PSCustomObject]@{ WouldProject = $false }
                    ProposedExports   = @()
                    Errors            = @()
                    Warnings          = @()
                    HasBlockingErrors = $false
                    AffectedSyncRules = @()
                }
            }

            Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId 1 -Id $id | Out-Null

            $script:capturedEndpoint | Should -Be "/api/v1/synchronisation/connected-systems/1/connector-space/$id/sync-preview"
        }
    }

    It 'Flattens the outcome tree into a depth-ordered Outcomes list' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    OutcomeTree = @(
                        [PSCustomObject]@{
                            OutcomeType             = 'DisconnectedOutOfScope'
                            TargetEntityId          = $null
                            TargetEntityDescription = 'Active Directory'
                            SyncRuleId              = $null
                            SyncRuleName            = $null
                            DetailCount             = 2
                            DetailMessage           = $null
                            Ordinal                 = 0
                            Children                = @(
                                [PSCustomObject]@{
                                    OutcomeType             = 'MvoDeleted'
                                    TargetEntityId          = [guid]::NewGuid()
                                    TargetEntityDescription = $null
                                    SyncRuleId              = $null
                                    SyncRuleName            = $null
                                    DetailCount             = $null
                                    DetailMessage           = 'WhenLastConnectorDisconnected'
                                    Ordinal                 = 0
                                    Children                = @()
                                }
                            )
                        }
                    )
                    Inbound           = $null
                    ProposedExports   = @()
                    Errors            = @()
                    Warnings          = @()
                    HasBlockingErrors = $false
                    AffectedSyncRules = @()
                }
            }

            $result = Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId 1 -Id ([guid]::NewGuid())

            $result.Outcomes.Count | Should -Be 2
            $result.Outcomes[0].Depth | Should -Be 0
            $result.Outcomes[0].Target | Should -Be 'Active Directory'
            $result.Outcomes[1].Depth | Should -Be 1
            $result.Outcomes[1].OutcomeType | Should -Be 'MvoDeleted'
            $result.Outcomes[1].DetailMessage | Should -Be 'WhenLastConnectorDisconnected'
        }
    }

    It 'Surfaces HasBlockingErrors so a caller can tell a failing preview from a succeeding one' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    OutcomeTree       = @()
                    Inbound           = $null
                    ProposedExports   = @()
                    Errors            = @([PSCustomObject]@{ Code = 'ExpressionEvaluationError'; Detail = 'boom' })
                    Warnings          = @()
                    HasBlockingErrors = $true
                    AffectedSyncRules = @()
                }
            }

            $result = Get-JIMConnectedSystemObjectSyncPreview -ConnectedSystemId 1 -Id ([guid]::NewGuid())

            $result.HasBlockingErrors | Should -BeTrue
            $result.Errors[0].Code | Should -Be 'ExpressionEvaluationError'
        }
    }

    It 'Accepts ConnectedSystemId and Id from the pipeline by property name' {
        $command = Get-Command Get-JIMConnectedSystemObjectSyncPreview
        foreach ($name in @('ConnectedSystemId', 'Id')) {
            ($command.Parameters[$name].Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName
            }) | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMMetaverseObjectSyncPreview' {
    It 'Reads the preview from the Metaverse Object sync-preview endpoint' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $id = [guid]::NewGuid()
            $script:capturedEndpoint = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                [PSCustomObject]@{
                    OutcomeTree       = @()
                    Inbound           = $null
                    ProposedExports   = @()
                    Errors            = @()
                    Warnings          = @()
                    HasBlockingErrors = $false
                    AffectedSyncRules = @()
                }
            }

            Get-JIMMetaverseObjectSyncPreview -Id $id | Out-Null

            $script:capturedEndpoint | Should -Be "/api/v1/metaverse/objects/$id/sync-preview"
        }
    }

    It 'Always returns a null Inbound, because a Metaverse Object has no inbound chain' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    OutcomeTree       = @()
                    Inbound           = $null
                    ProposedExports   = @()
                    Errors            = @()
                    Warnings          = @()
                    HasBlockingErrors = $false
                    AffectedSyncRules = @()
                }
            }

            $result = Get-JIMMetaverseObjectSyncPreview -Id ([guid]::NewGuid())

            $result.Inbound | Should -BeNullOrEmpty
        }
    }

    It 'Returns the proposed exports the outbound chain would stage' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    OutcomeTree       = @()
                    Inbound           = $null
                    ProposedExports   = @(
                        [PSCustomObject]@{ ConnectedSystemId = 3; ChangeType = 'Update'; AttributeChanges = @() }
                    )
                    Errors            = @()
                    Warnings          = @()
                    HasBlockingErrors = $false
                    AffectedSyncRules = @()
                }
            }

            $result = Get-JIMMetaverseObjectSyncPreview -Id ([guid]::NewGuid())

            $result.ProposedExports.Count | Should -Be 1
            $result.ProposedExports[0].ChangeType | Should -Be 'Update'
        }
    }

    It 'Accepts Id from the pipeline by property name' {
        $parameter = (Get-Command Get-JIMMetaverseObjectSyncPreview).Parameters['Id']
        ($parameter.Attributes | Where-Object {
            $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName
        }) | Should -Not -BeNullOrEmpty
    }
}

Describe 'ConvertTo-JIMSyncPreviewOutcomeRows' {
    It 'Returns an empty array for an empty tree' {
        InModuleScope JIM {
            @(ConvertTo-JIMSyncPreviewOutcomeRows -Nodes @()).Count | Should -Be 0
        }
    }

    It 'Falls back to the id when no description or name was captured' {
        InModuleScope JIM {
            $mvoId = [guid]::NewGuid()
            $node = [PSCustomObject]@{
                OutcomeType             = 'Projected'
                TargetEntityId          = $mvoId
                TargetEntityDescription = $null
                SyncRuleId              = 7
                SyncRuleName            = $null
                DetailCount             = $null
                DetailMessage           = $null
                Ordinal                 = 0
                Children                = @()
            }

            $rows = @(ConvertTo-JIMSyncPreviewOutcomeRows -Nodes @($node))

            $rows[0].Target | Should -Be $mvoId
            $rows[0].SyncRule | Should -Be 7
        }
    }
}
