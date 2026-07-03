# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for log viewing cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMLogEntry' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMLogEntry
        }

        It 'Should have an Entries parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'Entries'
        }

        It 'Should have a Levels parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Levels'
        }

        It 'Should have a Services parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Services'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMLogEntry
        }

        It 'Should have Service as an optional string parameter' {
            $command.Parameters['Service'].ParameterType.Name | Should -Be 'String'
        }

        It 'Should have Level as a string array parameter' {
            $command.Parameters['Level'].ParameterType.Name | Should -Be 'String[]'
        }

        It 'Should have Limit parameter with ValidateRange' {
            $param = $command.Parameters['Limit']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Offset parameter with ValidateRange' {
            $param = $command.Parameters['Offset']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have ListLevels as a switch parameter' {
            $command.Parameters['ListLevels'].SwitchParameter | Should -BeTrue
        }

        It 'Should have ListServices as a switch parameter' {
            $command.Parameters['ListServices'].SwitchParameter | Should -BeTrue
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMLogEntry -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected with ListLevels' {
            { Get-JIMLogEntry -ListLevels -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected with ListServices' {
            { Get-JIMLogEntry -ListServices -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMLogEntry -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMLogFile' {

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMLogFile -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMLogFile -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Watch-JIMLog' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Watch-JIMLog
        }

        It 'Should have Service as an optional string parameter' {
            $command.Parameters['Service'].ParameterType.Name | Should -Be 'String'
        }

        It 'Should have Level as a string array parameter' {
            $command.Parameters['Level'].ParameterType.Name | Should -Be 'String[]'
        }

        It 'Should have Search as an optional string parameter' {
            $command.Parameters['Search'].ParameterType.Name | Should -Be 'String'
        }

        It 'Should have IntervalSeconds parameter with ValidateRange' {
            $param = $command.Parameters['IntervalSeconds']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have MaxPolls parameter with ValidateRange' {
            $param = $command.Parameters['MaxPolls']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Watch-JIMLog -MaxPolls 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Polling Behaviour' {

        It 'Should poll the log entries endpoint once per poll cycle' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }
                Mock Write-Host { }
                Mock Start-Sleep { }

                Watch-JIMLog -MaxPolls 2

                Should -Invoke Invoke-JIMApi -Times 2 -Exactly -ParameterFilter {
                    $Endpoint -like '/api/v1/logs?*'
                }
            }
        }

        It 'Should pass service, level, and search filters to the endpoint' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { @() }
                Mock Write-Host { }
                Mock Start-Sleep { }

                Watch-JIMLog -Service worker -Level Error, Fatal -Search 'timeout' -MaxPolls 1

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -like '*service=worker*' -and
                    $Endpoint -like '*levels=Error*' -and
                    $Endpoint -like '*levels=Fatal*' -and
                    $Endpoint -like '*search=timeout*'
                }
            }
        }

        It 'Should only display entries newer than the previous poll' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $existing = [PSCustomObject]@{
                    timestamp = [datetime]'2026-07-03T10:00:00Z'; level = 'Information'; levelShort = 'INF'
                    message = 'existing entry'; service = 'worker'; exception = $null
                }
                $new = [PSCustomObject]@{
                    timestamp = [datetime]'2026-07-03T10:00:05Z'; level = 'Information'; levelShort = 'INF'
                    message = 'new entry'; service = 'worker'; exception = $null
                }
                $script:pollCount = 0
                Mock Invoke-JIMApi {
                    $script:pollCount++
                    if ($script:pollCount -eq 1) { @($existing) } else { @($new, $existing) }
                }
                Mock Write-Host { }
                Mock Start-Sleep { }

                Watch-JIMLog -MaxPolls 2

                Should -Invoke Write-Host -Times 1 -Exactly -ParameterFilter { $Object -like '*new entry*' }
                Should -Invoke Write-Host -Times 0 -Exactly -ParameterFilter { $Object -like '*existing entry*' }
            }
        }

        It 'Should warn and keep polling when the API call fails' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { throw 'transient failure' }
                Mock Write-Host { }
                Mock Write-Warning { }
                Mock Start-Sleep { }

                { Watch-JIMLog -MaxPolls 2 } | Should -Not -Throw

                Should -Invoke Invoke-JIMApi -Times 2 -Exactly
                Should -Invoke Write-Warning -Times 2 -Exactly
            }
        }

        It 'Should colour Error entries red' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $errorEntry = [PSCustomObject]@{
                    timestamp = [datetime]'2026-07-03T10:00:05Z'; level = 'Error'; levelShort = 'ERR'
                    message = 'something failed'; service = 'worker'; exception = $null
                }
                $script:pollCount = 0
                Mock Invoke-JIMApi {
                    $script:pollCount++
                    if ($script:pollCount -eq 1) { @() } else { @($errorEntry) }
                }
                Mock Write-Host { }
                Mock Start-Sleep { }

                Watch-JIMLog -MaxPolls 2

                Should -Invoke Write-Host -Times 1 -Exactly -ParameterFilter {
                    $Object -like '*something failed*' -and $ForegroundColor -eq 'Red'
                }
            }
        }
    }

    Context 'Level Colour Mapping' {

        It 'Should map levels to the expected console colours' {
            InModuleScope JIM {
                Get-JIMLogLevelColour -Level 'Fatal' | Should -Be 'Red'
                Get-JIMLogLevelColour -Level 'Error' | Should -Be 'Red'
                Get-JIMLogLevelColour -Level 'Warning' | Should -Be 'Yellow'
                Get-JIMLogLevelColour -Level 'Information' | Should -BeNullOrEmpty
                Get-JIMLogLevelColour -Level 'Debug' | Should -Be 'DarkGray'
                Get-JIMLogLevelColour -Level 'Verbose' | Should -Be 'DarkGray'
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Watch-JIMLog -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}
