# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMConnectedSystemObject cmdlet.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemObject' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystemObject
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }

        It 'Should have an AttributeValues parameter set' {
            $command.ParameterSets.Name | Should -Contain 'AttributeValues'
        }

        It 'Should have an AttributeValuesAll parameter set' {
            $command.ParameterSets.Name | Should -Contain 'AttributeValuesAll'
        }

        It 'Should have a Count parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Count'
        }

        It 'Should have a ListAll parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ListAll'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystemObject
        }

        It 'Should have ConnectedSystemId parameter that accepts pipeline by property name' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have ConnectedSystemId as mandatory' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id as mandatory' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
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

        It 'Should have Count as a switch parameter' {
            $command.Parameters['Count'].SwitchParameter | Should -BeTrue
        }

        It 'Should have ObjectTypeId as an optional int parameter' {
            $command.Parameters['ObjectTypeId'].ParameterType.Name | Should -Be 'Int32'
        }

        It 'Should have PartitionId as an optional int parameter' {
            $command.Parameters['PartitionId'].ParameterType.Name | Should -Be 'Int32'
        }

        It 'Should have Status parameter with a ValidateSet' {
            $param = $command.Parameters['Status']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'Obsolete'
        }

        It 'Should have JoinType parameter with a ValidateSet' {
            $param = $command.Parameters['JoinType']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'NotJoined'
        }

        It 'Should have SortBy as an optional string parameter' {
            $command.Parameters['SortBy'].ParameterType.Name | Should -Be 'String'
        }

        It 'Should have Ascending as a switch parameter' {
            $command.Parameters['Ascending'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id ([guid]::NewGuid()) -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected with Count' {
            { Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Count -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected with only ConnectedSystemId (List)' {
            { Get-JIMConnectedSystemObject -ConnectedSystemId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected with All (ListAll)' {
            { Get-JIMConnectedSystemObject -ConnectedSystemId 1 -All -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMConnectedSystemObject -Full
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

    Context 'Pagination safety (-All bounding)' {

        It 'Should expose a Force switch in the ListAll parameter set' {
            $param = (Get-Command Get-JIMConnectedSystemObject).Parameters['Force']
            $param | Should -Not -BeNullOrEmpty
            $param.SwitchParameter | Should -BeTrue
            $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ParameterSetName -eq 'ListAll' }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It '-All stops at the page cap and warns when the cap is reached without -Force' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $original = $script:JIMMaxAllPages
                try {
                    $script:JIMMaxAllPages = 3
                    # Always report another page so, without a cap, this would page forever.
                    Mock Invoke-JIMApi { [PSCustomObject]@{ items = @([PSCustomObject]@{ id = [guid]::NewGuid() }); hasNextPage = $true; totalPages = 999999; totalCount = 100 } }

                    Get-JIMConnectedSystemObject -ConnectedSystemId 1 -All -WarningVariable warnings -WarningAction SilentlyContinue | Out-Null

                    Should -Invoke Invoke-JIMApi -Times 3 -Exactly
                    ($warnings -join ' ') | Should -Match 'stopped after 3 pages'
                    ($warnings -join ' ') | Should -Match '-Force'
                }
                finally {
                    $script:JIMMaxAllPages = $original
                }
            }
        }

        It '-Force overrides the page cap and fetches every page' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $original = $script:JIMMaxAllPages
                try {
                    $script:JIMMaxAllPages = 2
                    $script:csoPollCount = 0
                    # Five pages of data; the cap is 2, so only -Force should reach page 5.
                    Mock Invoke-JIMApi {
                        $script:csoPollCount++
                        [PSCustomObject]@{ items = @([PSCustomObject]@{ id = [guid]::NewGuid() }); hasNextPage = ($script:csoPollCount -lt 5); totalPages = 5; totalCount = 100 }
                    }

                    Get-JIMConnectedSystemObject -ConnectedSystemId 1 -All -Force -WarningVariable warnings -WarningAction SilentlyContinue | Out-Null

                    Should -Invoke Invoke-JIMApi -Times 5 -Exactly
                    ($warnings -join ' ') | Should -Not -Match 'stopped after'
                }
                finally {
                    $script:JIMMaxAllPages = $original
                }
            }
        }

        It '-All warns up front when the result set is large' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                # Single page, but a large totalCount should trigger the up-front warning.
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 500; totalCount = 50000 } }

                Get-JIMConnectedSystemObject -ConnectedSystemId 1 -All -WarningVariable warnings -WarningAction SilentlyContinue | Out-Null

                ($warnings -join ' ') | Should -Match 'large result set'
            }
        }
    }
}
