# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Contract tests: cmdlets must send enum-typed request values as their string
    names, never numeric ordinals.

.DESCRIPTION
    The REST API's global JsonStringEnumConverter is configured with
    allowIntegerValues:false (see ApiJsonConfiguration / PR #1060), so any request
    DTO property carrying a numeric enum ordinal is rejected with a 400. These
    tests pin every cmdlet that puts an enum on the wire to the string-name
    contract, so a regression is caught here rather than in a slow integration run.
    Get-JIMScheduleExecution sends its enum as a query-string parameter (not the
    JSON body); it is exercised the same way for consistency with that contract.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Request enum serialisation (string names, not numeric ordinals)' {

    Context 'New-JIMSyncRule' {

        It 'Sends direction as the string enum name (Import)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMSyncRule -Name 'Test' -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.direction -is [string] -and $Body.direction -eq 'Import'
                }
            }
        }

        It 'Sends direction as the string enum name (Export)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMSyncRule -Name 'Test' -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Export -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.direction -is [string] -and $Body.direction -eq 'Export'
                }
            }
        }
    }

    Context 'New-JIMRunProfile' {

        It 'Sends runType as the string enum name' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMRunProfile -ConnectedSystemId 1 -Name 'Test' -RunType FullSynchronisation -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.runType -is [string] -and $Body.runType -eq 'FullSynchronisation'
                }
            }
        }
    }

    Context 'New-JIMSchedule' {

        It 'Sends triggerType, patternType and intervalUnit as string enum names' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMSchedule -Name 'Test' -TriggerType Cron -PatternType Interval -IntervalValue 2 -IntervalUnit Hours -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.triggerType -is [string] -and $Body.triggerType -eq 'Cron' -and
                    $Body.patternType -is [string] -and $Body.patternType -eq 'Interval' -and
                    $Body.intervalUnit -is [string] -and $Body.intervalUnit -eq 'Hours'
                }
            }
        }
    }

    Context 'Set-JIMSchedule' {

        It 'Sends an overridden triggerType as the string enum name in the PUT body' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                # The GET returns the existing schedule (the API serialises enums as
                # strings), then the cmdlet issues the PUT we assert on.
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{ id = 1; name = 'Existing'; triggerType = 'Cron'; patternType = 'SpecificTimes'; isEnabled = $true; steps = @() }
                }

                Set-JIMSchedule -Id ([guid]::NewGuid()) -TriggerType Manual -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'PUT' -and $Body.triggerType -is [string] -and $Body.triggerType -eq 'Manual'
                }
            }
        }
    }

    Context 'Switch-JIMMatchingMode' {

        It 'Sends mode as the string enum name' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ mode = 'SyncRule'; warnings = @() } }

                Switch-JIMMatchingMode -ConnectedSystemId 1 -Mode SyncRule -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.mode -is [string] -and $Body.mode -eq 'SyncRule'
                }
            }
        }
    }

    Context 'Get-JIMScheduleExecution' {

        It 'Sends status as the string enum name in the query string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @() } }

                Get-JIMScheduleExecution -Status Completed | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*status=Completed*'
                }
            }
        }
    }

    Context 'New-JIMMetaverseObjectType' {

        It 'Sends deletionRule as the string enum name' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMMetaverseObjectType -Name 'Test' -PluralName 'Tests' -DeletionRule WhenLastConnectorDisconnected -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.deletionRule -is [string] -and $Body.deletionRule -eq 'WhenLastConnectorDisconnected'
                }
            }
        }
    }

    Context 'Set-JIMMetaverseObjectType' {

        It 'Sends deletionRule as the string enum name' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenAuthoritativeSourceDisconnected -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -ParameterFilter {
                    $Body -and $Body.ContainsKey('deletionRule') -and $Body.deletionRule -is [string] -and $Body.deletionRule -eq 'WhenAuthoritativeSourceDisconnected'
                }
            }
        }
    }

    Context 'New-JIMMetaverseAttribute' {

        It 'Sends attributePlurality as the string enum name' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMMetaverseAttribute -Name 'Test' -Type Text -AttributePlurality MultiValued -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.type -is [string] -and $Body.type -eq 'Text' -and
                    $Body.attributePlurality -is [string] -and $Body.attributePlurality -eq 'MultiValued'
                }
            }
        }

        It "Normalises the 'Integer' alias to the enum member name 'Number'" {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMMetaverseAttribute -Name 'Test' -Type Integer -AttributePlurality SingleValued -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.type -is [string] -and $Body.type -eq 'Number'
                }
            }
        }

        It "Sends 'Decimal' as the exact enum member name (no alias normalisation)" {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMMetaverseAttribute -Name 'Test' -Type Decimal -AttributePlurality SingleValued -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.type -is [string] -and $Body.type -eq 'Decimal'
                }
            }
        }
    }

    Context 'Set-JIMMetaverseAttribute' {

        It 'Sends type (alias-normalised) and attributePlurality as string enum names to the schema endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                # The schema change first GETs the current attribute (enums come back as names).
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Existing'; type = 'Text'; attributePlurality = 'SingleValued' } }

                Set-JIMMetaverseAttribute -Id 1 -Type Integer -AttributePlurality MultiValued -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -ParameterFilter {
                    $Method -eq 'PATCH' -and $Endpoint -like '*/schema' -and
                    $Body.type -is [string] -and $Body.type -eq 'Number' -and
                    $Body.attributePlurality -is [string] -and $Body.attributePlurality -eq 'MultiValued'
                }
            }
        }

        It 'Sends renderingHint as the string enum name' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Existing'; type = 'Text'; attributePlurality = 'SingleValued' } }

                Set-JIMMetaverseAttribute -Id 1 -RenderingHint ChipSet -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -ParameterFilter {
                    $Method -eq 'PATCH' -and $Endpoint -notlike '*/schema' -and
                    $Body.renderingHint -is [string] -and $Body.renderingHint -eq 'ChipSet'
                }
            }
        }
    }
}
