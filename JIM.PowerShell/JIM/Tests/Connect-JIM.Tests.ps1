#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Connect-JIM cmdlet.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Connect-JIM' {

    Context 'Parameter Validation' {

        It 'Should have a mandatory Url parameter' {
            $command = Get-Command Connect-JIM
            $command.Parameters['Url'].Attributes.Mandatory | Should -Contain $true
        }

        It 'Should have a mandatory ApiKey parameter' {
            $command = Get-Command Connect-JIM
            $command.Parameters['ApiKey'].Attributes.Mandatory | Should -Contain $true
        }

        It 'Should reject invalid URL format' {
            { Connect-JIM -Url 'not-a-url' -ApiKey 'test-key' } | Should -Throw '*Invalid URL*'
        }

        It 'Should reject empty URL' {
            { Connect-JIM -Url '' -ApiKey 'test-key' } | Should -Throw
        }

        It 'Should reject empty ApiKey' {
            { Connect-JIM -Url 'http://localhost' -ApiKey '' } | Should -Throw
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Connect-JIM -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have a description' {
            $help.Description | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples | Should -Not -BeNullOrEmpty
        }

        It 'Should document the Url parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Url' } | Should -Not -BeNullOrEmpty
        }

        It 'Should document the ApiKey parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'ApiKey' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Disconnect-JIM' {

    Context 'Functionality' {

        It 'Should not throw when not connected' {
            { Disconnect-JIM } | Should -Not -Throw
        }

        It 'Should have CmdletBinding' {
            $command = Get-Command Disconnect-JIM
            $command.CmdletBinding | Should -BeTrue
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Disconnect-JIM -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have a description' {
            $help.Description | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Test-JIMConnection' {

    Context 'Parameter Validation' {

        It 'Should have a Quiet switch parameter' {
            $command = Get-Command Test-JIMConnection
            $command.Parameters['Quiet'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Functionality when not connected' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should return object with Connected = $false when not connected' {
            $result = Test-JIMConnection
            $result.Connected | Should -BeFalse
        }

        It 'Should return $false with -Quiet when not connected' {
            $result = Test-JIMConnection -Quiet
            $result | Should -BeFalse
        }

        It 'Should include helpful message when not connected' {
            $result = Test-JIMConnection
            $result.Message | Should -Match 'Connect-JIM'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Test-JIMConnection -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples | Should -Not -BeNullOrEmpty
        }
    }
}
