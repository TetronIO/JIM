#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Clear-JIMConnectedSystem cmdlet.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Clear-JIMConnectedSystem' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Clear-JIMConnectedSystem
        }

        It 'Should have ById as the default parameter set' {
            $command.DefaultParameterSet | Should -Be 'ById'
        }

        It 'Should have a ByInputObject parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ByInputObject'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Clear-JIMConnectedSystem
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a KeepChangeHistory switch parameter' {
            $command.Parameters['KeepChangeHistory'].SwitchParameter | Should -BeTrue
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $idParam = $command.Parameters['Id']
            $idParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have InputObject parameter that accepts pipeline input' {
            $inputParam = $command.Parameters['InputObject']
            $inputParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter as mandatory' {
            $idParam = $command.Parameters['Id']
            $idParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should write error when not connected' {
            { Clear-JIMConnectedSystem -Id 1 -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Clear-JIMConnectedSystem -Full
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

        It 'Should document the Id parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Id' } | Should -Not -BeNullOrEmpty
        }

        It 'Should document the KeepChangeHistory parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'KeepChangeHistory' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}
