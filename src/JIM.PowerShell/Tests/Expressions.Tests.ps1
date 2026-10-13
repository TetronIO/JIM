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

    It 'Posts the instant to test as of' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; resultLines = @('2027-01-15') }
            }

            Test-JIMExpression -Expression 'FormatDate(Today(), "yyyy-MM-dd")' -AsOf '2027-01-15' | Out-Null
            $script:capturedBody.asOf | Should -Be '2027-01-15T00:00:00'

            Test-JIMExpression -Expression 'Now()' -AsOf ([datetime]::new(2027, 1, 15, 9, 30, 0, [DateTimeKind]::Utc)) | Out-Null
            $script:capturedBody.asOf | Should -Be '2027-01-15T09:30:00Z'
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

    It 'Posts the object to load values from' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; outcome = 'Values' }
            }
            $csoId = [guid]'0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01'

            Test-JIMExpression -Expression 'cs["givenName"]' -SyncRuleId 12 -MappingId 40 -ConnectedSystemObjectId $csoId | Out-Null

            $script:capturedBody.connectedSystemObjectId | Should -Be $csoId
            $script:capturedBody.ContainsKey('metaverseObjectId') | Should -BeFalse
        }
    }

    It 'Posts the Metaverse Object to load values from' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; outcome = 'Values' }
            }
            $mvoId = [guid]'7c1d2e3f-4a5b-4c6d-8e9f-0a1b2c3d4e5f'

            Test-JIMExpression -Expression 'mv["Display Name"]' -SyncRuleId 12 -MappingId 41 -MetaverseObjectId $mvoId | Out-Null

            $script:capturedBody.metaverseObjectId | Should -Be $mvoId
        }
    }

    It 'Offers loading from an object only with an Attribute Flow' {
        $command = Get-Command Test-JIMExpression
        $command.Parameters['ConnectedSystemObjectId'].ParameterSets.Keys | Should -Be @('AttributeFlow')
        $command.Parameters['MetaverseObjectId'].ParameterSets.Keys | Should -Be @('AttributeFlow')
    }
}
