# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for what Invoke-JIMApiRequest says when JIM refuses a request with HTTP 403.

.DESCRIPTION
    JIM answers 403 to someone it knows whose roles do not allow the operation, as well as to someone it has no
    identity for, so the error names both, and the status, rather than telling a provisioned person they are not
    provisioned (#2441). An API key has no identity to provision, so its error names the key's roles only.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    function global:New-403Exception {
        $resp = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::Forbidden)
        return [Microsoft.PowerShell.Commands.HttpResponseException]::new('Forbidden', $resp)
    }
}

AfterAll {
    Remove-Item function:global:New-403Exception -ErrorAction SilentlyContinue
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Invoke-JIMApiRequest 403 handling' {

    It 'Tells a signed-in person their roles may not allow it, or they may not be provisioned, and names the status' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'OAuth'; AccessToken = 'token' }
            Mock Invoke-RestMethod { throw (New-403Exception) }

            $message = try { Invoke-JIMApiRequest -Endpoint '/api/v1/security/roles'; $null } catch { "$_" }

            $message | Should -Match '\(403\)'
            $message | Should -Match 'none of your JIM roles'
            $message | Should -Match 'not been provisioned'
        }
    }

    It "Tells an API key's caller that the key's roles do not allow it, and names the status" {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey'; ApiKey = 'jim_ak_test' }
            Mock Invoke-RestMethod { throw (New-403Exception) }

            $message = try { Invoke-JIMApiRequest -Endpoint '/api/v1/security/roles'; $null } catch { "$_" }

            $message | Should -Match '\(403\)'
            $message | Should -Match "none of this API key's roles"
            $message | Should -Not -Match 'provisioned'
        }
    }
}
