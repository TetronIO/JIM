# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests proving that cmdlets resolving a name to an id read every page of the list they search.

.DESCRIPTION
    The list endpoints behind name lookups answer with a paginated envelope (items, totalCount, page, ...),
    not an array. A cmdlet that calls one directly and filters the response by name never matches anything,
    and even with the envelope unwrapped it would see only the first page. The module's resolvers
    (Resolve-JIMMetaverseAttribute and friends) read every page; these tests drive each by-name parameter
    set (and the Connected System list) against a mock that answers the way the server does, with the target
    beyond the first page (#1967, and #894 before it). The sweep at the end stops the next cmdlet hand-rolling
    the same lookup.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Name lookups resolve across every page of the list they search' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

            # Answers like the API: a paginated envelope, default page size 25 when the caller names none.
            # The names under test sit beyond the first page of either size, so a lookup that reads one page fails.
            function script:Get-PagedTestResponse([string]$Endpoint, [object[]]$All) {
                $query = if ($Endpoint.Contains('?')) { $Endpoint.Substring($Endpoint.IndexOf('?') + 1) } else { '' }
                $parameters = @{}
                foreach ($pair in ($query -split '&' | Where-Object { $_ })) {
                    $name, $value = $pair -split '=', 2
                    $parameters[$name] = [int]$value
                }
                $page = if ($parameters.ContainsKey('page')) { $parameters['page'] } else { 1 }
                $pageSize = if ($parameters.ContainsKey('pageSize')) { $parameters['pageSize'] } else { 25 }
                $items = @($All | Select-Object -Skip (($page - 1) * $pageSize) -First $pageSize)
                [PSCustomObject]@{
                    items       = $items
                    totalCount  = $All.Count
                    page        = $page
                    pageSize    = $pageSize
                    totalPages  = [math]::Ceiling($All.Count / $pageSize)
                    hasNextPage = ($page * $pageSize) -lt $All.Count
                }
            }

            $script:testAttributes = @(1..150 | ForEach-Object { [PSCustomObject]@{ id = $_; name = "Attribute $_" } })
            $script:testAttributes[132] = [PSCustomObject]@{ id = 133; name = 'Department' }
            $script:testSystems = @(1..120 | ForEach-Object { [PSCustomObject]@{ id = $_; name = "System $_" } })
            $script:testSystems[109] = [PSCustomObject]@{ id = 110; name = 'HR' }

            Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1 } }
            Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '/api/v1/metaverse/attributes*' -and $Method -notin 'POST', 'PUT', 'PATCH', 'DELETE' } -MockWith {
                Get-PagedTestResponse -Endpoint $Endpoint -All $script:testAttributes
            }
            Mock Invoke-JIMApi -ParameterFilter { $Endpoint -like '/api/v1/synchronisation/connected-systems' -or $Endpoint -like '/api/v1/synchronisation/connected-systems[?]*' } -MockWith {
                Get-PagedTestResponse -Endpoint $Endpoint -All $script:testSystems
            }
        }
    }

    It 'New-JIMPredefinedSearchCriterion -MetaverseAttributeName sends the resolved attribute id' {
        InModuleScope JIM {
            New-JIMPredefinedSearchCriterion -PredefinedSearchId 2 -GroupId 7 -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Sales' -Confirm:$false -ErrorAction Stop | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'POST' -and $Endpoint -eq '/api/v1/predefined-searches/2/criteria-groups/7/criteria' -and $Body.metaverseAttributeId -eq 133
            }
        }
    }

    It 'Set-JIMPredefinedSearchCriterion -MetaverseAttributeName sends the resolved attribute id' {
        InModuleScope JIM {
            Set-JIMPredefinedSearchCriterion -PredefinedSearchId 2 -GroupId 7 -CriterionId 9 -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'Sales' -Confirm:$false -ErrorAction Stop | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PUT' -and $Endpoint -eq '/api/v1/predefined-searches/2/criteria-groups/7/criteria/9' -and $Body.metaverseAttributeId -eq 133
            }
        }
    }

    It 'New-JIMScopingCriterion -MetaverseAttributeName sends the resolved attribute id' {
        InModuleScope JIM {
            New-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'IT' -Confirm:$false -ErrorAction Stop | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'POST' -and $Endpoint -eq '/api/v1/synchronisation/sync-rules/5/scoping-criteria/10/criteria' -and $Body.metaverseAttributeId -eq 133
            }
        }
    }

    It 'Set-JIMScopingCriterion -MetaverseAttributeName sends the resolved attribute id' {
        InModuleScope JIM {
            Set-JIMScopingCriterion -SyncRuleId 5 -GroupId 10 -CriterionId 11 -MetaverseAttributeName 'Department' -ComparisonType Equals -StringValue 'IT' -Confirm:$false -ErrorAction Stop | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Method -eq 'PUT' -and $Endpoint -eq '/api/v1/synchronisation/sync-rules/5/scoping-criteria/10/criteria/11' -and $Body.metaverseAttributeId -eq 133
            }
        }
    }

    It 'Get-JIMHistoryCount -ConnectedSystemName reads the count for the resolved Connected System' {
        InModuleScope JIM {
            Get-JIMHistoryCount -ConnectedSystemName 'HR' -ErrorAction Stop | Out-Null

            Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                $Endpoint -eq '/api/v1/history/connected-systems/110/count'
            }
        }
    }

    It 'Get-JIMConnectedSystem lists every Connected System, not just the first page' {
        InModuleScope JIM {
            $systems = @(Get-JIMConnectedSystem -ErrorAction Stop)

            $systems.Count | Should -Be 120
        }
    }

    It 'Get-JIMConnectedSystem -Name finds a Connected System beyond the first page' {
        InModuleScope JIM {
            $systems = @(Get-JIMConnectedSystem -Name 'HR' -ErrorAction Stop)

            $systems.Count | Should -Be 1
            $systems[0].id | Should -Be 110
        }
    }

    It '<Command> reports an unknown name as an error and sends nothing' -ForEach @(
        @{ Command = 'New-JIMPredefinedSearchCriterion'; Arguments = @{ PredefinedSearchId = 2; GroupId = 7; MetaverseAttributeName = 'No Such Attribute'; ComparisonType = 'Equals'; StringValue = 'x'; Confirm = $false } }
        @{ Command = 'Set-JIMPredefinedSearchCriterion'; Arguments = @{ PredefinedSearchId = 2; GroupId = 7; CriterionId = 9; MetaverseAttributeName = 'No Such Attribute'; ComparisonType = 'Equals'; StringValue = 'x'; Confirm = $false } }
        @{ Command = 'New-JIMScopingCriterion'; Arguments = @{ SyncRuleId = 5; GroupId = 10; MetaverseAttributeName = 'No Such Attribute'; ComparisonType = 'Equals'; StringValue = 'x'; Confirm = $false } }
        @{ Command = 'Set-JIMScopingCriterion'; Arguments = @{ SyncRuleId = 5; GroupId = 10; CriterionId = 11; MetaverseAttributeName = 'No Such Attribute'; ComparisonType = 'Equals'; StringValue = 'x'; Confirm = $false } }
        @{ Command = 'Get-JIMHistoryCount'; Arguments = @{ ConnectedSystemName = 'No Such System' } }
    ) {
        InModuleScope JIM -Parameters @{ Command = $Command; Arguments = $Arguments } {
            param($Command, $Arguments)
            { & $Command @Arguments -ErrorAction Stop } | Should -Throw '*not found*'

            Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter {
                $Method -in 'POST', 'PUT' -or $Endpoint -like '/api/v1/history/*'
            }
        }
    }
}

