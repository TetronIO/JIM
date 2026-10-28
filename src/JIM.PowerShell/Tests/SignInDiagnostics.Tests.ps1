# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMSignInDiagnostic (#2443): the sign-in problems JIM has met, and the redirect URIs it sends.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMSignInDiagnostic' {

    It 'Should throw when not connected' {
        Disconnect-JIM
        { Get-JIMSignInDiagnostic -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
    }

    It 'Reads the sign-in diagnostics' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    redirectUri = 'https://jim.example.com/signin-oidc'
                    postLogoutRedirectUri = 'https://jim.example.com/signout-callback-oidc'
                    problems = @(
                        [PSCustomObject]@{ source = 'Api'; setting = 'JIM_SSO_API_SCOPE'; expected = 'api://jim'; received = 'account'; count = 3 }
                    )
                }
            }

            $diagnostics = Get-JIMSignInDiagnostic

            $diagnostics.redirectUri | Should -Be 'https://jim.example.com/signin-oidc'
            $diagnostics.problems[0].setting | Should -Be 'JIM_SSO_API_SCOPE'
            Should -Invoke Invoke-JIMApi -Times 1 -ParameterFilter { $Endpoint -eq '/api/v1/auth/sign-in-diagnostics' -and -not $Method }
        }
    }

    It 'Reports a failure as an error' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { throw 'Forbidden' }

            { Get-JIMSignInDiagnostic -ErrorAction Stop } | Should -Throw '*sign-in diagnostics*'
        }
    }

    It 'Has help with examples' {
        (Get-Help Get-JIMSignInDiagnostic -Full).Examples.Example.Count | Should -BeGreaterThan 0
    }
}
