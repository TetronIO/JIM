# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the persistent refresh-token store (issue #305).

.DESCRIPTION
    Covers the pure cache-key derivation and the provider dispatch / argument
    construction for Save-JIMToken, Get-JIMPersistedToken, and Remove-JIMToken.
    Platform store calls are exercised via a mocked Invoke-JIMStoreProcess so the
    tests are deterministic on any OS without needing a real keyring.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    # Dot-source the private token store so its functions are testable directly and
    # internal calls can be mocked in this scope (same pattern as OAuth.Tests.ps1).
    $ModuleRoot = Split-Path $ModulePath -Parent
    . (Join-Path $ModuleRoot 'Private' 'JIMTokenStore.ps1')
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMTokenCacheKey' {

    It 'Normalises a trailing slash to the same key' {
        $a = Get-JIMTokenCacheKey -BaseUrl 'https://jim.company.com'
        $b = Get-JIMTokenCacheKey -BaseUrl 'https://jim.company.com/'
        $a | Should -Be $b
    }

    It 'Includes the explicit default port for https' {
        Get-JIMTokenCacheKey -BaseUrl 'https://jim.company.com' | Should -Be 'https://jim.company.com:443'
    }

    It 'Preserves a non-default port' {
        Get-JIMTokenCacheKey -BaseUrl 'http://localhost:5200' | Should -Be 'http://localhost:5200'
    }

    It 'Lower-cases the host and scheme' {
        Get-JIMTokenCacheKey -BaseUrl 'HTTPS://JIM.Company.COM' | Should -Be 'https://jim.company.com:443'
    }

    It 'Produces different keys for different schemes' {
        $http = Get-JIMTokenCacheKey -BaseUrl 'http://jim.company.com'
        $https = Get-JIMTokenCacheKey -BaseUrl 'https://jim.company.com'
        $http | Should -Not -Be $https
    }

    It 'Produces different keys for different ports' {
        $a = Get-JIMTokenCacheKey -BaseUrl 'http://localhost:5200'
        $b = Get-JIMTokenCacheKey -BaseUrl 'http://localhost:5300'
        $a | Should -Not -Be $b
    }

    It 'Produces different keys for different hosts' {
        $a = Get-JIMTokenCacheKey -BaseUrl 'https://jim1.company.com'
        $b = Get-JIMTokenCacheKey -BaseUrl 'https://jim2.company.com'
        $a | Should -Not -Be $b
    }
}

Describe 'Get-JIMTokenStoreProvider' {

    It 'Returns LibSecret on Linux when secret-tool is available' -Skip:(-not $IsLinux) {
        Mock Get-Command { [PSCustomObject]@{ Name = 'secret-tool' } } -ParameterFilter { $Name -eq 'secret-tool' }
        Get-JIMTokenStoreProvider | Should -Be 'LibSecret'
    }

    It 'Returns None on Linux when secret-tool is absent' -Skip:(-not $IsLinux) {
        Mock Get-Command { $null } -ParameterFilter { $Name -eq 'secret-tool' }
        Get-JIMTokenStoreProvider | Should -Be 'None'
    }

    It 'Test-JIMTokenPersistenceAvailable is false when provider is None' {
        Mock Get-JIMTokenStoreProvider { 'None' }
        Test-JIMTokenPersistenceAvailable | Should -BeFalse
    }

    It 'Test-JIMTokenPersistenceAvailable is true when a provider exists' {
        Mock Get-JIMTokenStoreProvider { 'LibSecret' }
        Test-JIMTokenPersistenceAvailable | Should -BeTrue
    }
}

