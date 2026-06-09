# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for System cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMHealth' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMHealth
        }

        It 'Should have a Health parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'Health'
        }

        It 'Should have a Ready parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Ready'
        }

        It 'Should have a Live parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Live'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMHealth
        }

        It 'Should have an optional Url parameter' {
            $command.Parameters['Url'] | Should -Not -BeNullOrEmpty
        }

        It 'Url should not be mandatory' {
            $urlParam = $command.Parameters['Url']
            $mandatoryAttrs = $urlParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            $mandatoryAttrs | Should -BeNullOrEmpty
        }

        It 'Should have a Ready switch parameter' {
            $command.Parameters['Ready'].SwitchParameter | Should -BeTrue
        }

        It 'Ready should be mandatory in Ready parameter set' {
            $readyParam = $command.Parameters['Ready']
            $readyParamSet = $readyParam.ParameterSets['Ready']
            $readyParamSet.IsMandatory | Should -BeTrue
        }

        It 'Should have a Live switch parameter' {
            $command.Parameters['Live'].SwitchParameter | Should -BeTrue
        }

        It 'Live should be mandatory in Live parameter set' {
            $liveParam = $command.Parameters['Live']
            $liveParamSet = $liveParam.ParameterSets['Live']
            $liveParamSet.IsMandatory | Should -BeTrue
        }
    }

    Context 'Does Not Require Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should not throw connection error when Url is provided' {
            # Should fail with a network error, not a connection error
            $err = $null
            Get-JIMHealth -Url "http://localhost:1" -ErrorAction SilentlyContinue -ErrorVariable err
            $err | Should -Not -BeNullOrEmpty
            $err[0].Exception.Message | Should -Not -BeLike '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMHealth -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should document the Url parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Url' } | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMVersion' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMVersion
        }

        It 'Should have an optional Url parameter' {
            $command.Parameters['Url'] | Should -Not -BeNullOrEmpty
        }

        It 'Url should not be mandatory' {
            $urlParam = $command.Parameters['Url']
            $mandatoryAttrs = $urlParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            $mandatoryAttrs | Should -BeNullOrEmpty
        }

        It 'Should not have mandatory parameters' {
            $mandatoryParams = $command.Parameters.Values | Where-Object {
                $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            }
            $mandatoryParams | Should -BeNullOrEmpty
        }
    }

    Context 'Does Not Require Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should not throw connection error when Url is provided' {
            $err = $null
            Get-JIMVersion -Url "http://localhost:1" -ErrorAction SilentlyContinue -ErrorVariable err
            $err | Should -Not -BeNullOrEmpty
            $err[0].Exception.Message | Should -Not -BeLike '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMVersion -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should document the Url parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Url' } | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMAuthConfig' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMAuthConfig
        }

        It 'Should have an optional Url parameter' {
            $command.Parameters['Url'] | Should -Not -BeNullOrEmpty
        }

        It 'Url should not be mandatory' {
            $urlParam = $command.Parameters['Url']
            $mandatoryAttrs = $urlParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            $mandatoryAttrs | Should -BeNullOrEmpty
        }

        It 'Should not have mandatory parameters' {
            $mandatoryParams = $command.Parameters.Values | Where-Object {
                $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            }
            $mandatoryParams | Should -BeNullOrEmpty
        }
    }

    Context 'Does Not Require Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should not throw connection error when Url is provided' {
            $err = $null
            Get-JIMAuthConfig -Url "http://localhost:1" -ErrorAction SilentlyContinue -ErrorVariable err
            $err | Should -Not -BeNullOrEmpty
            $err[0].Exception.Message | Should -Not -BeLike '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMAuthConfig -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should document the Url parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Url' } | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMUserInfo' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMUserInfo
        }

        It 'Should not have mandatory parameters' {
            $mandatoryParams = $command.Parameters.Values | Where-Object {
                $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            }
            $mandatoryParams | Should -BeNullOrEmpty
        }

        It 'Should not have a Url parameter' {
            $command.Parameters.Keys | Should -Not -Contain 'Url'
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMUserInfo -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMUserInfo -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
