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

        It 'Should have an ApiKey parameter' {
            $command = Get-Command Connect-JIM
            $command.Parameters['ApiKey'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command = Get-Command Connect-JIM
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a TimeoutSeconds parameter' {
            $command = Get-Command Connect-JIM
            $command.Parameters['TimeoutSeconds'] | Should -Not -BeNullOrEmpty
        }

        It 'Should reject invalid URL format' {
            { Connect-JIM -Url 'not-a-url' -ApiKey 'test-key' } | Should -Throw '*Invalid URL*'
        }

        It 'Should reject empty URL' {
            { Connect-JIM -Url '' -ApiKey 'test-key' } | Should -Throw
        }

        It 'Should reject empty ApiKey when ApiKey parameter set is used' {
            { Connect-JIM -Url 'http://localhost' -ApiKey '' } | Should -Throw
        }
    }

    Context 'Parameter Sets' {

        It 'Should have an Interactive parameter set' {
            $command = Get-Command Connect-JIM
            $command.ParameterSets.Name | Should -Contain 'Interactive'
        }

        It 'Should have an ApiKey parameter set' {
            $command = Get-Command Connect-JIM
            $command.ParameterSets.Name | Should -Contain 'ApiKey'
        }

        It 'Should default to Interactive parameter set' {
            $command = Get-Command Connect-JIM
            $command.DefaultParameterSet | Should -Be 'Interactive'
        }

        It 'ApiKey should be mandatory in ApiKey parameter set' {
            $command = Get-Command Connect-JIM
            $apiKeyParam = $command.Parameters['ApiKey']
            $apiKeyParamSet = $apiKeyParam.ParameterSets['ApiKey']
            $apiKeyParamSet.IsMandatory | Should -BeTrue
        }

        It 'Force should only be in Interactive parameter set' {
            $command = Get-Command Connect-JIM
            $forceParam = $command.Parameters['Force']
            $forceParam.ParameterSets.Keys | Should -Contain 'Interactive'
            $forceParam.ParameterSets.Keys | Should -Not -Contain 'ApiKey'
        }

        It 'TimeoutSeconds should only be in Interactive parameter set' {
            $command = Get-Command Connect-JIM
            $timeoutParam = $command.Parameters['TimeoutSeconds']
            $timeoutParam.ParameterSets.Keys | Should -Contain 'Interactive'
            $timeoutParam.ParameterSets.Keys | Should -Not -Contain 'ApiKey'
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

        It 'Should document the Force parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Force' } | Should -Not -BeNullOrEmpty
        }

        It 'Should document the TimeoutSeconds parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'TimeoutSeconds' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }

        It 'Should mention interactive authentication in description' {
            $help.Description.Text | Should -Match 'interactive|browser|SSO'
        }

        It 'Should mention API key authentication in description' {
            $help.Description.Text | Should -Match 'API key'
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

        It 'Should mention OAuth in description' {
            $help.Description.Text | Should -Match 'OAuth|token'
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

        It 'Should include AuthMethod property' {
            $result = Test-JIMConnection
            $result.PSObject.Properties.Name | Should -Contain 'AuthMethod'
        }

        It 'Should include ServerVersion property' {
            $result = Test-JIMConnection
            $result.PSObject.Properties.Name | Should -Contain 'ServerVersion'
        }

        It 'Should include TokenExpiresAt property' {
            $result = Test-JIMConnection
            $result.PSObject.Properties.Name | Should -Contain 'TokenExpiresAt'
        }

        It 'Should have null AuthMethod when not connected' {
            $result = Test-JIMConnection
            $result.AuthMethod | Should -BeNullOrEmpty
        }

        It 'Should have null ServerVersion when not connected' {
            $result = Test-JIMConnection
            $result.ServerVersion | Should -BeNullOrEmpty
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

        It 'Should document AuthMethod in description' {
            # AuthMethod is documented in the description since PowerShell help
            # doesn't always parse .OUTPUTS structured data the same way
            $help.Description.Text | Should -Match 'AuthMethod|authentication method'
        }
    }
}
