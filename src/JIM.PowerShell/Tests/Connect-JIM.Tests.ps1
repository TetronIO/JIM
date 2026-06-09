# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Connect-JIM cmdlet.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
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

        It 'Should have a NoPersist switch parameter' {
            $command = Get-Command Connect-JIM
            $command.Parameters['NoPersist'].SwitchParameter | Should -BeTrue
        }

        It 'NoPersist should only be in Interactive parameter set' {
            $command = Get-Command Connect-JIM
            $noPersistParam = $command.Parameters['NoPersist']
            $noPersistParam.ParameterSets.Keys | Should -Contain 'Interactive'
            $noPersistParam.ParameterSets.Keys | Should -Not -Contain 'ApiKey'
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

    Context 'Parameters' {

        It 'Should not have a ClearCache parameter (retired)' {
            (Get-Command Disconnect-JIM).Parameters.Keys | Should -Not -Contain 'ClearCache'
        }

        It 'Should have a Url parameter' {
            (Get-Command Disconnect-JIM).Parameters['Url'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have an All switch parameter' {
            (Get-Command Disconnect-JIM).Parameters['All'].SwitchParameter | Should -BeTrue
        }

        It 'All should be in its own parameter set, separate from Url' {
            $command = Get-Command Disconnect-JIM
            $command.Parameters['All'].ParameterSets.Keys | Should -Not -Contain 'Default'
            $command.Parameters['Url'].ParameterSets.Keys | Should -Not -Contain 'All'
        }

        It 'Should reject an invalid Url' {
            { Disconnect-JIM -Url 'not-a-url' } | Should -Throw '*Invalid URL*'
        }
    }

    Context 'Disconnect-and-forget behaviour' {

        It 'Removes the persisted token for the currently connected instance by default' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'OAuth' }
                Mock Remove-JIMToken { 1 }
                Disconnect-JIM
                Should -Invoke Remove-JIMToken -Times 1 -ParameterFilter { $BaseUrl -eq 'https://jim.example.com' }
            }
        }

        It 'Removes the persisted token regardless of auth method (API key session)' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Remove-JIMToken { 0 }
                Disconnect-JIM
                Should -Invoke Remove-JIMToken -Times 1 -ParameterFilter { $BaseUrl -eq 'https://jim.example.com' }
            }
        }

        It 'Removes the persisted token for an explicit -Url even when not connected' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                Mock Remove-JIMToken { 1 }
                Disconnect-JIM -Url 'https://other.example.com'
                Should -Invoke Remove-JIMToken -Times 1 -ParameterFilter { $BaseUrl -eq 'https://other.example.com' }
            }
        }

        It 'Does not touch the credential store when not connected and no -Url' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                Mock Remove-JIMToken { 0 }
                Disconnect-JIM
                Should -Invoke Remove-JIMToken -Times 0
            }
        }

        It 'Removes all persisted tokens with -All' {
            InModuleScope JIM {
                $script:JIMConnection = $null
                Mock Remove-JIMToken { 3 }
                Disconnect-JIM -All
                Should -Invoke Remove-JIMToken -Times 1 -ParameterFilter { $All.IsPresent }
            }
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

Describe 'Invoke-JIMApi when not connected' {

    It 'Does not throw by default (non-terminating error)' {
        InModuleScope JIM {
            $script:JIMConnection = $null
            { Invoke-JIMApi -Endpoint '/api/v1/health' -ErrorAction SilentlyContinue } | Should -Not -Throw
        }
    }

    It 'Writes an error that points the user at Connect-JIM -Url' {
        InModuleScope JIM {
            $script:JIMConnection = $null
            Invoke-JIMApi -Endpoint '/api/v1/health' -ErrorVariable apiError -ErrorAction SilentlyContinue | Out-Null
            ($apiError -join "`n") | Should -Match 'Connect-JIM -Url'
        }
    }

    It 'Throws a catchable error with -ErrorAction Stop' {
        InModuleScope JIM {
            $script:JIMConnection = $null
            { Invoke-JIMApi -Endpoint '/api/v1/health' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    It 'Returns no output (caller emits nothing)' {
        InModuleScope JIM {
            $script:JIMConnection = $null
            $result = Invoke-JIMApi -Endpoint '/api/v1/health' -ErrorAction SilentlyContinue
            $result | Should -BeNullOrEmpty
        }
    }
}

Describe 'Invoke-TokenRefresh write-back (issue #305)' {

    # Most identity providers rotate the refresh token on every use. The persisted
    # copy must therefore be re-saved after each successful refresh, or cross-session
    # persistence works exactly once. These tests pin that write-back contract.

    It 'Persists the rotated refresh token after a successful refresh when the session is persisted' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{
                Url            = 'https://jim.example.com'
                AuthMethod     = 'OAuth'
                AccessToken    = 'old-at'
                RefreshToken   = 'old-rt'
                TokenExpiresAt = $null
                Persisted      = $true
                OAuthConfig    = @{ TokenEndpoint = 'https://idp/token'; ClientId = 'jim'; Scopes = @('openid', 'offline_access') }
            }
            Mock Invoke-OAuthTokenRefresh { [PSCustomObject]@{ AccessToken = 'new-at'; RefreshToken = 'rotated-rt'; ExpiresAt = (Get-Date).AddHours(1) } }
            Mock Save-JIMToken { $true }

            Invoke-TokenRefresh

            Should -Invoke Save-JIMToken -Times 1 -ParameterFilter {
                $BaseUrl -eq 'https://jim.example.com' -and $RefreshToken -eq 'rotated-rt'
            }
            $script:JIMConnection.RefreshToken | Should -Be 'rotated-rt'
            $script:JIMConnection.AccessToken | Should -Be 'new-at'
        }
    }

    It 'Does not write to the credential store when the session is not persisted (-NoPersist)' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{
                Url            = 'https://jim.example.com'
                AuthMethod     = 'OAuth'
                AccessToken    = 'old-at'
                RefreshToken   = 'old-rt'
                TokenExpiresAt = $null
                Persisted      = $false
                OAuthConfig    = @{ TokenEndpoint = 'https://idp/token'; ClientId = 'jim'; Scopes = @('openid') }
            }
            Mock Invoke-OAuthTokenRefresh { [PSCustomObject]@{ AccessToken = 'new-at'; RefreshToken = 'rotated-rt'; ExpiresAt = (Get-Date).AddHours(1) } }
            Mock Save-JIMToken { $true }

            Invoke-TokenRefresh

            Should -Invoke Save-JIMToken -Times 0
            # The in-memory token is still rotated; only the on-disk copy is skipped.
            $script:JIMConnection.RefreshToken | Should -Be 'rotated-rt'
        }
    }

    It 'A failed save does not surface as a terminating error (refresh still succeeds)' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{
                Url            = 'https://jim.example.com'
                AuthMethod     = 'OAuth'
                AccessToken    = 'old-at'
                RefreshToken   = 'old-rt'
                TokenExpiresAt = $null
                Persisted      = $true
                OAuthConfig    = @{ TokenEndpoint = 'https://idp/token'; ClientId = 'jim'; Scopes = @('openid', 'offline_access') }
            }
            Mock Invoke-OAuthTokenRefresh { [PSCustomObject]@{ AccessToken = 'new-at'; RefreshToken = 'rotated-rt'; ExpiresAt = (Get-Date).AddHours(1) } }
            Mock Save-JIMToken { throw 'keyring exploded' }

            { Invoke-TokenRefresh } | Should -Not -Throw
            $script:JIMConnection.AccessToken | Should -Be 'new-at'
        }
    }
}

