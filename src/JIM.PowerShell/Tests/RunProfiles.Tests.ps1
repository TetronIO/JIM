# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Run Profile cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMRunProfile' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMRunProfile
        }

        It 'Should have a mandatory ConnectedSystemId parameter' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id as an alias for ConnectedSystemId' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Aliases | Should -Contain 'Id'
        }

        It 'Should accept pipeline by property name' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a ConnectedSystemName parameter' {
            $command.Parameters['ConnectedSystemName'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMRunProfile -ConnectedSystemId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMRunProfile -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Start-JIMRunProfile' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Start-JIMRunProfile
        }

        It 'Should have a mandatory ConnectedSystemId parameter' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory RunProfileId parameter' {
            $param = $command.Parameters['RunProfileId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id as an alias for RunProfileId' {
            $param = $command.Parameters['RunProfileId']
            $param.Aliases | Should -Contain 'Id'
        }

        It 'Should have a Wait switch parameter' {
            $command.Parameters['Wait'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a Timeout parameter with validation' {
            $param = $command.Parameters['Timeout']
            $param | Should -Not -BeNullOrEmpty
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a ConnectedSystemName parameter' {
            $command.Parameters['ConnectedSystemName'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a RunProfileName parameter' {
            $command.Parameters['RunProfileName'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should write error when not connected' {
            { Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Start-JIMRunProfile -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have a description mentioning async execution' {
            $help.Description.Text | Should -Match 'async|queue'
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should have related links' {
            $help.RelatedLinks | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'New-JIMRunProfile' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command New-JIMRunProfile
        }

        It 'Should have a mandatory ConnectedSystemId parameter' {
            $param = $command.Parameters['ConnectedSystemId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory RunType parameter' {
            $param = $command.Parameters['RunType']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have RunType parameter with ValidateSet' {
            $param = $command.Parameters['RunType']
            $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'FullImport'
            $validateSet.ValidValues | Should -Contain 'DeltaImport'
            $validateSet.ValidValues | Should -Contain 'FullSynchronisation'
            $validateSet.ValidValues | Should -Contain 'DeltaSynchronisation'
            $validateSet.ValidValues | Should -Contain 'Export'
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have PageSize parameter with validation' {
            $param = $command.Parameters['PageSize']
            $param | Should -Not -BeNullOrEmpty
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a ConnectedSystemName parameter' {
            $command.Parameters['ConnectedSystemName'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { New-JIMRunProfile -ConnectedSystemId 1 -Name "Test" -RunType FullImport -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMRunProfile -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Set-JIMRunProfile' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMRunProfile
        }

        It 'Should have a mandatory ConnectedSystemId parameter in ById set' {
            $param = $command.Parameters['ConnectedSystemId']
            $paramAttr = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and
                $_.Mandatory -and
                $_.ParameterSetName -eq 'ById'
            }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory RunProfileId parameter' {
            $param = $command.Parameters['RunProfileId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have InputObject parameter that accepts pipeline input' {
            $param = $command.Parameters['InputObject']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a ConnectedSystemName parameter' {
            $command.Parameters['ConnectedSystemName'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Name "Test" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMRunProfile -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Remove-JIMRunProfile' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Remove-JIMRunProfile
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Force switch parameter' {
            $command.Parameters['Force'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should have InputObject parameter that accepts pipeline input' {
            $param = $command.Parameters['InputObject']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a ConnectedSystemName parameter' {
            $command.Parameters['ConnectedSystemName'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Remove-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMRunProfile -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Start-JIMRunProfile -Wait progress polling' {

    Context 'Wait behaviour' {

        It 'Polls the lightweight progress endpoint rather than the full Activity detail' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:waitPollCount = 0
                Mock Invoke-JIMApi {
                    if ($Endpoint -like '*/execute') {
                        return [PSCustomObject]@{
                            activityId = '11111111-1111-1111-1111-111111111111'
                            taskId = '22222222-2222-2222-2222-222222222222'
                        }
                    }
                    if ($Endpoint -like '*/progress') {
                        $script:waitPollCount++
                        $status = if ($script:waitPollCount -ge 2) { 'Complete' } else { 'InProgress' }
                        return [PSCustomObject]@{
                            status = $status
                            objectsProcessed = 5 * $script:waitPollCount
                            objectsToProcess = 10
                            percentComplete = 50 * $script:waitPollCount
                            estimatedSecondsRemaining = 4
                            objectsPerSecond = 2.5
                            message = 'Syncing'
                        }
                    }
                    return $null
                }

                # -Timeout bounds the red case: without the progress-endpoint implementation the
                # wait loop would otherwise poll forever against this mock.
                Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Wait -Timeout 30

                Should -Invoke Invoke-JIMApi -Times 2 -Exactly -ParameterFilter { $Endpoint -like '*/progress' }
                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter {
                    $Endpoint -like '*/activities/*' -and $Endpoint -notlike '*/progress'
                }
            }
        }
    }
}
