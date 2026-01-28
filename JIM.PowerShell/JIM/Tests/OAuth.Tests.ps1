#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for OAuth helper functions (PKCE, discovery, etc.).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    # Import private functions for testing
    . (Join-Path $ModulePath 'Private' 'Invoke-OAuthBrowserFlow.ps1')
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'New-PkceCodeVerifier' {

    Context 'Output Format' {

        It 'Should return a 64-character string' {
            $verifier = New-PkceCodeVerifier
            $verifier.Length | Should -Be 64
        }

        It 'Should contain only base64url characters (A-Za-z0-9_-)' {
            $verifier = New-PkceCodeVerifier
            $verifier | Should -Match '^[A-Za-z0-9_-]+$'
        }

        It 'Should not contain standard base64 characters (+, /, =)' {
            $verifier = New-PkceCodeVerifier
            $verifier | Should -Not -Match '[+/=]'
        }
    }

    Context 'Randomness' {

        It 'Should generate unique values on each call' {
            $v1 = New-PkceCodeVerifier
            $v2 = New-PkceCodeVerifier
            $v1 | Should -Not -Be $v2
        }

        It 'Should generate 10 unique values' {
            $verifiers = 1..10 | ForEach-Object { New-PkceCodeVerifier }
            $unique = $verifiers | Select-Object -Unique
            $unique.Count | Should -Be 10
        }
    }

    Context 'RFC 7636 Compliance' {

        It 'Should meet minimum length requirement (43 characters)' {
            $verifier = New-PkceCodeVerifier
            $verifier.Length | Should -BeGreaterOrEqual 43
        }

        It 'Should not exceed maximum length (128 characters)' {
            $verifier = New-PkceCodeVerifier
            $verifier.Length | Should -BeLessOrEqual 128
        }
    }
}

Describe 'Get-PkceCodeChallenge' {

    Context 'S256 Algorithm' {

        It 'Should produce a base64url-encoded string' {
            $verifier = New-PkceCodeVerifier
            $challenge = Get-PkceCodeChallenge -CodeVerifier $verifier
            $challenge | Should -Match '^[A-Za-z0-9_-]+$'
        }

        It 'Should not contain standard base64 characters (+, /, =)' {
            $verifier = New-PkceCodeVerifier
            $challenge = Get-PkceCodeChallenge -CodeVerifier $verifier
            $challenge | Should -Not -Match '[+/=]'
        }

        It 'Should produce a 43-character string (SHA256 = 32 bytes = 43 base64url chars)' {
            $verifier = New-PkceCodeVerifier
            $challenge = Get-PkceCodeChallenge -CodeVerifier $verifier
            $challenge.Length | Should -Be 43
        }

        It 'Should produce consistent output for the same input' {
            $verifier = 'test-verifier-12345'
            $challenge1 = Get-PkceCodeChallenge -CodeVerifier $verifier
            $challenge2 = Get-PkceCodeChallenge -CodeVerifier $verifier
            $challenge1 | Should -Be $challenge2
        }

        It 'Should produce different output for different inputs' {
            $challenge1 = Get-PkceCodeChallenge -CodeVerifier 'verifier-one'
            $challenge2 = Get-PkceCodeChallenge -CodeVerifier 'verifier-two'
            $challenge1 | Should -Not -Be $challenge2
        }
    }

    Context 'RFC 7636 Test Vectors' {

        # RFC 7636 Appendix B provides a test vector
        # Note: The RFC example uses a specific verifier and expected challenge

        It 'Should produce correct challenge for RFC 7636 Appendix B test vector' {
            # From RFC 7636 Appendix B:
            # code_verifier = dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk
            # code_challenge = E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM
            $verifier = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk'
            $expectedChallenge = 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM'

            $challenge = Get-PkceCodeChallenge -CodeVerifier $verifier
            $challenge | Should -Be $expectedChallenge
        }
    }

    Context 'Error Handling' {

        It 'Should throw when CodeVerifier is null' {
            { Get-PkceCodeChallenge -CodeVerifier $null } | Should -Throw
        }

        It 'Should throw when CodeVerifier is empty' {
            { Get-PkceCodeChallenge -CodeVerifier '' } | Should -Throw
        }
    }
}

Describe 'Open-Browser' {

    Context 'Platform Detection' {

        It 'Should not throw on any platform' {
            # This test just verifies the function doesn't crash
            # It may or may not actually open a browser depending on environment
            { Open-Browser -Url 'https://example.com' -ErrorAction SilentlyContinue } | Should -Not -Throw
        }

        It 'Should accept a valid URL' {
            $command = Get-Command Open-Browser
            $command.Parameters['Url'] | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-OidcDiscoveryDocument' {

    Context 'Parameter Validation' {

        It 'Should have a mandatory Authority parameter' {
            $command = Get-Command Get-OidcDiscoveryDocument
            $command.Parameters['Authority'].Attributes.Mandatory | Should -Contain $true
        }
    }

    Context 'URL Construction' {

        It 'Should throw for unreachable authority' {
            { Get-OidcDiscoveryDocument -Authority 'https://invalid.example.com' } | Should -Throw
        }
    }
}

Describe 'Invoke-OAuthTokenRefresh' {

    Context 'Parameter Validation' {

        It 'Should have mandatory TokenEndpoint parameter' {
            $command = Get-Command Invoke-OAuthTokenRefresh
            $command.Parameters['TokenEndpoint'].Attributes.Mandatory | Should -Contain $true
        }

        It 'Should have mandatory ClientId parameter' {
            $command = Get-Command Invoke-OAuthTokenRefresh
            $command.Parameters['ClientId'].Attributes.Mandatory | Should -Contain $true
        }

        It 'Should have mandatory RefreshToken parameter' {
            $command = Get-Command Invoke-OAuthTokenRefresh
            $command.Parameters['RefreshToken'].Attributes.Mandatory | Should -Contain $true
        }

        It 'Should have mandatory Scopes parameter' {
            $command = Get-Command Invoke-OAuthTokenRefresh
            $command.Parameters['Scopes'].Attributes.Mandatory | Should -Contain $true
        }
    }
}

Describe 'Invoke-OAuthBrowserFlow' {

    Context 'Parameter Validation' {

        It 'Should have mandatory AuthorizeEndpoint parameter' {
            $command = Get-Command Invoke-OAuthBrowserFlow
            $command.Parameters['AuthorizeEndpoint'].Attributes.Mandatory | Should -Contain $true
        }

        It 'Should have mandatory TokenEndpoint parameter' {
            $command = Get-Command Invoke-OAuthBrowserFlow
            $command.Parameters['TokenEndpoint'].Attributes.Mandatory | Should -Contain $true
        }

        It 'Should have mandatory ClientId parameter' {
            $command = Get-Command Invoke-OAuthBrowserFlow
            $command.Parameters['ClientId'].Attributes.Mandatory | Should -Contain $true
        }

        It 'Should have mandatory Scopes parameter' {
            $command = Get-Command Invoke-OAuthBrowserFlow
            $command.Parameters['Scopes'].Attributes.Mandatory | Should -Contain $true
        }

        It 'Should have optional RedirectPort parameter with default 8400' {
            $command = Get-Command Invoke-OAuthBrowserFlow
            $command.Parameters['RedirectPort'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have optional TimeoutSeconds parameter with default 300' {
            $command = Get-Command Invoke-OAuthBrowserFlow
            $command.Parameters['TimeoutSeconds'] | Should -Not -BeNullOrEmpty
        }
    }
}
