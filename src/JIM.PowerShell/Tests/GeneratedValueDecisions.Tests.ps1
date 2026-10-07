# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the generated value decision cmdlets (Unique Value Generation, #242, release 4).

.DESCRIPTION
    A target rejected a generated value as already in use and JIM held the export for a decision. These cmdlets
    list what is held, allow a rename, and try again. The behaviour pinned here that a reader would not guess
    from the parameters: Approve-JIMGeneratedValueRename renames a live account, so it asks first (ConfirmImpact
    High); a pipeline into Reset-JIMGeneratedValueDecision is collected into ONE request; and a Reset naming no
    criteria refuses to act on every held value unless -AllDecisions says so.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMGeneratedValueDecision' {

    Context 'Parameter sets' {

        BeforeAll {
            $command = Get-Command Get-JIMGeneratedValueDecision
        }

        It 'Should default to listing held values' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should offer the <Name> parameter set' -ForEach @(
            @{ Name = 'ListAll' }
            @{ Name = 'ById' }
            @{ Name = 'Summary' }
        ) {
            $command.ParameterSets.Name | Should -Contain $Name
        }

        It 'Should constrain Status to the statuses a list holds' {
            $validateSet = $command.Parameters['Status'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Should -Be @('NeedsDecision', 'RenameAllowed')
        }
    }

    Context 'Requests' {

        It 'Should read the decisions endpoint with the filters it was given' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ Items = @(); TotalCount = 0 } }

                Get-JIMGeneratedValueDecision -ConnectedSystemId 3 -SyncRuleId 7 -Status NeedsDecision | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '/api/v1/generated-values/decisions?*' -and
                    $Endpoint -like '*connectedSystemId=3*' -and
                    $Endpoint -like '*syncRuleId=7*' -and
                    $Endpoint -like '*status=NeedsDecision*'
                }
            }
        }

        It 'Should return the rows from the response envelope' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{ Items = @([PSCustomObject]@{ Id = [guid]::NewGuid(); Value = 'r.okafor' }); TotalCount = 1 }
                }

                $rows = @(Get-JIMGeneratedValueDecision)

                $rows.Count | Should -Be 1
                $rows[0].Value | Should -Be 'r.okafor'
            }
        }

        It 'Should read one value by id' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 'x' } }
                $id = [guid]'11111111-2222-3333-4444-555555555555'

                Get-JIMGeneratedValueDecision -Id $id | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/generated-values/decisions/11111111-2222-3333-4444-555555555555'
                }
            }
        }

        It 'Should read the summary, narrowed by the filters it was given' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ NeedsDecisionCount = 1 } }

                Get-JIMGeneratedValueDecision -Summary -SyncRuleId 7 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/generated-values/decisions/summary?syncRuleId=7'
                }
            }
        }
    }
}

Describe 'Approve-JIMGeneratedValueRename' {

    Context 'Confirmation' {

        BeforeAll {
            $command = Get-Command Approve-JIMGeneratedValueRename
        }

        It 'Should ask before renaming a live account (ConfirmImpact High)' {
            $binding = $command.ScriptBlock.Attributes | Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] }
            $binding.SupportsShouldProcess | Should -BeTrue
            $binding.ConfirmImpact | Should -Be 'High'
        }

        It 'Should not call the API under -WhatIf' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { }

                Approve-JIMGeneratedValueRename -Id ([guid]::NewGuid()) -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }

    Context 'Requests' {

        It 'Should post allow-rename for the id given' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ Status = 'RenameAllowed' } }
                $id = [guid]'11111111-2222-3333-4444-555555555555'

                $result = Approve-JIMGeneratedValueRename -Id $id -Force

                $result.Status | Should -Be 'RenameAllowed'
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'POST' -and $Endpoint -eq '/api/v1/generated-values/11111111-2222-3333-4444-555555555555/allow-rename'
                }
            }
        }

        It 'Should take held values from Get-JIMGeneratedValueDecision through the pipeline, one request each' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ Status = 'RenameAllowed' } }
                $rows = @(
                    [PSCustomObject]@{ Id = [guid]::NewGuid(); AttributeName = 'Account Name'; Value = 'r.okafor'; MetaverseObjectDisplayName = 'Rita Okafor' }
                    [PSCustomObject]@{ Id = [guid]::NewGuid(); AttributeName = 'Account Name'; Value = 's.adeyemi'; MetaverseObjectDisplayName = 'Sam Adeyemi' }
                )

                $rows | Approve-JIMGeneratedValueRename -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 2 -Exactly -ParameterFilter { $Endpoint -like '*/allow-rename' }
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/generated-values/$($rows[0].Id)/allow-rename" }
            }
        }
    }
}

