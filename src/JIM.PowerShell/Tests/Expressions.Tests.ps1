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

    It 'Posts the Attribute Flow to test the expression as' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; outcome = 'Values'; valueLines = @('2026-03-02') }
            }

            Test-JIMExpression -Expression 'FormatDate((DateTime)cs["hireDate"], "yyyy-MM-dd")' -SyncRuleId 12 -MappingId 40 -CsAttributes @{ hireDate = '2026-03-02' } | Out-Null

            $script:capturedBody.syncRuleId | Should -Be 12
            $script:capturedBody.mappingId | Should -Be 40
            $script:capturedBody.csAttributes.hireDate | Should -Be '2026-03-02'
        }
    }

    It 'Returns what the Attribute Flow would do, as the API reports it' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    IsValid            = $false
                    Outcome            = 'Error'
                    ErrorReason        = 'ResultNotConvertible'
                    OutcomeDescription = "The Expression returned 'Priya', which a Number attribute cannot hold."
                    ResultLines        = @('Priya')
                    TargetAttributeName = 'Employee Number'
                }
            }

            $result = Test-JIMExpression -Expression 'cs["givenName"]' -SyncRuleId 12 -MappingId 40 -CsAttributes @{ givenName = 'Priya' }

            $result.Outcome | Should -Be 'Error'
            $result.ErrorReason | Should -Be 'ResultNotConvertible'
            $result.TargetAttributeName | Should -Be 'Employee Number'
        }
    }

    It 'Does not send a Synchronisation Rule when no Attribute Flow is given' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true }
            }

            Test-JIMExpression -Expression '1 + 1' | Out-Null

            $script:capturedBody.ContainsKey('syncRuleId') | Should -BeFalse
            $script:capturedBody.ContainsKey('mappingId') | Should -BeFalse
        }
    }

    It 'Requires -MappingId with -SyncRuleId' {
        { Test-JIMExpression -Expression '1' -SyncRuleId 12 -ErrorAction Stop } | Should -Throw
    }
}
