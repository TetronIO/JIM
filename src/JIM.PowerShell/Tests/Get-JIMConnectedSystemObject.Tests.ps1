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

        It 'Should have a ById parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'ById'
        }

        It 'Should have an AttributeValues parameter set' {
            $command.ParameterSets.Name | Should -Contain 'AttributeValues'
        }

        It 'Should have an AttributeValuesAll parameter set' {
            $command.ParameterSets.Name | Should -Contain 'AttributeValuesAll'
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
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id ([guid]::NewGuid()) } | Should -Throw '*Connect-JIM*'
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
}