Describe 'Connect-JIMInteractive cached reconnect (issue #305)' {

    # The headline feature: a persisted refresh token lets a new terminal reconnect
    # silently, with no browser round-trip, and re-persists the rotated token.

    It 'Reconnects silently with a cached token and never opens a browser' {
        InModuleScope JIM {
            $script:JIMConnection = $null
            Mock Invoke-RestMethod { [PSCustomObject]@{ authority = 'https://idp/'; clientId = 'jim'; scopes = @('openid', 'offline_access') } }
            Mock Get-OidcDiscoveryDocument { [PSCustomObject]@{ TokenEndpoint = 'https://idp/token'; AuthorizeEndpoint = 'https://idp/authorize' } }
            Mock Test-JIMTokenPersistenceAvailable { $true }
            Mock Invoke-JIMApi { [PSCustomObject]@{ status = 'Healthy' } }
            Mock Get-JIMServerVersion { '1.2.3' }
            Mock Show-JIMBanner { }
            Mock Test-JIMAuthorisation { [PSCustomObject]@{ authorised = $true } }
            Mock Save-JIMToken { $true }
            Mock Get-JIMPersistedToken { 'cached-rt' }
            Mock Invoke-OAuthTokenRefresh { [PSCustomObject]@{ AccessToken = 'at'; RefreshToken = 'rotated-rt'; ExpiresAt = (Get-Date).AddHours(1) } }
            Mock Invoke-OAuthBrowserFlow { throw 'Browser flow must not run on a cached reconnect' }

            $result = Connect-JIMInteractive -BaseUrl 'https://jim.example.com'

            $result.Status | Should -Be 'Connected (cached)'
            $result.Connected | Should -BeTrue
            Should -Invoke Invoke-OAuthBrowserFlow -Times 0
            Should -Invoke Save-JIMToken -Times 1 -ParameterFilter { $RefreshToken -eq 'rotated-rt' }
        }
    }

    It 'Falls back to the browser and clears the stale token when the cached refresh is rejected' {
        InModuleScope JIM {
            $script:JIMConnection = $null
            Mock Invoke-RestMethod { [PSCustomObject]@{ authority = 'https://idp/'; clientId = 'jim'; scopes = @('openid', 'offline_access') } }
            Mock Get-OidcDiscoveryDocument { [PSCustomObject]@{ TokenEndpoint = 'https://idp/token'; AuthorizeEndpoint = 'https://idp/authorize' } }
            Mock Test-JIMTokenPersistenceAvailable { $true }
            Mock Invoke-JIMApi { [PSCustomObject]@{ status = 'Healthy' } }
            Mock Get-JIMServerVersion { '1.2.3' }
            Mock Show-JIMBanner { }
            Mock Test-JIMAuthorisation { [PSCustomObject]@{ authorised = $true } }
            Mock Save-JIMToken { $true }
            Mock Get-JIMPersistedToken { 'stale-rt' }
            Mock Invoke-OAuthTokenRefresh { throw 'invalid_grant' }
            Mock Remove-JIMToken { 1 }
            Mock Invoke-OAuthBrowserFlow { [PSCustomObject]@{ AccessToken = 'at'; RefreshToken = 'fresh-rt'; ExpiresAt = (Get-Date).AddHours(1) } }

            $result = Connect-JIMInteractive -BaseUrl 'https://jim.example.com'

            Should -Invoke Remove-JIMToken -Times 1 -ParameterFilter { $BaseUrl -eq 'https://jim.example.com' }
            Should -Invoke Invoke-OAuthBrowserFlow -Times 1
            Should -Invoke Save-JIMToken -Times 1 -ParameterFilter { $RefreshToken -eq 'fresh-rt' }
            $result.Connected | Should -BeTrue
        }
    }

    It 'Skips the cached-token path entirely when -Force is set' {
        InModuleScope JIM {
            $script:JIMConnection = $null
            Mock Invoke-RestMethod { [PSCustomObject]@{ authority = 'https://idp/'; clientId = 'jim'; scopes = @('openid', 'offline_access') } }
            Mock Get-OidcDiscoveryDocument { [PSCustomObject]@{ TokenEndpoint = 'https://idp/token'; AuthorizeEndpoint = 'https://idp/authorize' } }
            Mock Test-JIMTokenPersistenceAvailable { $true }
            Mock Invoke-JIMApi { [PSCustomObject]@{ status = 'Healthy' } }
            Mock Get-JIMServerVersion { '1.2.3' }
            Mock Show-JIMBanner { }
            Mock Test-JIMAuthorisation { [PSCustomObject]@{ authorised = $true } }
            Mock Save-JIMToken { $true }
            Mock Get-JIMPersistedToken { 'cached-rt' }
            Mock Invoke-OAuthBrowserFlow { [PSCustomObject]@{ AccessToken = 'at'; RefreshToken = 'fresh-rt'; ExpiresAt = (Get-Date).AddHours(1) } }

            $null = Connect-JIMInteractive -BaseUrl 'https://jim.example.com' -Force

            Should -Invoke Get-JIMPersistedToken -Times 0
            Should -Invoke Invoke-OAuthBrowserFlow -Times 1
        }
    }
}
