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
