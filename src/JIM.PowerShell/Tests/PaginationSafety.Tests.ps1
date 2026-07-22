# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the shared -All pagination safety bounding (issue #487) across every paginated
    Get-* cmdlet whose -All auto-paginates. The two originally-capped cmdlets (Get-JIMMetaverseObject
    and Get-JIMConnectedSystemObject) keep their own cap tests in their area files; this file covers
    the remaining seven so that every -All path shares one bounded, -Force-overridable behaviour.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

# Data-driven matrix: each cmdlet whose -All auto-paginates, the parameter set its -Force switch
# lives in, and the representative mandatory parameters to drive it in -All mode. Splatting BaseParams
# inside the module scope means the same bounding contract is asserted identically for every cmdlet.
Describe 'Pagination safety (-All bounding) via Invoke-JIMPagedFetch' -ForEach @(
    @{ Cmdlet = 'Search-JIMMetaverseObject'; ForceSet = 'ListAll'; BaseParams = @{ PredefinedSearchUri = 'users' } }
    @{ Cmdlet = 'Get-JIMMetaverseObjectChangeHistory'; ForceSet = 'All'; BaseParams = @{ Id = [guid]::Empty } }
    @{ Cmdlet = 'Get-JIMConnectedSystemObjectAttributeValue'; ForceSet = 'All'; BaseParams = @{ ConnectedSystemId = 1; CsoId = [guid]::Empty; AttributeName = 'member' } }
    @{ Cmdlet = 'Get-JIMConnectedSystemObjectChangeHistory'; ForceSet = 'All'; BaseParams = @{ ConnectedSystemId = 1; Id = [guid]::Empty } }
    @{ Cmdlet = 'Get-JIMPendingExport'; ForceSet = 'ListAll'; BaseParams = @{ ConnectedSystemId = 2 } }
    @{ Cmdlet = 'Get-JIMConfigurationChangeHistory'; ForceSet = 'All'; BaseParams = @{ Type = 'ConnectedSystem'; Id = '9' } }
    @{ Cmdlet = 'Get-JIMActivityChildren'; ForceSet = 'All'; BaseParams = @{ Id = [guid]::Empty } }
) {

    It '<Cmdlet> exposes a Force switch in its -All parameter set' {
        $param = (Get-Command $Cmdlet).Parameters['Force']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ParameterSetName -eq $ForceSet }
        $paramAttr | Should -Not -BeNullOrEmpty
    }

    It '<Cmdlet> -All stops at the page cap and warns when the cap is reached without -Force' {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet; BaseParams = $BaseParams } {
            param($Cmdlet, $BaseParams)
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $original = $script:JIMMaxAllPages
            try {
                $script:JIMMaxAllPages = 3
                # Always report another page so, without a cap, this would page forever.
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @([PSCustomObject]@{ id = [guid]::NewGuid() }); hasNextPage = $true; totalPages = 999999; totalCount = 100 } }

                & $Cmdlet @BaseParams -All -WarningVariable w -WarningAction SilentlyContinue | Out-Null

                Should -Invoke Invoke-JIMApi -Times 3 -Exactly
                ($w -join ' ') | Should -Match 'stopped after 3 pages'
                ($w -join ' ') | Should -Match '-Force'
            }
            finally {
                $script:JIMMaxAllPages = $original
            }
        }
    }

    It '<Cmdlet> -All -Force overrides the page cap and fetches every page' {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet; BaseParams = $BaseParams } {
            param($Cmdlet, $BaseParams)
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $original = $script:JIMMaxAllPages
            try {
                $script:JIMMaxAllPages = 2
                $script:allPollCount = 0
                # Five pages of data; the cap is 2, so only -Force should reach page 5.
                Mock Invoke-JIMApi {
                    $script:allPollCount++
                    [PSCustomObject]@{ items = @([PSCustomObject]@{ id = [guid]::NewGuid() }); hasNextPage = ($script:allPollCount -lt 5); totalPages = 5; totalCount = 100 }
                }

                & $Cmdlet @BaseParams -All -Force -WarningVariable w -WarningAction SilentlyContinue | Out-Null

                Should -Invoke Invoke-JIMApi -Times 5 -Exactly
                ($w -join ' ') | Should -Not -Match 'stopped after'
            }
            finally {
                $script:JIMMaxAllPages = $original
            }
        }
    }

    It '<Cmdlet> -All warns up front when the result set is large' {
        InModuleScope JIM -Parameters @{ Cmdlet = $Cmdlet; BaseParams = $BaseParams } {
            param($Cmdlet, $BaseParams)
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            # Single page, but a large totalCount should trigger the up-front warning.
            Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false; totalPages = 500; totalCount = 50000 } }

            & $Cmdlet @BaseParams -All -WarningVariable w -WarningAction SilentlyContinue | Out-Null

            ($w -join ' ') | Should -Match 'large result set'
        }
    }
}
