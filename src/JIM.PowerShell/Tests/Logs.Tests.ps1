# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for log viewing cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMLogEntry' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMLogEntry
        }

        It 'Should have an Entries parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'Entries'
        }

        It 'Should have a Levels parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Levels'
        }

        It 'Should have a Services parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Services'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMLogEntry
        }

        It 'Should have Service as an optional string parameter' {
            $command.Parameters['Service'].ParameterType.Name | Should -Be 'String'
        }

        It 'Should have Level as a string array parameter' {
            $command.Parameters['Level'].ParameterType.Name | Should -Be 'String[]'
        }

        It 'Should have Limit parameter with ValidateRange' {
            $param = $command.Parameters['Limit']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Offset parameter with ValidateRange' {
            $param = $command.Parameters['Offset']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have ListLevels as a switch parameter' {
            $command.Parameters['ListLevels'].SwitchParameter | Should -BeTrue
        }

        It 'Should have ListServices as a switch parameter' {
            $command.Parameters['ListServices'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMLogEntry -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected with ListLevels' {
            { Get-JIMLogEntry -ListLevels -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected with ListServices' {
            { Get-JIMLogEntry -ListServices -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMLogEntry -Full
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

Describe 'Get-JIMLogFile' {

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMLogFile -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMLogFile -Full
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
