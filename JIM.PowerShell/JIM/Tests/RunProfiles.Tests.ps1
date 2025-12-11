#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Run Profile cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMRunProfile' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMRunProfile
        }

        It 'Should have a mandatory ConnectedSystemId parameter' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id as an alias for ConnectedSystemId' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Aliases | Should -Contain 'Id'
        }

        It 'Should accept pipeline by property name' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMRunProfile -ConnectedSystemId 1 } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMRunProfile -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Start-JIMRunProfile' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Start-JIMRunProfile
        }

        It 'Should have a mandatory ConnectedSystemId parameter' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory RunProfileId parameter' {
            $param = $command.Parameters['RunProfileId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id as an alias for RunProfileId' {
            $param = $command.Parameters['RunProfileId']
            $param.Aliases | Should -Contain 'Id'
        }

        It 'Should have a Wait switch parameter' {
            $command.Parameters['Wait'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a Timeout parameter with validation' {
            $param = $command.Parameters['Timeout']
            $param | Should -Not -BeNullOrEmpty
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should write error when not connected' {
            { Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Start-JIMRunProfile -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have a description mentioning async execution' {
            $help.Description.Text | Should -Match 'async|queue'
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}
