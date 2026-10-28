# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMPendingExport cmdlet.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMPendingExport' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMPendingExport
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ListAll parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ListAll'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }

        It 'Should have an AttributeChanges parameter set' {
            $command.ParameterSets.Name | Should -Contain 'AttributeChanges'
        }

        It 'Should have an AttributeChangesAll parameter set' {
            $command.ParameterSets.Name | Should -Contain 'AttributeChangesAll'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMPendingExport
        }

        It 'Should have ConnectedSystemId parameter that accepts pipeline by property name' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have ConnectedSystemId as mandatory for List parameter set' {
            $listSet = $command.ParameterSets | Where-Object { $_.Name -eq 'List' }
            $csIdParam = $listSet.Parameters | Where-Object { $_.Name -eq 'ConnectedSystemId' }
            $csIdParam.IsMandatory | Should -BeTrue
        }

        It 'Should have Id as mandatory for ById parameter set' {
            $byIdSet = $command.ParameterSets | Where-Object { $_.Name -eq 'ById' }
            $idParam = $byIdSet.Parameters | Where-Object { $_.Name -eq 'Id' }
            $idParam.IsMandatory | Should -BeTrue
        }

        It 'Should have AttributeName as a string parameter' {
            $command.Parameters['AttributeName'].ParameterType.Name | Should -Be 'String'
        }

        It 'Should have Page parameter with ValidateRange' {
            $param = $command.Parameters['Page']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have PageSize parameter with ValidateRange' {
            $param = $command.Parameters['PageSize']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have All as a switch parameter' {
            $command.Parameters['All'].SwitchParameter | Should -BeTrue
        }

        It 'Should have Search as an optional string parameter' {
            $command.Parameters['Search'].ParameterType.Name | Should -Be 'String'
        }

        It 'Should accept every Pending Export status the API can filter by for -Status' {
            # The API binds the status filter to PendingExportStatus by name, so the set must name every member.
            $enumSource = Get-Content -Raw (Join-Path $PSScriptRoot '..' '..' 'JIM.Models' 'Transactional' 'PendingExportEnums.cs')
            $enumBody = [regex]::Match($enumSource, 'public enum PendingExportStatus\s*\{(?<body>[^}]*)\}').Groups['body'].Value
            $expected = [regex]::Matches($enumBody, '(?m)^\s*(?<name>[A-Za-z]+)\s*=\s*\d+') | ForEach-Object { $_.Groups['name'].Value }
            $expected | Should -Not -BeNullOrEmpty

            $validateSet = $command.Parameters['Status'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet.ValidValues | Sort-Object | Should -Be ($expected | Sort-Object)
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when listing without connection' {
            { Get-JIMPendingExport -ConnectedSystemId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when getting by ID without connection' {
            { Get-JIMPendingExport -Id ([guid]::NewGuid()) -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Failing' {

        It 'Asks the list for failing Pending Exports only' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @() } }

                Get-JIMPendingExport -ConnectedSystemId 2 -Failing | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/2/pending-exports?page=1&pageSize=50&failing=true'
                }
            }
        }

        It 'Counts failing Pending Exports only' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { 3 }

                $count = Get-JIMPendingExport -ConnectedSystemId 2 -Count -Failing

                $count | Should -Be 3
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/2/pending-exports/count?failing=true'
                }
            }
        }

        It 'Leaves the filter off unless -Failing is given' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @() } }

                Get-JIMPendingExport -ConnectedSystemId 2 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -notlike '*failing*' }
            }
        }
    }

    Context 'Change Type and Status on the list' {

        It 'Asks the list for one change type and status' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @() } }

                Get-JIMPendingExport -ConnectedSystemId 2 -ChangeType Delete -Status Pending | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/synchronisation/connected-systems/2/pending-exports?page=1&pageSize=50&changeType=Delete&status=Pending'
                }
            }
        }

        It 'Sends the filters on every page of -All' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0; totalPages = 0 } }

                Get-JIMPendingExport -ConnectedSystemId 2 -ChangeType Update -All | Out-Null

                Should -Invoke Invoke-JIMApi -ParameterFilter { $Endpoint -like '*changeType=Update*' }
                Should -Invoke Invoke-JIMApi -Times 0 -ParameterFilter { $Endpoint -notlike '*changeType=Update*' }
            }
        }

        It 'Leaves both filters off unless asked' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @() } }

                Get-JIMPendingExport -ConnectedSystemId 2 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -notlike '*changeType*' -and $Endpoint -notlike '*status*'
                }
            }
        }
    }

    Context 'Queued by' {

        It 'Has a QueuedBy parameter set' {
            (Get-Command Get-JIMPendingExport).ParameterSets.Name | Should -Contain 'QueuedBy'
        }

        It 'Requests the runs that queued the Pending Export and returns them as rows' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $pendingExportId = [guid]::NewGuid()
                $first = [guid]::NewGuid()
                $second = [guid]::NewGuid()
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        pendingExportId = $pendingExportId
                        runs = @([PSCustomObject]@{ id = $first }, [PSCustomObject]@{ id = $second })
                        notRetainedCount = 0; earlierRunsMayBeOmitted = $false
                    }
                }

                $rows = @(Get-JIMPendingExport -Id $pendingExportId -QueuedBy -WarningVariable warnings)

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/synchronisation/pending-exports/$pendingExportId/queued-by" }
                $rows.id | Should -Be @($first, $second)
                @($warnings).Count | Should -Be 0
            }
        }

        It 'Warns where runs are no longer retained or earlier ones may be missing' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{ runs = @(); notRetainedCount = 2; earlierRunsMayBeOmitted = $true }
                }

                $null = Get-JIMPendingExport -Id ([guid]::NewGuid()) -QueuedBy -WarningVariable warnings -WarningAction SilentlyContinue

                @($warnings).Count | Should -Be 2
                "$($warnings[0])" | Should -BeLike '*2*no longer retained*'
            }
        }

        It 'Takes Pending Exports piped from the list, by their Id' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ runs = @(); notRetainedCount = 0; earlierRunsMayBeOmitted = $false } }
                $pendingExportId = [guid]::NewGuid()
                # A list row carries its Connected System beside its own id.
                $listRow = [PSCustomObject]@{ Id = $pendingExportId; ConnectedSystemId = 2 }

                $listRow | Get-JIMPendingExport -QueuedBy | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/synchronisation/pending-exports/$pendingExportId/queued-by" }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMPendingExport -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have a description' {
            $help.Description | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should document the ConnectedSystemId parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'ConnectedSystemId' } | Should -Not -BeNullOrEmpty
        }

        It 'Should document the Id parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Id' } | Should -Not -BeNullOrEmpty
        }

        It 'Should document the AttributeName parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'AttributeName' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}
