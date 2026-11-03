# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMConnectedSystemPartition cmdlet.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemPartition' {

    Context 'Requests the partitions endpoint and emits each partition individually' {

        It 'Calls the connected-systems/{id}/partitions endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }

                Get-JIMConnectedSystemPartition -ConnectedSystemId 7 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/partitions'
                }
            }
        }

        It 'Emits each partition as a separate pipeline object' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ id = 70; name = 'dc=corp,dc=local'; selected = $true; deltaImportWatermark = $null },
                        [PSCustomObject]@{ id = 80; name = 'dc=partners,dc=local'; selected = $false; deltaImportWatermark = $null }
                    )
                }

                $result = @(Get-JIMConnectedSystemPartition -ConnectedSystemId 7)

                $result.Count | Should -Be 2
                $result[1].name | Should -Be 'dc=partners,dc=local'
            }
        }
    }

    Context 'Each partition carries its current Delta Import watermark' {

        # The watermark is the API's object, passed through whole: where the partition's next Delta Import reads changes
        # after, when an import last recorded it, and the Connector's reading of it.
        It 'Returns a recorded watermark with its value, recording time and description' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{
                            id                   = 70
                            name                 = 'dc=corp,dc=local'
                            selected             = $true
                            deltaImportWatermark = [PSCustomObject]@{
                                value       = '{"Kind":"Accesslog","AccesslogTimestamp":"20261103100541.000000Z","AccesslogTimestampIsLogged":true}'
                                recordedAt  = '2026-11-03T10:17:02Z'
                                description = [PSCustomObject]@{
                                    kind   = 'access log'
                                    points = @([PSCustomObject]@{ scope = $null; number = $null; time = '2026-11-03T10:05:41Z'; text = $null })
                                }
                            }
                        }
                    )
                }

                $watermark = (Get-JIMConnectedSystemPartition -ConnectedSystemId 7).deltaImportWatermark

                $watermark.value | Should -BeLike '*"AccesslogTimestamp":"20261103100541.000000Z"*'
                $watermark.recordedAt | Should -Be '2026-11-03T10:17:02Z'
                $watermark.description.kind | Should -Be 'access log'
                @($watermark.description.points).Count | Should -Be 1
                @($watermark.description.points)[0].time | Should -Be '2026-11-03T10:05:41Z'
            }
        }

        It 'Leaves the watermark empty for a partition that holds none, so its next Delta Import reads in full' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @([PSCustomObject]@{ id = 80; name = 'dc=partners,dc=local'; selected = $true; deltaImportWatermark = $null })
                }

                $partition = Get-JIMConnectedSystemPartition -ConnectedSystemId 7

                $partition.PSObject.Properties.Name | Should -Contain 'deltaImportWatermark'
                $partition.deltaImportWatermark | Should -BeNullOrEmpty
            }
        }

        It 'Lets a selected partition with no watermark be found by filtering on it' {
            # The documented check: which selected partitions will read every object at their next Delta Import.
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    @(
                        [PSCustomObject]@{ id = 70; name = 'dc=corp,dc=local'; selected = $true; deltaImportWatermark = [PSCustomObject]@{ value = 'x'; recordedAt = '2026-11-03T10:17:02Z'; description = $null } },
                        [PSCustomObject]@{ id = 80; name = 'dc=partners,dc=local'; selected = $true; deltaImportWatermark = $null },
                        [PSCustomObject]@{ id = 90; name = 'dc=archive,dc=local'; selected = $false; deltaImportWatermark = $null }
                    )
                }

                $unrecorded = @(Get-JIMConnectedSystemPartition -ConnectedSystemId 7 |
                    Where-Object { $_.selected -and -not $_.deltaImportWatermark })

                $unrecorded.name | Should -Be @('dc=partners,dc=local')
            }
        }
    }
}
