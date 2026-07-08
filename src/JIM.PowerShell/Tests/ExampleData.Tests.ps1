# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Data Generation cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMExampleDataSet' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMExampleDataSet
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMExampleDataSet
        }

        It 'Should have Page parameter with validation' {
            $param = $command.Parameters['Page']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have PageSize parameter with validation' {
            $param = $command.Parameters['PageSize']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id as a mandatory int parameter in the ById set' {
            $param = $command.Parameters['Id']
            $param.ParameterType.Name | Should -Be 'Int32'
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMExampleDataSet -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected with Id' {
            { Get-JIMExampleDataSet -Id 5 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMExampleDataSet -Full
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

Describe 'New-JIMExampleDataSet' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command New-JIMExampleDataSet
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory Culture parameter' {
            $param = $command.Parameters['Culture']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Values as a string array parameter' {
            $command.Parameters['Values'].ParameterType.Name | Should -Be 'String[]'
        }

        It 'Should have PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Request body composition' {

        It 'Should have an optional ChangeReason parameter' {
            $command = Get-Command New-JIMExampleDataSet
            $param = $command.Parameters['ChangeReason']
            $param | Should -Not -BeNullOrEmpty
            ($param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }) | Should -BeNullOrEmpty
        }

        It 'Sends changeReason in the POST body when -ChangeReason is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMExampleDataSet -Name 'Test' -Culture 'en-GB' -ChangeReason 'Seeding test data (CHG0100)' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.changeReason -eq 'Seeding test data (CHG0100)'
                }
            }
        }

        It 'Omits changeReason from the POST body when -ChangeReason is not specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 1; name = 'Test' } }

                New-JIMExampleDataSet -Name 'Test' -Culture 'en-GB' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('changeReason')
                }
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { New-JIMExampleDataSet -Name "Test" -Culture "en-GB" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMExampleDataSet -Full
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

Describe 'Set-JIMExampleDataSet' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMExampleDataSet
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Request body composition' {

        It 'Should have an optional ChangeReason parameter' {
            $command = Get-Command Set-JIMExampleDataSet
            $param = $command.Parameters['ChangeReason']
            $param | Should -Not -BeNullOrEmpty
            ($param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }) | Should -BeNullOrEmpty
        }

        It 'Sends changeReason in the PUT body when -ChangeReason is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 5; name = 'Test' } }

                Set-JIMExampleDataSet -Id 5 -Name 'New Name' -ChangeReason 'Corrected city list (CHG0101)' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Body.changeReason -eq 'Corrected city list (CHG0101)'
                }
            }
        }

        It 'Omits changeReason from the PUT body when -ChangeReason is not specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 5; name = 'Test' } }

                Set-JIMExampleDataSet -Id 5 -Name 'New Name' -Confirm:$false | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    -not $Body.ContainsKey('changeReason')
                }
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMExampleDataSet -Id 5 -Name "New Name" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMExampleDataSet -Full
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

Describe 'Remove-JIMExampleDataSet' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Remove-JIMExampleDataSet
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Request composition' {

        It 'Should have an optional ChangeReason parameter' {
            $command = Get-Command Remove-JIMExampleDataSet
            $param = $command.Parameters['ChangeReason']
            $param | Should -Not -BeNullOrEmpty
            ($param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }) | Should -BeNullOrEmpty
        }

        It 'Sends changeReason as a query-string parameter on the DELETE when -ChangeReason is specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 5; name = 'Test' } }

                Remove-JIMExampleDataSet -Id 5 -Force -ChangeReason 'Retiring obsolete set (CHG0102)' | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -like '*changeReason=Retiring*'
                }
            }
        }

        It 'Omits the changeReason query-string parameter on the DELETE when -ChangeReason is not specified' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi { [PSCustomObject]@{ id = 5; name = 'Test' } }

                Remove-JIMExampleDataSet -Id 5 -Force | Out-Null

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -notlike '*changeReason*'
                }
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Remove-JIMExampleDataSet -Id 5 -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMExampleDataSet -Full
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

Describe 'Get-JIMExampleDataTemplate' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMExampleDataTemplate
        }

        It 'Should have a List parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'List'
        }

        It 'Should have a ById parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ById'
        }

        It 'Should have a ByName parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ByName'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMExampleDataTemplate
        }

        It 'Should have Id parameter' {
            $command.Parameters['Id'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have Name parameter' {
            $command.Parameters['Name'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have Name parameter with validation' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateNotNullOrEmptyAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMExampleDataTemplate -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMExampleDataTemplate -Full
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

Describe 'Invoke-JIMExampleDataTemplate' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Invoke-JIMExampleDataTemplate
        }

        It 'Should have a ById parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'ById'
        }

        It 'Should have a ByName parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ByName'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Invoke-JIMExampleDataTemplate
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Name parameter with validation' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateNotNullOrEmptyAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Wait switch parameter' {
            $command.Parameters['Wait'].SwitchParameter | Should -BeTrue
        }

        It 'Should have PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Invoke-JIMExampleDataTemplate -Id 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Invoke-JIMExampleDataTemplate -Full
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