Describe 'Public cmdlets resolve names through the module''s resolvers' {

    It 'No public cmdlet reads a resolver-backed list endpoint with a single unpaged GET' {
        # The endpoints that need every page read are exactly the ones a resolver exists for; take them from the
        # resolvers themselves so a new resolver extends this sweep without anyone editing it.
        $privatePath = Join-Path $PSScriptRoot '..' 'Private'
        $pagedEndpoints = @(Get-ChildItem -Path $privatePath -Filter 'Resolve-JIM*.ps1' |
            Select-String -Pattern 'Get-JIMPagedItems\s+-Endpoint\s+["'']([^"''?]+)["'']' |
            ForEach-Object { $_.Matches[0].Groups[1].Value } |
            Sort-Object -Unique)
        $pagedEndpoints.Count | Should -BeGreaterThan 0 -Because 'the sweep must find the resolvers it takes its endpoints from'

        $publicPath = Join-Path $PSScriptRoot '..' 'Public'
        $offenders = foreach ($file in Get-ChildItem -Path $publicPath -Filter '*.ps1' -Recurse) {
            $lines = Get-Content -Path $file.FullName
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $line = $lines[$i]
                $match = [regex]::Match($line, 'Invoke-JIMApi\s+-Endpoint\s+["'']([^"'']+)["'']')
                if (-not $match.Success -or $match.Groups[1].Value -notin $pagedEndpoints) { continue }
                if ($line -match '-Method\s+["'']?(POST|PUT|PATCH|DELETE)') { continue }
                # A deliberate exception carries a visible marker on the line above, so the reason travels with it.
                if ($i -gt 0 -and $lines[$i - 1] -match '#\s*paged-lookup:\s*exempt') { continue }
                "$($file.Name):$($i + 1): $($line.Trim())"
            }
        }

        $offenders | Should -BeNullOrEmpty -Because 'a list endpoint answers with one page of a paginated envelope; resolve names with Resolve-JIMMetaverseAttribute, Resolve-JIMConnectedSystem and the other resolvers, which read every page'
    }
}