Describe 'Reset-JIMGeneratedValueDecision' {

    Context 'Confirmation' {

        It 'Should support -WhatIf and -Confirm' {
            $binding = (Get-Command Reset-JIMGeneratedValueDecision).ScriptBlock.Attributes |
                Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] }
            $binding.SupportsShouldProcess | Should -BeTrue
        }

        It 'Should not call the API under -WhatIf' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { }

                Reset-JIMGeneratedValueDecision -SyncRuleId 7 -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }

    Context 'Requests' {

        It 'Should collect a pipeline of held values into one request naming their ids' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 2 } }
                $first = [guid]::NewGuid()
                $second = [guid]::NewGuid()

                $result = @([PSCustomObject]@{ Id = $first }, [PSCustomObject]@{ Id = $second }) | Reset-JIMGeneratedValueDecision -Force

                $result.AffectedCount | Should -Be 2
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'POST' -and $Endpoint -eq '/api/v1/generated-values/decisions/try-again' -and
                    $Body.ids.Count -eq 2 -and $Body.ids -contains $first.ToString() -and $Body.ids -contains $second.ToString()
                }
            }
        }

        It 'Should send the filters it was given' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 3 } }

                Reset-JIMGeneratedValueDecision -ConnectedSystemId 3 -SyncRuleId 7 -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.connectedSystemId -eq 3 -and $Body.syncRuleId -eq 7 -and -not $Body.ContainsKey('applyToAllDecisions')
                }
            }
        }

        It 'Should refuse a request naming nothing, without calling the API' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { }

                Reset-JIMGeneratedValueDecision -Force -ErrorAction SilentlyContinue -ErrorVariable failure

                $failure | Should -Not -BeNullOrEmpty
                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }

        It 'Should release every held value only with -AllDecisions' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedCount = 5 } }

                Reset-JIMGeneratedValueDecision -AllDecisions -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Body.applyToAllDecisions -eq $true }
            }
        }

        It 'Should treat an empty pipeline as nothing to do' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { }

                @() | Reset-JIMGeneratedValueDecision -Force

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }
}

Describe 'Collision Remediation on the mapping cmdlets' {

    It 'New-JIMSyncRuleMapping sends -CollisionRemediation as generation.collisionRemediation' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

            New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 12 -Generate -TokenKind Random -CollisionRemediation $false -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                ($Body | ConvertTo-Json -Depth 10 -Compress) -match '"collisionRemediation":false'
            }
        }
    }

    It 'New-JIMSyncRuleMapping sends no collisionRemediation when it is omitted' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }

            New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 12 -Generate -TokenKind Random -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                ($Body | ConvertTo-Json -Depth 10 -Compress) -notmatch 'collisionRemediation'
            }
        }
    }

    It 'Set-JIMSyncRuleMapping sends -CollisionRemediation as generation.collisionRemediation' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 12 } }

            Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -CollisionRemediation $true -Confirm:$false

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PATCH' -and ($Body | ConvertTo-Json -Depth 10 -Compress) -match '"generation":\{"collisionRemediation":true\}'
            }
        }
    }

    It 'New-JIMSyncRuleMapping offers -CollisionRemediation on the generated parameter sets only' {
        $setNames = (Get-Command New-JIMSyncRuleMapping).Parameters['CollisionRemediation'].ParameterSets.Keys
        @($setNames | Sort-Object) | Should -Be @('ExportGenerated', 'ImportGenerated')
    }
}
