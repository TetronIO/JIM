# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for File System browsing cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMFileSystemItem' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMFileSystemItem
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a Roots parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Roots'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMFileSystemItem
        }

        It 'Should have Path as an optional string parameter' {
            $command.Parameters['Path'].ParameterType.Name | Should -Be 'String'
        }

        It 'Should have Roots as a switch parameter' {
            $command.Parameters['Roots'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMFileSystemItem -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected with Roots' {
            { Get-JIMFileSystemItem -Roots -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMFileSystemItem -Full
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

Describe 'Test-JIMFileSystemPath' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Test-JIMFileSystemPath
        }

        It 'Should have a mandatory Path parameter' {
            $param = $command.Parameters['Path']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Path parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Path']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Test-JIMFileSystemPath -Path "/data" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Test-JIMFileSystemPath -Full
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