Describe 'Save-JIMToken' {

    It 'Returns false and does not call the store when no provider is available' {
        Mock Get-JIMTokenStoreProvider { 'None' }
        Mock Invoke-JIMStoreProcess { throw 'should not be called' }
        $result = Save-JIMToken -BaseUrl 'https://jim.company.com' -RefreshToken 'rt-123'
        $result | Should -BeFalse
        Should -Invoke Invoke-JIMStoreProcess -Times 0
    }

    It 'Stores via libsecret with the token piped on stdin (not on the command line)' {
        Mock Get-JIMTokenStoreProvider { 'LibSecret' }
        Mock Invoke-JIMStoreProcess { [PSCustomObject]@{ ExitCode = 0; StdOut = ''; StdErr = '' } }

        $result = Save-JIMToken -BaseUrl 'https://jim.company.com' -RefreshToken 'secret-refresh-token'

        $result | Should -BeTrue
        Should -Invoke Invoke-JIMStoreProcess -Times 1 -ParameterFilter {
            $FilePath -eq 'secret-tool' -and
            $Arguments -contains 'store' -and
            $Arguments -contains 'https://jim.company.com:443' -and
            $StdinData -eq 'secret-refresh-token'
        }
    }

    It 'Throws a clear error when libsecret fails' {
        Mock Get-JIMTokenStoreProvider { 'LibSecret' }
        Mock Invoke-JIMStoreProcess { [PSCustomObject]@{ ExitCode = 1; StdOut = ''; StdErr = 'boom' } }
        { Save-JIMToken -BaseUrl 'https://jim.company.com' -RefreshToken 'rt' } | Should -Throw '*libsecret*'
    }

    It 'Passes the secret as an argument with -U on macOS' {
        Mock Get-JIMTokenStoreProvider { 'MacOS' }
        Mock Invoke-JIMStoreProcess { [PSCustomObject]@{ ExitCode = 0; StdOut = ''; StdErr = '' } }

        Save-JIMToken -BaseUrl 'https://jim.company.com' -RefreshToken 'rt-mac' | Should -BeTrue

        Should -Invoke Invoke-JIMStoreProcess -Times 1 -ParameterFilter {
            $FilePath -eq 'security' -and
            $Arguments -contains 'add-generic-password' -and
            $Arguments -contains '-U' -and
            $Arguments -contains 'rt-mac'
        }
    }
}

Describe 'Get-JIMPersistedToken' {

    It 'Returns null when no provider is available' {
        Mock Get-JIMTokenStoreProvider { 'None' }
        Get-JIMPersistedToken -BaseUrl 'https://jim.company.com' | Should -BeNullOrEmpty
    }

    It 'Returns the trimmed token from libsecret' {
        Mock Get-JIMTokenStoreProvider { 'LibSecret' }
        Mock Invoke-JIMStoreProcess { [PSCustomObject]@{ ExitCode = 0; StdOut = "my-token`n"; StdErr = '' } }
        Get-JIMPersistedToken -BaseUrl 'https://jim.company.com' | Should -Be 'my-token'
    }

    It 'Returns null when libsecret reports not found' {
        Mock Get-JIMTokenStoreProvider { 'LibSecret' }
        Mock Invoke-JIMStoreProcess { [PSCustomObject]@{ ExitCode = 1; StdOut = ''; StdErr = '' } }
        Get-JIMPersistedToken -BaseUrl 'https://jim.company.com' | Should -BeNullOrEmpty
    }

    It 'Looks up by the normalised cache key' {
        Mock Get-JIMTokenStoreProvider { 'LibSecret' }
        Mock Invoke-JIMStoreProcess { [PSCustomObject]@{ ExitCode = 0; StdOut = 'tok'; StdErr = '' } }
        Get-JIMPersistedToken -BaseUrl 'https://jim.company.com/' | Out-Null
        Should -Invoke Invoke-JIMStoreProcess -Times 1 -ParameterFilter {
            $Arguments -contains 'lookup' -and $Arguments -contains 'https://jim.company.com:443'
        }
    }
}

Describe 'Remove-JIMToken' {

    It 'Returns 0 when no provider is available' {
        Mock Get-JIMTokenStoreProvider { 'None' }
        Remove-JIMToken -BaseUrl 'https://jim.company.com' | Should -Be 0
    }

    It 'Clears a single libsecret entry by url' {
        Mock Get-JIMTokenStoreProvider { 'LibSecret' }
        Mock Invoke-JIMStoreProcess { [PSCustomObject]@{ ExitCode = 0; StdOut = ''; StdErr = '' } }
        Remove-JIMToken -BaseUrl 'https://jim.company.com' | Should -Be 1
        Should -Invoke Invoke-JIMStoreProcess -Times 1 -ParameterFilter {
            $Arguments -contains 'clear' -and $Arguments -contains 'url' -and $Arguments -contains 'https://jim.company.com:443'
        }
    }

    It 'Clears all libsecret entries by service only when -All is used' {
        Mock Get-JIMTokenStoreProvider { 'LibSecret' }
        Mock Invoke-JIMStoreProcess { [PSCustomObject]@{ ExitCode = 0; StdOut = ''; StdErr = '' } }
        Remove-JIMToken -All | Out-Null
        Should -Invoke Invoke-JIMStoreProcess -Times 1 -ParameterFilter {
            $Arguments -contains 'clear' -and ($Arguments -notcontains 'url')
        }
    }
}
