# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemContainerScopeText' {
    It 'Reads the Container Scope text for a Connected System' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedEndpoint = $null
            $script:capturedMethod = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                $script:capturedMethod = $Method
                [PSCustomObject]@{ Text = "include OU=Corp,DC=example,DC=com" }
            }

            $result = Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 2

            $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/connected-systems/2/container-scope-text'
            $script:capturedMethod | Should -Be 'GET'
            $result | Should -Be 'include OU=Corp,DC=example,DC=com'
        }
    }

    It 'Returns the text itself, not the object wrapping it, so it pipes straight into Set-' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ Text = "include OU=Corp,DC=example,DC=com`nexclude OU=Svc,OU=Corp,DC=example,DC=com" } }

            $result = Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 2

            $result | Should -BeOfType [string]
            $result.Split("`n").Count | Should -Be 2
        }
    }
}

Describe 'Set-JIMConnectedSystemContainerScopeText' {
    It 'Sends the text to the Connected System it names' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedEndpoint = $null
            $script:capturedMethod = $null
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                $script:capturedMethod = $Method
                $script:capturedBody = $Body
                [PSCustomObject]@{ Text = 'include OU=Corp,DC=example,DC=com' }
            }

            Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 2 -Text 'include OU=Corp,DC=example,DC=com' -Confirm:$false

            $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/connected-systems/2/container-scope-text'
            $script:capturedMethod | Should -Be 'PUT'
            $script:capturedBody.text | Should -Be 'include OU=Corp,DC=example,DC=com'
        }
    }

    It 'Sends empty text rather than dropping it, because clearing the scope is a real instruction' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ Text = '' }
            }

            Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 2 -Text '' -Confirm:$false

            $script:capturedBody.ContainsKey('text') | Should -BeTrue
            $script:capturedBody.text | Should -Be ''
        }
    }

    It 'Returns nothing unless asked, and the canonical text when asked' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ Text = 'include OU=Corp,DC=example,DC=com' } }

            $silent = Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 2 -Text '+ OU=Corp,DC=example,DC=com' -Confirm:$false
            $passed = Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 2 -Text '+ OU=Corp,DC=example,DC=com' -PassThru -Confirm:$false

            $silent | Should -BeNullOrEmpty
            $passed | Should -Be 'include OU=Corp,DC=example,DC=com'
        }
    }

    It 'Reads the text from the pipeline, so a scope can be moved between Connected Systems' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ Text = 'include OU=Corp,DC=example,DC=com' }
            }

            'include OU=Corp,DC=example,DC=com' | Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 3 -Confirm:$false

            $script:capturedBody.text | Should -Be 'include OU=Corp,DC=example,DC=com'
        }
    }

    It 'Does not send anything when the caller declines the confirmation' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ Text = '' } }

            Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 2 -Text 'include OU=Corp,DC=example,DC=com' -WhatIf

            Should -Invoke Invoke-JIMApi -Times 0
        }
    }
}
