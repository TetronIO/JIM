# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the JIM API client's HTTP 429 (rate limit) handling in Invoke-JIMApiRequest.

.DESCRIPTION
    The JIM REST API rate limiter returns 429 with a Retry-After header when a principal exceeds its
    per-minute request budget. A well-behaved client honours Retry-After and retries with bounded backoff
    rather than hard-failing the caller. These tests drive the private Invoke-JIMApiRequest with a mocked
    Invoke-RestMethod so no real HTTP or waiting occurs.

    The exception-builder helpers are defined in the global scope so they are visible inside InModuleScope
    JIM blocks (a plain script-scoped function in this file would not be, because InModuleScope switches to
    the module's scope chain, which excludes the test file's script scope).
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    # Builds a real HttpResponseException carrying a 429 status and an integer Retry-After header, matching what
    # Invoke-RestMethod throws in PowerShell 7 against JIM's rate limiter.
    function global:New-429Exception {
        param([int]$RetryAfterSeconds = 1)
        $resp = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::TooManyRequests)
        $resp.Headers.Add('Retry-After', "$RetryAfterSeconds")
        return [Microsoft.PowerShell.Commands.HttpResponseException]::new('Too Many Requests', $resp)
    }

    function global:New-429ExceptionNoHeader {
        $resp = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::TooManyRequests)
        return [Microsoft.PowerShell.Commands.HttpResponseException]::new('Too Many Requests', $resp)
    }

    function global:New-500Exception {
        $resp = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::InternalServerError)
        return [Microsoft.PowerShell.Commands.HttpResponseException]::new('Server Error', $resp)
    }
}

AfterAll {
    Remove-Item function:global:New-429Exception -ErrorAction SilentlyContinue
    Remove-Item function:global:New-429ExceptionNoHeader -ErrorAction SilentlyContinue
    Remove-Item function:global:New-500Exception -ErrorAction SilentlyContinue
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Invoke-JIMApiRequest 429 rate-limit handling' {

    It 'Retries after a 429 honouring Retry-After, then returns the successful response' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey'; ApiKey = 'jim_ak_test' }
            $script:rlCallCount = 0
            Mock Start-Sleep { }
            Mock Invoke-RestMethod {
                $script:rlCallCount++
                if ($script:rlCallCount -eq 1) {
                    throw (New-429Exception -RetryAfterSeconds 3)
                }
                return [PSCustomObject]@{ ok = $true }
            }

            $result = Invoke-JIMApiRequest -Endpoint '/api/v1/test'

            $result.ok | Should -BeTrue
            $script:rlCallCount | Should -Be 2
            Should -Invoke Start-Sleep -Times 1 -Exactly -Scope It
            Should -Invoke Start-Sleep -Times 1 -Exactly -Scope It -ParameterFilter { $Seconds -eq 3 }
        }
    }

    It 'Retries multiple times then succeeds within the retry budget' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey'; ApiKey = 'jim_ak_test' }
            $script:rlCallCount = 0
            Mock Start-Sleep { }
            Mock Invoke-RestMethod {
                $script:rlCallCount++
                if ($script:rlCallCount -lt 4) {
                    throw (New-429Exception -RetryAfterSeconds 1)
                }
                return [PSCustomObject]@{ ok = $true }
            }

            $result = Invoke-JIMApiRequest -Endpoint '/api/v1/test'

            $result.ok | Should -BeTrue
            $script:rlCallCount | Should -Be 4
            Should -Invoke Start-Sleep -Times 3 -Exactly -Scope It
        }
    }

    It 'Throws a 429 error once the retry budget is exhausted' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey'; ApiKey = 'jim_ak_test' }
            $script:rlCallCount = 0
            Mock Start-Sleep { }
            Mock Invoke-RestMethod {
                $script:rlCallCount++
                throw (New-429Exception -RetryAfterSeconds 1)
            }

            { Invoke-JIMApiRequest -Endpoint '/api/v1/test' } | Should -Throw '*429*'
            # 1 initial attempt + 5 retries = 6 total calls.
            $script:rlCallCount | Should -Be 6
        }
    }

    It 'Does not retry non-429 errors' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey'; ApiKey = 'jim_ak_test' }
            $script:rlCallCount = 0
            Mock Start-Sleep { }
            Mock Invoke-RestMethod {
                $script:rlCallCount++
                throw (New-500Exception)
            }

            { Invoke-JIMApiRequest -Endpoint '/api/v1/test' } | Should -Throw '*500*'
            $script:rlCallCount | Should -Be 1
            Should -Invoke Start-Sleep -Times 0 -Exactly -Scope It
        }
    }
}

Describe 'Get-JIMRetryAfterSeconds' {

    It 'Returns the Retry-After delta when the header is present' {
        InModuleScope JIM {
            Get-JIMRetryAfterSeconds -Exception (New-429Exception -RetryAfterSeconds 7) -AttemptNumber 0 | Should -Be 7
        }
    }

    It 'Falls back to exponential backoff when no Retry-After header is present' {
        InModuleScope JIM {
            # Base 2s, attempt 0 -> 2s, attempt 2 -> 8s (2 * 2^2).
            Get-JIMRetryAfterSeconds -Exception (New-429ExceptionNoHeader) -AttemptNumber 0 | Should -Be 2
            Get-JIMRetryAfterSeconds -Exception (New-429ExceptionNoHeader) -AttemptNumber 2 | Should -Be 8
        }
    }

    It 'Clamps an absurdly large Retry-After to the maximum wait' {
        InModuleScope JIM {
            Get-JIMRetryAfterSeconds -Exception (New-429Exception -RetryAfterSeconds 99999) -AttemptNumber 0 | Should -Be 60
        }
    }
}
