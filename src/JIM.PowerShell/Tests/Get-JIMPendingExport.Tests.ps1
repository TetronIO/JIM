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
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when listing without connection' {
            { Get-JIMPendingExport -ConnectedSystemId 1 } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when getting by ID without connection' {
            { Get-JIMPendingExport -Id ([guid]::NewGuid()) } | Should -Throw '*Connect-JIM*'
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
