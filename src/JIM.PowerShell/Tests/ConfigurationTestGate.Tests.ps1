# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the save gate (#2055 Phase 4b) in PowerShell: -SaveAnyway and -AcceptNewResult on every cmdlet
    that saves configuration a configuration test case can cover, and the error a refused change raises.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    # A 409 Conflict as Invoke-RestMethod throws it in PowerShell 7. Global, so InModuleScope blocks can see it.
    function global:New-409Exception {
        $resp = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::Conflict)
        return [Microsoft.PowerShell.Commands.HttpResponseException]::new('Conflict', $resp)
    }
}

AfterAll {
    Remove-Item function:global:New-409Exception -ErrorAction SilentlyContinue
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Save gate switches' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
        }
    }

    It 'Every cmdlet that saves covered configuration takes -SaveAnyway and -AcceptNewResult' {
        $cmdlets = 'Set-JIMSyncRule', 'Set-JIMSyncRuleMapping', 'New-JIMScopingCriteriaGroup', 'Set-JIMScopingCriteriaGroup',
            'Remove-JIMScopingCriteriaGroup', 'New-JIMScopingCriterion', 'Set-JIMScopingCriterion', 'Remove-JIMScopingCriterion',
            'Set-JIMMatchingRule', 'Set-JIMSyncRuleMatchingRule'
        foreach ($cmdlet in $cmdlets) {
            $parameters = (Get-Command $cmdlet).Parameters
            $parameters['SaveAnyway'].ParameterType | Should -Be ([switch]) -Because $cmdlet
            $parameters['AcceptNewResult'].ParameterType | Should -Be ([switch]) -Because $cmdlet
        }
    }

    It 'Sends saveAnyway in the body when -SaveAnyway is given' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 8 } }

            Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 8 -Expression 'Lower(cs["sn"])' -SaveAnyway -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PATCH' -and $Body.saveAnyway -eq $true -and -not $Body.ContainsKey('acceptNewResults')
            }
        }
    }

    It 'Sends acceptNewResults in the body when -AcceptNewResult is given' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 7 } }

            Set-JIMMatchingRule -ConnectedSystemId 1 -Id 7 -SourceExpression 'Lower(cs["employeeId"])' -AcceptNewResult -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PUT' -and $Body.acceptNewResults -eq $true -and -not $Body.ContainsKey('saveAnyway')
            }
        }
    }

    It 'Sends neither when neither switch is given, so a change that breaks a case is refused' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 10 } }

            Set-JIMScopingCriteriaGroup -SyncRuleId 1 -GroupId 10 -Type Any -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PUT' -and -not $Body.ContainsKey('saveAnyway') -and -not $Body.ContainsKey('acceptNewResults')
            }
        }
    }

    It 'Sends both on the query string for a removal' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { }

            Remove-JIMScopingCriterion -SyncRuleId 1 -GroupId 10 -CriterionId 15 -SaveAnyway -AcceptNewResult -Confirm:$false

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'DELETE' -and
                $Endpoint -eq '/api/v1/synchronisation/sync-rules/1/scoping-criteria/10/criteria/15?saveAnyway=true&acceptNewResults=true'
            }
        }
    }

    It 'Sends no query string for a removal without either switch' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { }

            Remove-JIMScopingCriteriaGroup -SyncRuleId 1 -GroupId 10 -Confirm:$false

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/sync-rules/1/scoping-criteria/10'
            }
        }
    }
}

Describe 'A change the save gate refuses' {

    It 'Names each case and what it now gives, and says how to go ahead' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey'; ApiKey = 'jim_ak_test' }
            Mock Invoke-RestMethod {
                $body = @{
                    message     = "This change stops the configuration test case 'Surname flows' passing."
                    regressions = @(@{ result = @{ testCaseName = 'Surname flows'; summary = "Expected 'BLOGGS', but got 'bloggs'." }; canAccept = $true })
                } | ConvertTo-Json -Depth 5
                $record = [System.Management.Automation.ErrorRecord]::new((New-409Exception), 'Conflict', 'InvalidOperation', $null)
                $record.ErrorDetails = [System.Management.Automation.ErrorDetails]::new($body)
                throw $record
            }

            { Invoke-JIMApiRequest -Endpoint '/api/v1/synchronisation/sync-rules/1/mappings/8' -Method 'PATCH' -Body @{ expression = 'x' } } |
                Should -Throw -ExpectedMessage "*(409)*'Surname flows': Expected 'BLOGGS', but got 'bloggs'.*-AcceptNewResult*-SaveAnyway*"
        }
    }
}
