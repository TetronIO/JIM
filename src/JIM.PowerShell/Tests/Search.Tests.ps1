# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Predefined Search cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMPredefinedSearch' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMPredefinedSearch
        }

        It 'Should have an Id parameter' {
            $command.Parameters['Id'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Uri parameter' {
            $command.Parameters['Uri'] | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline by property name for Id' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept pipeline by property name for Uri' {
            $param = $command.Parameters['Uri']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should support wildcards on Uri' {
            $param = $command.Parameters['Uri']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.SupportsWildcardsAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have three parameter sets: List, ById, ByUri' {
            $sets = $command.ParameterSets.Name
            $sets | Should -Contain 'List'
            $sets | Should -Contain 'ById'
            $sets | Should -Contain 'ByUri'
        }

        It 'Should default to the List parameter set' {
            $command.DefaultParameterSet | Should -Be 'List'
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMPredefinedSearch -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMPredefinedSearch -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Set-JIMPredefinedSearch' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMPredefinedSearch
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should type Id as int' {
            $command.Parameters['Id'].ParameterType.FullName | Should -Be 'System.Int32'
        }

        It 'Should accept pipeline by property name for Id' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have an IsEnabled parameter typed as bool' {
            $command.Parameters['IsEnabled'].ParameterType.FullName | Should -Be 'System.Boolean'
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess (ConfirmImpact Medium)' {
            $cmdletBinding = $command.ScriptBlock.Attributes | Where-Object { $_ -is [System.Management.Automation.CmdletBindingAttribute] }
            $cmdletBinding.SupportsShouldProcess | Should -BeTrue
            $cmdletBinding.ConfirmImpact | Should -Be 'Medium'
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMPredefinedSearch -Id 1 -IsEnabled $true -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMPredefinedSearch -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}
