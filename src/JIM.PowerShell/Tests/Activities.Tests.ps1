# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Activity cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMActivity' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMActivity
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }

        It 'Should have an ExecutionItems parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ExecutionItems'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMActivity
        }

        It 'Should have Id parameter of type guid' {
            $command.Parameters['Id'].ParameterType | Should -Be ([guid])
        }

        It 'Should have Page parameter with validation range' {
            $param = $command.Parameters['Page']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have PageSize parameter with validation range 1-100' {
            $param = $command.Parameters['PageSize']
            $validateRange = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
            $validateRange | Should -Not -BeNullOrEmpty
            $validateRange.MinRange | Should -Be 1
            $validateRange.MaxRange | Should -Be 100
        }

        It 'Should have ExecutionItems as a switch parameter' {
            $command.Parameters['ExecutionItems'].SwitchParameter | Should -BeTrue
        }

        It 'Should have Search parameter for filtering' {
            $command.Parameters['Search'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMActivity -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMActivity -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have a description' {
            $help.Description | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }

    Context 'Security audit events' {

        AfterEach {
            InModuleScope JIM { $script:JIMConnection = $null }
        }

        It 'Returns a failed authentication with its reason, client IP, attempt count and window' {
            # Read parity with the portal (#2108): a script, or a SIEM, has to tell a forged-token spray from one
            # expired token. Mocked below Invoke-JIMApi, so the module's own output normalisation runs.
            InModuleScope JIM {
                $script:JIMConnection = @{
                    Url        = 'https://jim.example.com'
                    AuthMethod = 'ApiKey'
                    ApiKey     = 'jim_ak_activities_test_key'
                    Connected  = $true
                }
                Mock Invoke-JIMApiRequest {
                    [PSCustomObject]@{
                        id                     = '0199d2a4-0000-7000-8000-000000000001'
                        targetType             = 'Authentication'
                        targetName             = 'Access token authentication failed'
                        securityEventReason    = 'Access token signature invalid'
                        clientIpAddress        = '203.0.113.7'
                        apiKeyPrefix           = $null
                        attemptCount           = 500
                        firstSeen              = '2026-10-11T20:16:01Z'
                        lastSeen               = '2026-10-11T20:29:42Z'
                        aggregationWindowStart = '2026-10-11T20:15:00Z'
                    }
                }

                $activity = Get-JIMActivity -Id ([guid]'0199d2a4-0000-7000-8000-000000000001')

                $activity.SecurityEventReason | Should -Be 'Access token signature invalid'
                $activity.ClientIpAddress | Should -Be '203.0.113.7'
                $activity.AttemptCount | Should -Be 500
                $activity.LastSeen | Should -Not -BeNullOrEmpty
                $activity.AggregationWindowStart | Should -Not -BeNullOrEmpty
                ($activity.PSObject.Properties.Name -ccontains 'SecurityEventReason') | Should -BeTrue
            }
        }
    }

    Context 'Filter parameters' {

        # The Activity query supports far more filtering than the cmdlet used to expose; these prove each
        # filter reaches the API as a query parameter rather than being silently dropped.

        It 'Sends no filter query parameters when none are supplied' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -notmatch 'operation=' -and
                    $Endpoint -notmatch 'outcome=' -and
                    $Endpoint -notmatch 'status=' -and
                    $Endpoint -notmatch 'initiatedById=' -and
                    $Endpoint -notmatch 'hasChildActivities=' -and
                    $Endpoint -notmatch 'createdFrom=' -and
                    $Endpoint -notmatch 'createdTo=' -and
                    $Endpoint -notmatch 'connectedSystem=' -and
                    $Endpoint -notmatch 'runProfile=' -and
                    $Endpoint -notmatch 'initiatedBy=' -and
                    $Endpoint -notmatch 'scheduledOnly=' -and
                    $Endpoint -notmatch 'scheduleId='
                }
            }
        }

        It 'Sends the Operation filter in the query string, one entry per value' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -Operation Execute, Clear | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'operation=Execute' -and $Endpoint -match 'operation=Clear'
                }
            }
        }

        It 'Sends the Outcome filter in the query string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -Outcome Errors | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -match 'outcome=Errors' }
            }
        }

        It 'Sends the Status filter in the query string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -Status FailedWithError | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -match 'status=FailedWithError' }
            }
        }

        It 'Sends the InitiatedById filter in the query string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -InitiatedById '3f2504e0-4f89-11d3-9a0c-0305e82c3301' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'initiatedById=3f2504e0-4f89-11d3-9a0c-0305e82c3301'
                }
            }
        }

        It 'Sends the InitiatedBy name filter in the query string, URL encoded' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -InitiatedBy 'Alice Adams' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'initiatedBy=Alice%20Adams'
                }
            }
        }

        It 'Sends HasChildActivities as false when explicitly set to false' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -HasChildActivities $false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -match 'hasChildActivities=false' }
            }
        }

        It 'Sends the created date range in the query string as round-trip UTC' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -CreatedFrom ([datetime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)) `
                    -CreatedTo ([datetime]::new(2026, 2, 1, 0, 0, 0, [DateTimeKind]::Utc)) | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'createdFrom=2026-01-01T00:00:00' -and $Endpoint -match 'createdTo=2026-02-01T00:00:00'
                }
            }
        }

        It 'Sends the ConnectedSystem filter in the query string, URL encoded, one entry per value' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -ConnectedSystem 'Contoso AD', 'Fabrikam HR' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'connectedSystem=Contoso%20AD' -and $Endpoint -match 'connectedSystem=Fabrikam%20HR'
                }
            }
        }

        It 'Sends the RunProfile filter in the query string, URL encoded' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -RunProfile 'Full Import' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -match 'runProfile=Full%20Import' }
            }
        }

        It 'Sends ScheduledOnly in the query string' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -ScheduledOnly $true | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -match 'scheduledOnly=true' }
            }
        }

        It 'Sends the ScheduleId filter in the query string, one entry per value' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -ScheduleId '3f2504e0-4f89-11d3-9a0c-0305e82c3301', '6ba7b810-9dad-11d1-80b4-00c04fd430c8' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'scheduleId=3f2504e0-4f89-11d3-9a0c-0305e82c3301' -and
                    $Endpoint -match 'scheduleId=6ba7b810-9dad-11d1-80b4-00c04fd430c8'
                }
            }
        }

        It 'Combines several filters in one request' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0 } }

                Get-JIMActivity -ConnectedSystem 'Contoso AD' -Status FailedWithError -ScheduledOnly $true | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -match 'connectedSystem=Contoso%20AD' -and
                    $Endpoint -match 'status=FailedWithError' -and
                    $Endpoint -match 'scheduledOnly=true'
                }
            }
        }

        It 'Rejects an unknown Status value at bind time' {
            { Get-JIMActivity -Status 'NotAStatus' } | Should -Throw
        }
    }
}

Describe 'Get-JIMActivityStats' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMActivityStats
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter of type guid' {
            $command.Parameters['Id'].ParameterType | Should -Be ([guid])
        }

        It 'Should have ActivityId as an alias for Id' {
            $param = $command.Parameters['Id']
            $param.Aliases | Should -Contain 'ActivityId'
        }

        It 'Should accept pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMActivityStats -Id ([guid]::NewGuid()) -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMActivityStats -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should mention Run Profile in description' {
            $help.Description.Text | Should -Match 'Run Profile'
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMActivityChildren' {

    # Get-JIMActivityChildren is a Public cmdlet, but is not currently listed in JIM.psd1's
    # FunctionsToExport, so it is not visible via a normal Import-Module of the manifest. Every
    # test here therefore runs inside InModuleScope, which executes within the module's own
    # session state and can see the function regardless of manifest export filtering.

    Context 'Parameter sets' {

        It 'Defaults to the Page parameter set' {
            InModuleScope JIM {
                (Get-Command Get-JIMActivityChildren).DefaultParameterSet | Should -Be 'Page'
            }
        }

        It 'Exposes the Page and All parameter sets' {
            InModuleScope JIM {
                $names = (Get-Command Get-JIMActivityChildren).ParameterSets.Name
                $names | Should -Contain 'Page'
                $names | Should -Contain 'All'
            }
        }

        It 'Rejects -Page and -All used together' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                { Get-JIMActivityChildren -Id ([guid]::NewGuid()) -Page 2 -All -ErrorAction Stop } | Should -Throw '*parameter set*'
            }
        }
    }

    Context 'Parameter validation' {

        It 'Requires Id and types it as a guid' {
            InModuleScope JIM {
                $id = (Get-Command Get-JIMActivityChildren).Parameters['Id']
                ($id.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }) | Should -Not -BeNullOrEmpty
                $id.ParameterType | Should -Be ([guid])
            }
        }

        It 'Accepts Id from the pipeline by property name' {
            InModuleScope JIM {
                $id = (Get-Command Get-JIMActivityChildren).Parameters['Id']
                ($id.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName }) | Should -Not -BeNullOrEmpty
            }
        }

        It 'Exposes All as a switch parameter' {
            InModuleScope JIM {
                (Get-Command Get-JIMActivityChildren).Parameters['All'].SwitchParameter | Should -BeTrue
            }
        }

        It 'Has a Page parameter with a validation range starting at 1' {
            InModuleScope JIM {
                $param = (Get-Command Get-JIMActivityChildren).Parameters['Page']
                $validateRange = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
                $validateRange | Should -Not -BeNullOrEmpty
                $validateRange.MinRange | Should -Be 1
            }
        }

        It 'Has a PageSize parameter with a validation range of 1-100' {
            InModuleScope JIM {
                $param = (Get-Command Get-JIMActivityChildren).Parameters['PageSize']
                $validateRange = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
                $validateRange | Should -Not -BeNullOrEmpty
                $validateRange.MinRange | Should -Be 1
                $validateRange.MaxRange | Should -Be 100
            }
        }

        It 'Defaults Page to 1 and PageSize to 50' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMActivityChildren -Id ([guid]::NewGuid()) | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*page=1*' -and $Endpoint -like '*pageSize=50*'
                }
            }
        }
    }

    Context 'Requires connection' {

        It 'Throws a connect-first error when not connected' {
            InModuleScope JIM {
                Disconnect-JIM
                { Get-JIMActivityChildren -Id ([guid]::NewGuid()) -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
            }
        }
    }

    Context 'Pagination behaviour' {

        It 'Emits each child activity from the response envelope, unwrapped' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $child1 = [PSCustomObject]@{ id = [guid]::NewGuid(); targetName = 'Step 1' }
                $child2 = [PSCustomObject]@{ id = [guid]::NewGuid(); targetName = 'Step 2' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @($child1, $child2); hasNextPage = $false } }

                $result = @(Get-JIMActivityChildren -Id ([guid]::NewGuid()))

                $result.Count | Should -Be 2
                $result[0].targetName | Should -Be 'Step 1'
                $result[1].targetName | Should -Be 'Step 2'
            }
        }

        It 'Requests a specific page and page size when provided' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMActivityChildren -Id ([guid]::NewGuid()) -Page 3 -PageSize 10 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*page=3*' -and $Endpoint -like '*pageSize=10*'
                }
            }
        }

        It '-All automatically pages through every child activity' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $pageOneChild = [PSCustomObject]@{ id = [guid]::NewGuid(); targetName = 'Page 1 child' }
                $pageTwoChild = [PSCustomObject]@{ id = [guid]::NewGuid(); targetName = 'Page 2 child' }
                $script:pollCount = 0
                Mock Invoke-JIMApi {
                    $script:pollCount++
                    if ($script:pollCount -eq 1) {
                        [PSCustomObject]@{ items = @($pageOneChild); hasNextPage = $true }
                    }
                    else {
                        [PSCustomObject]@{ items = @($pageTwoChild); hasNextPage = $false }
                    }
                }

                $result = @(Get-JIMActivityChildren -Id ([guid]::NewGuid()) -All)

                $result.Count | Should -Be 2
                $result[0].targetName | Should -Be 'Page 1 child'
                $result[1].targetName | Should -Be 'Page 2 child'
                Should -Invoke Invoke-JIMApi -Times 2 -Exactly
            }
        }

        It '-All stops looping once hasNextPage is false' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); hasNextPage = $false } }

                Get-JIMActivityChildren -Id ([guid]::NewGuid()) -All | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly
            }
        }
    }

    Context 'Help documentation' {

        It 'Has a synopsis' {
            InModuleScope JIM {
                (Get-Help Get-JIMActivityChildren -Full).Synopsis | Should -Not -BeNullOrEmpty
            }
        }

        It 'Has examples' {
            InModuleScope JIM {
                (Get-Help Get-JIMActivityChildren -Full).Examples.Example.Count | Should -BeGreaterThan 0
            }
        }

        It 'Documents that the response envelope is unwrapped so pipeline output is unaffected' {
            InModuleScope JIM {
                $help = Get-Help Get-JIMActivityChildren -Full
                ($help.Description.Text -join ' ') | Should -Match 'unwrap'
            }
        }
    }
}

Describe 'Get-JIMActivity -Follow' {

    Context 'Parameter set' {

        BeforeAll {
            $command = Get-Command Get-JIMActivity
        }

        It 'Should have a Follow parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Follow'
        }

        It 'Should require Id and Follow in the Follow parameter set' {
            $followSet = $command.ParameterSets | Where-Object Name -eq 'Follow'
            ($followSet.Parameters | Where-Object Name -eq 'Id').IsMandatory | Should -BeTrue
            ($followSet.Parameters | Where-Object Name -eq 'Follow').IsMandatory | Should -BeTrue
        }

        It 'Should validate IntervalSeconds between 1 and 300' {
            $attribute = $command.Parameters['IntervalSeconds'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
            $attribute.MinRange | Should -Be 1
            $attribute.MaxRange | Should -Be 300
        }

        It 'Should have a MaxPolls parameter for bounded following' {
            $command.Parameters.Keys | Should -Contain 'MaxPolls'
        }
    }

    Context 'Follow behaviour' {

        It 'Polls the progress endpoint until the Activity completes, then emits the final Activity' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:followPollCount = 0
                Mock Invoke-JIMApi {
                    if ($Endpoint -like '*/progress') {
                        $script:followPollCount++
                        $status = if ($script:followPollCount -ge 3) { 'Complete' } else { 'InProgress' }
                        return [PSCustomObject]@{
                            status = $status
                            objectsProcessed = 100 * $script:followPollCount
                            objectsToProcess = 300
                            percentComplete = [Math]::Min(100, 33 * $script:followPollCount)
                            estimatedSecondsRemaining = 10
                            objectsPerSecond = 50
                            message = 'Importing objects from Connected System'
                        }
                    }
                    return [PSCustomObject]@{ id = 'final'; status = 'Complete' }
                }

                $result = Get-JIMActivity -Id ([guid]::NewGuid()) -Follow -IntervalSeconds 1

                Should -Invoke Invoke-JIMApi -Times 3 -Exactly -ParameterFilter { $Endpoint -like '*/progress' }
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*/activities/*' -and $Endpoint -notlike '*/progress'
                }
                $result.status | Should -Be 'Complete'
            }
        }

        It 'Stops after MaxPolls when the Activity does not complete' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Endpoint -like '*/progress') {
                        return [PSCustomObject]@{ status = 'InProgress'; objectsProcessed = 1; objectsToProcess = 10 }
                    }
                    return [PSCustomObject]@{ id = 'final'; status = 'InProgress' }
                }

                Get-JIMActivity -Id ([guid]::NewGuid()) -Follow -IntervalSeconds 1 -MaxPolls 2 | Out-Null

                Should -Invoke Invoke-JIMApi -Times 2 -Exactly -ParameterFilter { $Endpoint -like '*/progress' }
            }
        }

        It 'Keeps polling through transient errors rather than aborting' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:flakyPollCount = 0
                Mock Invoke-JIMApi {
                    if ($Endpoint -like '*/progress') {
                        $script:flakyPollCount++
                        if ($script:flakyPollCount -eq 1) {
                            throw 'Transient failure'
                        }
                        return [PSCustomObject]@{ status = 'Complete'; objectsProcessed = 10; objectsToProcess = 10 }
                    }
                    return [PSCustomObject]@{ id = 'final'; status = 'Complete' }
                }

                { Get-JIMActivity -Id ([guid]::NewGuid()) -Follow -IntervalSeconds 1 -WarningAction SilentlyContinue | Out-Null } |
                    Should -Not -Throw

                Should -Invoke Invoke-JIMApi -Times 2 -Exactly -ParameterFilter { $Endpoint -like '*/progress' }
            }
        }
    }
}

Describe 'Get-JIMActivityExecutionItem' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMActivityExecutionItem
        }

        It 'Should have a mandatory Id parameter of type guid' {
            $param = $command.Parameters['Id']
            $param.ParameterType | Should -Be ([guid])
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should not alias Id as ActivityId (an item id is not an Activity id)' {
            $command.Parameters['Id'].Aliases | Should -Not -Contain 'ActivityId'
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMActivityExecutionItem -Id ([guid]::NewGuid()) -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Request' {

        It 'Requests the item from the execution item detail endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = $itemId; errorMessage = 'No free value was found.' } }

                $result = Get-JIMActivityExecutionItem -Id $itemId

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/activities/items/$itemId" }
                $result.errorMessage | Should -Be 'No free value was found.'
            }
        }

        It 'Fetches each item piped in from the execution item list, by its Id' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ errorMessage = 'x' } }
                $first = [guid]::NewGuid()
                $second = [guid]::NewGuid()
                # The shape Get-JIMActivity -ExecutionItems returns: headers carrying Id and ErrorType, no message.
                $headers = @(
                    [PSCustomObject]@{ Id = $first; ErrorType = 'GeneratedValueExhausted'; DisplayName = 'Osric Tamworth' }
                    [PSCustomObject]@{ Id = $second; ErrorType = 'UnhandledError'; DisplayName = 'Nerys Vandal' }
                )

                $headers | Get-JIMActivityExecutionItem | Out-Null

                Should -Invoke Invoke-JIMApi -Times 2 -Exactly
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/activities/items/$first" }
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/activities/items/$second" }
            }
        }

        It 'Returns an ambiguous match''s candidates with what each is joined to' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                $candidateId = [guid]::NewGuid()
                # The wire shape: camelCase, candidates nested in the item, joined objects nested in each candidate.
                $wire = @{
                    id = $itemId
                    errorType = 'AmbiguousMatch'
                    matchCandidates = @(
                        @{
                            candidateKind = 'MetaverseObject'; candidateId = $candidateId; displayNameSnapshot = 'Ann Bloggs'
                            matchedValue = '48213'; candidateTotal = 2; exists = $true
                            joinedObjects = @(@{ connectedSystemName = 'Corporate Directory'; externalId = 'abloggs' })
                        }
                    )
                } | ConvertTo-Json -Depth 5
                Mock Invoke-RestMethod { $wire | ConvertFrom-Json }

                $result = Get-JIMActivityExecutionItem -Id $itemId

                $candidate = @($result.MatchCandidates)[0]
                $candidate.PSObject.Properties.Name | Should -Contain 'DisplayNameSnapshot'
                $candidate.CandidateId | Should -Be $candidateId
                $candidate.Exists | Should -BeTrue
                @($candidate.JoinedObjects)[0].PSObject.Properties.Name | Should -Contain 'ConnectedSystemName'
                @($candidate.JoinedObjects)[0].ExternalId | Should -Be 'abloggs'
            }
        }
    }

    Context 'Causes and consequences' {

        It 'Has the item as its default parameter set, with Causes and Consequences beside it' {
            $command = Get-Command Get-JIMActivityExecutionItem
            $command.DefaultParameterSet | Should -Be 'Item'
            $command.ParameterSets.Name | Should -Contain 'Causes'
            $command.ParameterSets.Name | Should -Contain 'Consequences'
        }

        It 'Requests the causes and flattens nested cohorts into one row per cohort, by level' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        runProfileExecutionItemId = $itemId
                        isTruncatedByDepth = $false
                        cohorts = @(
                            [PSCustomObject]@{
                                edgeType = 'MetaverseObjectDeletionCausedReferenceRemoval'; reasonCode = 'AuthoritativeSourceDisconnected'
                                connectedSystemName = 'Yellowstone APAC'; objectTypeName = 'User'; objectTypePluralName = 'Users'
                                attributeName = 'Static Members'; memberCount = 1
                                members = @(
                                    [PSCustomObject]@{
                                        displayName = 'Tina Adams'; resolution = 'Resolved'
                                        causes = @(
                                            [PSCustomObject]@{
                                                sourceImportChangeType = 'Deleted'; connectedSystemName = 'Yellowstone APAC'; memberCount = 1
                                                members = @([PSCustomObject]@{ displayName = 'Tina Adams'; resolution = 'NoFurtherCauses'; causes = @() })
                                            }
                                        )
                                    }
                                )
                            }
                        )
                    }
                }

                $rows = @(Get-JIMActivityExecutionItem -Id $itemId -Causes)

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/activities/items/$itemId/causes" }
                $rows.Count | Should -Be 2
                $rows[0].Level | Should -Be 1
                $rows[0].EdgeType | Should -Be 'MetaverseObjectDeletionCausedReferenceRemoval'
                $rows[0].Count | Should -Be 1
                $rows[0].ObjectType | Should -Be 'User'
                $rows[0].Attribute | Should -Be 'Static Members'
                $rows[0].Members | Should -Be @('Tina Adams')
                $rows[1].Level | Should -Be 2
                $rows[1].SourceImportChangeType | Should -Be 'Deleted'
                $rows[1].Resolution | Should -Be 'NoFurtherCauses'
                $rows[1].ExecutionItemId | Should -Be $itemId
            }
        }

        It 'Requests the consequences and flattens them with each branch''s ending and cohort key' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        runProfileExecutionItemId = $itemId
                        hasWaitingBranches = $true
                        endings = @()
                        cohorts = @(
                            [PSCustomObject]@{
                                cohortKey = 'key-1'; hopKind = 'Edge'; edgeType = 'MetaverseObjectDeletionCausedReferenceRemoval'
                                objectTypeName = 'Group'; objectTypePluralName = 'Groups'; attributeName = 'Static Members'
                                memberCount = 4218; members = @([PSCustomObject]@{ displayName = 'Project Pulse' }); ending = $null
                                endings = @([PSCustomObject]@{ ending = 'NoFurtherConsequences'; memberCount = 2 })
                                isFurtherThanShown = $false
                                consequences = @(
                                    [PSCustomObject]@{
                                        cohortKey = 'key-2'; hopKind = 'OutstandingPendingExport'; pendingExportStatus = 'Pending'
                                        connectedSystemName = 'Glitterband EMEA'; memberCount = 4216; members = @(); ending = 'WaitingForExport'
                                        endings = @(); consequences = @(); isFurtherThanShown = $false
                                    }
                                )
                            }
                        )
                    }
                }

                $rows = @(Get-JIMActivityExecutionItem -Id $itemId -Consequences)

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/activities/items/$itemId/consequences" }
                $rows.Count | Should -Be 2
                $rows[0].Level | Should -Be 1
                $rows[0].Count | Should -Be 4218
                $rows[0].ObjectType | Should -Be 'Groups'
                $rows[0].Ending | Should -Be 'NoFurtherConsequences (2)'
                $rows[0].CohortKey | Should -Be 'key-1'
                $rows[0].ExecutionItemId | Should -Be $itemId
                $rows[1].Level | Should -Be 2
                $rows[1].HopKind | Should -Be 'OutstandingPendingExport'
                $rows[1].ConnectedSystem | Should -Be 'Glitterband EMEA'
                $rows[1].Ending | Should -Be 'WaitingForExport'
            }
        }

        It 'Returns the item''s own ending as a row when it caused nothing' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        runProfileExecutionItemId = $itemId; cohorts = @()
                        endings = @([PSCustomObject]@{ ending = 'WaitingForSynchronisation'; memberCount = 1; connectedSystemName = 'Yellowstone APAC' })
                    }
                }

                $rows = @(Get-JIMActivityExecutionItem -Id $itemId -Consequences)

                $rows.Count | Should -Be 1
                $rows[0].Level | Should -Be 0
                $rows[0].Ending | Should -Be 'WaitingForSynchronisation'
                $rows[0].ConnectedSystem | Should -Be 'Yellowstone APAC'
            }
        }
    }

    Context 'Causal timeline' {

        It 'Keeps the item as its default parameter set, with the timeline sets beside it' {
            $command = Get-Command Get-JIMActivityExecutionItem
            $command.DefaultParameterSet | Should -Be 'Item'
            foreach ($set in 'ConnectedSystemObjectTimeline', 'ConnectedSystemObjectTimelineAll', 'MetaverseObjectTimeline', 'MetaverseObjectTimelineAll') {
                $command.ParameterSets.Name | Should -Contain $set
            }
        }

        It 'Requests a page of a Connected System Object''s timeline and returns its rows' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $csoId = [guid]::NewGuid()
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        object = [PSCustomObject]@{ kind = 'ConnectedSystemObject'; id = $csoId; isDeleted = $false }
                        items = @([PSCustomObject]@{ id = $itemId; because = 'Tina Adams was deleted' })
                        totalCount = 1; page = 2; pageSize = 10; hasNextPage = $false
                        metaverseObjectChangesNotRecorded = $false
                    }
                }

                $rows = @(Get-JIMActivityExecutionItem -ConnectedSystemId 3 -ConnectedSystemObjectId $csoId -Page 2 -PageSize 10)

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq "/api/v1/synchronisation/connected-systems/3/connector-space/$csoId/causal-timeline?page=2&pageSize=10"
                }
                $rows.Count | Should -Be 1
                $rows[0].id | Should -Be $itemId
                $rows[0].because | Should -Be 'Tina Adams was deleted'
            }
        }

        It 'Pages through a whole Metaverse Object timeline with -All' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $mvoId = [guid]::NewGuid()
                Mock Invoke-JIMApi {
                    $page = if ($Endpoint -match 'page=(\d+)') { [int]$Matches[1] } else { 0 }
                    [PSCustomObject]@{
                        items = @([PSCustomObject]@{ id = [guid]::NewGuid(); page = $page })
                        totalCount = 2; page = $page; pageSize = 1; totalPages = 2; hasNextPage = ($page -lt 2)
                        metaverseObjectChangesNotRecorded = $false
                    }
                }

                $rows = @(Get-JIMActivityExecutionItem -MetaverseObjectId $mvoId -All -PageSize 1)

                $rows.Count | Should -Be 2
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/metaverse/objects/$mvoId/causal-timeline?page=1&pageSize=1" }
                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/metaverse/objects/$mvoId/causal-timeline?page=2&pageSize=1" }
            }
        }

        It 'Warns once that a Metaverse Object timeline is incomplete where its changes are not recorded' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    $page = if ($Endpoint -match 'page=(\d+)') { [int]$Matches[1] } else { 0 }
                    [PSCustomObject]@{
                        items = @([PSCustomObject]@{ id = [guid]::NewGuid() })
                        totalCount = 2; page = $page; pageSize = 1; totalPages = 2; hasNextPage = ($page -lt 2)
                        metaverseObjectChangesNotRecorded = $true
                    }
                }

                $null = Get-JIMActivityExecutionItem -MetaverseObjectId ([guid]::NewGuid()) -All -PageSize 1 -WarningVariable warnings -WarningAction SilentlyContinue

                @($warnings).Count | Should -Be 1
                "$($warnings[0])" | Should -BeLike '*Metaverse Object changes*'
            }
        }

        It 'Pipes each timeline row into -Causes by the row''s own execution item id' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi { [PSCustomObject]@{ runProfileExecutionItemId = $itemId; cohorts = @() } }
                # The row shape: the item's id beside the ids of the objects it was about.
                $row = [PSCustomObject]@{
                    Id = $itemId; ConnectedSystemId = 3; ConnectedSystemObjectId = [guid]::NewGuid(); MetaverseObjectId = [guid]::NewGuid()
                }

                $row | Get-JIMActivityExecutionItem -Causes | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/activities/items/$itemId/causes" }
            }
        }

        It 'Fetches the item for a piped timeline row given no switch, not a timeline' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = $itemId } }
                $row = [PSCustomObject]@{
                    Id = $itemId; ConnectedSystemId = 3; ConnectedSystemObjectId = [guid]::NewGuid(); MetaverseObjectId = [guid]::NewGuid()
                }

                $row | Get-JIMActivityExecutionItem | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter { $Endpoint -eq "/api/v1/activities/items/$itemId" }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMActivityExecutionItem -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Get-JIMCausalCohortMember' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMCausalCohortMember
        }

        It 'Takes the execution item and cohort key from the pipeline by property name' {
            $command.Parameters['ExecutionItemId'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } |
                Should -Not -BeNullOrEmpty
            $command.Parameters['CohortKey'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } |
                Should -Not -BeNullOrEmpty
        }

        It 'Has PageSize limited to 1-100' {
            $validateRange = $command.Parameters['PageSize'].Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
            $validateRange.MinRange | Should -Be 1
            $validateRange.MaxRange | Should -Be 100
        }
    }

    Context 'Request' {

        It 'Requests one page of the cohort''s members, with the search text' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @([PSCustomObject]@{ displayName = 'Project Pulse' }); totalCount = 1; hasNextPage = $false } }

                $members = @(Get-JIMCausalCohortMember -ExecutionItemId $itemId -CohortKey 'abc_-1' -Page 2 -PageSize 10 -Search 'project pulse')

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq "/api/v1/activities/items/$itemId/consequences/cohorts/abc_-1/members?page=2&pageSize=10&search=project%20pulse"
                }
                $members[0].displayName | Should -Be 'Project Pulse'
            }
        }

        It 'Fetches every page with -All' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @([PSCustomObject]@{ displayName = 'Project Pulse' }); totalCount = 1; totalPages = 1; hasNextPage = $false } }

                $members = @(Get-JIMCausalCohortMember -ExecutionItemId $itemId -CohortKey 'abc' -All)

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq "/api/v1/activities/items/$itemId/consequences/cohorts/abc/members?page=1&pageSize=100"
                }
                $members.Count | Should -Be 1
            }
        }

        It 'Pages the cohort of a consequence row piped in from Get-JIMActivityExecutionItem -Consequences' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $itemId = [guid]::NewGuid()
                Mock Invoke-JIMApi { [PSCustomObject]@{ items = @(); totalCount = 0; hasNextPage = $false } }
                $row = [PSCustomObject]@{ ExecutionItemId = $itemId; Level = 1; CohortKey = 'key-1'; Count = 4218 }

                $row | Get-JIMCausalCohortMember | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like "/api/v1/activities/items/$itemId/consequences/cohorts/key-1/members?page=1&pageSize=*"
                }
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMCausalCohortMember -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}
