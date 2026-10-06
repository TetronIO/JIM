# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the -MetaverseAttributeName path of the criterion cmdlets (#1965).
.DESCRIPTION
    Each cmdlet resolved the name by reading /api/v1/metaverse/attributes once and filtering the response, but the
    endpoint returns a paged envelope, so the filter matched nothing and the name path always failed. Each must
    resolve through Resolve-JIMMetaverseAttribute, which reads every page (#894). The mock puts the attribute on the
    second page, so a lookup that reads only the first still fails.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe '<Cmdlet> -MetaverseAttributeName' -ForEach @(
    @{ Cmdlet = 'New-JIMScopingCriterion'; ExpectedMethod = 'POST'; BaseParams = @{ SyncRuleId = 1; GroupId = 5 } }
    @{ Cmdlet = 'Set-JIMScopingCriterion'; ExpectedMethod = 'PUT'; BaseParams = @{ SyncRuleId = 1; GroupId = 5; CriterionId = 9 } }
    @{ Cmdlet = 'New-JIMPredefinedSearchCriterion'; ExpectedMethod = 'POST'; BaseParams = @{ PredefinedSearchId = 3; GroupId = 10 } }
    @{ Cmdlet = 'Set-JIMPredefinedSearchCriterion'; ExpectedMethod = 'PUT'; BaseParams = @{ PredefinedSearchId = 3; GroupId = 10; CriterionId = 9 } }
) {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

            # Two attributes, one per page, as the server pages them; Department is only on page two.
            Mock Invoke-JIMApi {
                if ($Endpoint -like '/api/v1/metaverse/attributes?*page=1*') {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 32; name = 'Display Name' }); page = 1; pageSize = 1; totalCount = 2; hasNextPage = $true }
                }
                elseif ($Endpoint -like '/api/v1/metaverse/attributes?*page=2*') {
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 33; name = 'Department' }); page = 2; pageSize = 1; totalCount = 2; hasNextPage = $false }
                }
                elseif ($Endpoint -eq '/api/v1/metaverse/attributes') {
                    # What the endpoint returns unpaged: the envelope, not an array of attributes.
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 32; name = 'Display Name' }); page = 1; pageSize = 1; totalCount = 2; hasNextPage = $true }
                }
                else {
                    [PSCustomObject]@{ id = 999 }
                }
            }
        }
    }

    It 'Resolves a name beyond the first page and sends its id' {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet; ExpectedMethod = $ExpectedMethod; BaseParams = $BaseParams } {
            param($Cmdlet, $ExpectedMethod, $BaseParams)

            & $Cmdlet @BaseParams -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Finance' -Confirm:$false | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq $ExpectedMethod -and $Body.metaverseAttributeId -eq 33
            }
        }
    }

    It 'Writes an error and sends nothing when the name matches no attribute' {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet; ExpectedMethod = $ExpectedMethod; BaseParams = $BaseParams } {
            param($Cmdlet, $ExpectedMethod, $BaseParams)

            & $Cmdlet @BaseParams -MetaverseAttributeName 'Nonexistent' -ComparisonType Equals -StringValue 'x' -Confirm:$false -ErrorVariable failures -ErrorAction SilentlyContinue | Out-Null

            ($failures -join ' ') | Should -Match 'Nonexistent'
            Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -in 'POST', 'PUT' }
        }
    }
}
