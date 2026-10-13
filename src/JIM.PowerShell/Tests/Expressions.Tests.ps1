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

Describe 'Test-JIMExpression' {
    It 'Returns the result as the lines the portal tester shows' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    isValid       = $true
                    result        = @('a', 'b')
                    resultLines   = @('a', 'b')
                    resultType    = 'String[]'
                    errorMessage  = $null
                    errorPosition = $null
                }
            }

            $result = Test-JIMExpression -Expression 'Split("a;b", ";")'

            $result.ResultLines | Should -Be @('a', 'b')
            $result.IsValid | Should -BeTrue
        }
    }

    It 'Posts the expression and sample attributes to the test-expression endpoint' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedEndpoint = $null
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; result = 'x'; resultLines = @('x'); resultType = 'String' }
            }

            Test-JIMExpression -Expression 'mv["Name"]' -MvAttributes @{ Name = 'x' } | Out-Null

            $script:capturedEndpoint | Should -Be '/api/v1/synchronisation/test-expression'
            $script:capturedBody.expression | Should -Be 'mv["Name"]'
            $script:capturedBody.mvAttributes.Name | Should -Be 'x'
        }
    }
}
