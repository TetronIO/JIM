# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for a Connected System's unresolved references (#959): Get-JIMConnectedSystemUnresolvedReferenceSummary,
    and Get-JIMConnectedSystemObject -HasUnresolvedReference.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemUnresolvedReferenceSummary' {

    It 'Should be exported by the module' {
        Get-Command Get-JIMConnectedSystemUnresolvedReferenceSummary -Module JIM | Should -Not -BeNullOrEmpty
    }

    It 'Should request the Connected System''s summary' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ connectedSystemId = 3; totalCount = 1284; objectCount = 312; causes = @(); fixes = @() } }

            $result = Get-JIMConnectedSystemUnresolvedReferenceSummary -ConnectedSystemId 3

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/3/connector-space/unresolved-references/summary'
            }
            $result.totalCount | Should -Be 1284
            $result.objectCount | Should -Be 312
        }
    }

    It 'Should take a Connected System from the pipeline' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ totalCount = 0; objectCount = 0; causes = @(); fixes = @() } }

            [PSCustomObject]@{ Id = 7; Name = 'Corp AD' } | Get-JIMConnectedSystemUnresolvedReferenceSummary | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/7/connector-space/unresolved-references/summary'
            }
        }
    }

    It 'Should refuse to run when not connected' {
        InModuleScope JIM {
            $script:JIMConnection = $null
            Mock Invoke-JIMApi { }

            Get-JIMConnectedSystemUnresolvedReferenceSummary -ConnectedSystemId 3 -ErrorAction SilentlyContinue -ErrorVariable failure | Out-Null

            $failure | Should -Not -BeNullOrEmpty
            Should -Invoke Invoke-JIMApi -Times 0 -Exactly
        }
    }
}

Describe 'Get-JIMConnectedSystemObject -HasUnresolvedReference' {

    It 'Should offer the switch when listing, not when reading one object' {
        $param = (Get-Command Get-JIMConnectedSystemObject).Parameters['HasUnresolvedReference']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
        $param.ParameterSets.Keys | Should -Contain 'List'
        $param.ParameterSets.Keys | Should -Contain 'ListAll'
        $param.ParameterSets.Keys | Should -Not -Contain 'ById'
    }

    It 'Should filter a page of the Connector Space to the objects holding unresolved references' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ items = @([PSCustomObject]@{ id = [guid]::NewGuid(); unresolvedReferenceCount = 412 }); hasNextPage = $false; totalCount = 1 } }

            $result = Get-JIMConnectedSystemObject -ConnectedSystemId 3 -HasUnresolvedReference

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/synchronisation/connected-systems/3/connector-space?page=1&pageSize=50&hasUnresolvedReferences=true'
            }
            $result.unresolvedReferenceCount | Should -Be 412
        }
    }

    It 'Should filter every page with -All' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 1; totalCount = 0 } }

            Get-JIMConnectedSystemObject -ConnectedSystemId 3 -All -HasUnresolvedReference | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -like '*/connector-space?page=1&pageSize=50*&hasUnresolvedReferences=true'
            }
        }
    }

    It 'Should not filter without the switch' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalCount = 0 } }

            Get-JIMConnectedSystemObject -ConnectedSystemId 3 | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -notlike '*hasUnresolvedReferences*'
            }
        }
    }
}
