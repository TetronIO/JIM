# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for System cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMHealth' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMHealth
        }

        It 'Should have a Health parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'Health'
        }

        It 'Should have a Ready parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Ready'
        }

        It 'Should have a Live parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Live'
        }
    }

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMHealth
        }

        It 'Should have an optional Url parameter' {
            $command.Parameters['Url'] | Should -Not -BeNullOrEmpty
        }

        It 'Url should not be mandatory' {
            $urlParam = $command.Parameters['Url']
            $mandatoryAttrs = $urlParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            $mandatoryAttrs | Should -BeNullOrEmpty
        }

        It 'Should have a Ready switch parameter' {
            $command.Parameters['Ready'].SwitchParameter | Should -BeTrue
        }

        It 'Ready should be mandatory in Ready parameter set' {
            $readyParam = $command.Parameters['Ready']
            $readyParamSet = $readyParam.ParameterSets['Ready']
            $readyParamSet.IsMandatory | Should -BeTrue
        }

        It 'Should have a Live switch parameter' {
            $command.Parameters['Live'].SwitchParameter | Should -BeTrue
        }

        It 'Live should be mandatory in Live parameter set' {
            $liveParam = $command.Parameters['Live']
            $liveParamSet = $liveParam.ParameterSets['Live']
            $liveParamSet.IsMandatory | Should -BeTrue
        }
    }

    Context 'Does Not Require Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should not throw connection error when Url is provided' {
            # Should fail with a network error, not a connection error
            $err = $null
            Get-JIMHealth -Url "http://localhost:1" -ErrorAction SilentlyContinue -ErrorVariable err
            $err | Should -Not -BeNullOrEmpty
            $err[0].Exception.Message | Should -Not -BeLike '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMHealth -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should document the Url parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Url' } | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMVersion' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMVersion
        }

        It 'Should have an optional Url parameter' {
            $command.Parameters['Url'] | Should -Not -BeNullOrEmpty
        }

        It 'Url should not be mandatory' {
            $urlParam = $command.Parameters['Url']
            $mandatoryAttrs = $urlParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            $mandatoryAttrs | Should -BeNullOrEmpty
        }

        It 'Should not have mandatory parameters' {
            $mandatoryParams = $command.Parameters.Values | Where-Object {
                $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            }
            $mandatoryParams | Should -BeNullOrEmpty
        }
    }

    Context 'Does Not Require Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should not throw connection error when Url is provided' {
            $err = $null
            Get-JIMVersion -Url "http://localhost:1" -ErrorAction SilentlyContinue -ErrorVariable err
            $err | Should -Not -BeNullOrEmpty
            $err[0].Exception.Message | Should -Not -BeLike '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMVersion -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should document the Url parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Url' } | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMAuthConfig' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMAuthConfig
        }

        It 'Should have an optional Url parameter' {
            $command.Parameters['Url'] | Should -Not -BeNullOrEmpty
        }

        It 'Url should not be mandatory' {
            $urlParam = $command.Parameters['Url']
            $mandatoryAttrs = $urlParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            $mandatoryAttrs | Should -BeNullOrEmpty
        }

        It 'Should not have mandatory parameters' {
            $mandatoryParams = $command.Parameters.Values | Where-Object {
                $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            }
            $mandatoryParams | Should -BeNullOrEmpty
        }
    }

    Context 'Does Not Require Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should not throw connection error when Url is provided' {
            $err = $null
            Get-JIMAuthConfig -Url "http://localhost:1" -ErrorAction SilentlyContinue -ErrorVariable err
            $err | Should -Not -BeNullOrEmpty
            $err[0].Exception.Message | Should -Not -BeLike '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMAuthConfig -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should document the Url parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Url' } | Should -Not -BeNullOrEmpty
        }
    }
}

Describe 'Get-JIMUserInfo' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMUserInfo
        }

        It 'Should not have mandatory parameters' {
            $mandatoryParams = $command.Parameters.Values | Where-Object {
                $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            }
            $mandatoryParams | Should -BeNullOrEmpty
        }

        It 'Should not have a Url parameter' {
            $command.Parameters.Keys | Should -Not -Contain 'Url'
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMUserInfo -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMUserInfo -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Get-JIMServiceHealth' {

    Context 'Module export' {

        It 'Is exported by the manifest' {
            (Get-Module JIM).ExportedFunctions.Keys | Should -Contain 'Get-JIMServiceHealth'
        }
    }

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Get-JIMServiceHealth
        }

        It 'Should default to the Services parameter set' {
            $command.DefaultParameterSet | Should -Be 'Services'
        }

        It 'Should have a Summary parameter set' {
            $command.ParameterSets.Name | Should -Contain 'Summary'
        }

        It 'Summary should be a switch, mandatory in its own set' {
            $command.Parameters['Summary'].SwitchParameter | Should -BeTrue
            $command.Parameters['Summary'].ParameterSets['Summary'].IsMandatory | Should -BeTrue
        }

        It 'Should not have a Url parameter, because the endpoint needs the Administrator role' {
            $command.Parameters.Keys | Should -Not -Contain 'Url'
        }

        It 'Should have no mandatory parameters in the default set' {
            $mandatory = $command.ParameterSets |
                Where-Object Name -eq 'Services' |
                ForEach-Object { $_.Parameters | Where-Object IsMandatory }
            $mandatory | Should -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Get-JIMServiceHealth -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Output shape' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedEndpoint = $null
                # The wire shape of GET /api/v1/system/health: camelCase, enums as names, one entry per service in
                # report order. Invoke-JIMApi is mocked below the normaliser, so the cmdlet must not rely on it to
                # produce PascalCase names.
                $script:healthWire = [PSCustomObject]@{
                    overall      = 'NotSeen'
                    webVersion   = '1.2.3'
                    generatedAt  = '2026-09-05T10:00:00Z'
                    services     = @(
                        [PSCustomObject]@{
                            service = 'WorkerSync'; state = 'Running'; reason = 'Last seen 2 seconds ago'
                            instanceId = 'host-a:1234'; hostName = 'host-a'; version = '1.2.3'
                            startedAt = '2026-09-05T09:00:00Z'; lastSeenAt = '2026-09-05T09:59:58Z'
                            currentWork = 'Full Import: Corporate Directory'; currentWorkStartedAt = '2026-09-05T09:50:00Z'
                            lastProgressAt = '2026-09-05T09:59:50Z'; detail = $null
                        },
                        [PSCustomObject]@{
                            service = 'WorkerPasswordDelivery'; state = 'Running'; reason = 'Last seen 2 seconds ago'
                            instanceId = 'host-a:1234'; hostName = 'host-a'; version = '1.2.3'
                            startedAt = '2026-09-05T09:00:00Z'; lastSeenAt = '2026-09-05T09:59:58Z'
                            currentWork = $null; currentWorkStartedAt = $null; lastProgressAt = $null; detail = 'queue: 0 due'
                        },
                        [PSCustomObject]@{
                            service = 'Scheduler'; state = 'NotSeen'; reason = 'Never reported'
                            instanceId = $null; hostName = $null; version = $null
                            startedAt = $null; lastSeenAt = $null
                            currentWork = $null; currentWorkStartedAt = $null; lastProgressAt = $null; detail = $null
                        }
                    )
                }
            }
        }

        It 'Calls GET /api/v1/system/health' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { $script:capturedEndpoint = $Endpoint; $script:healthWire }

                Get-JIMServiceHealth | Out-Null

                $script:capturedEndpoint | Should -Be '/api/v1/system/health'
            }
        }

        It 'Emits one JIM.ServiceHealth object per service, in report order' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { $script:healthWire }

                $services = @(Get-JIMServiceHealth)

                $services.Count | Should -Be 3
                $services[0].PSObject.TypeNames[0] | Should -Be 'JIM.ServiceHealth'
                $services.Service | Should -Be @('WorkerSync', 'WorkerPasswordDelivery', 'Scheduler')
                $services.State | Should -Be @('Running', 'Running', 'NotSeen')
            }
        }

        It 'Names every documented property, in the documented order' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { $script:healthWire }

                $worker = @(Get-JIMServiceHealth)[0]

                $worker.PSObject.Properties.Name | Should -Be @(
                    'Service', 'State', 'Reason', 'CurrentWork', 'CurrentWorkStartedAt', 'LastSeenAt', 'StartedAt',
                    'HostName', 'Version', 'InstanceId', 'LastProgressAt', 'Detail')
                $worker.Reason | Should -Be 'Last seen 2 seconds ago'
                $worker.CurrentWork | Should -Be 'Full Import: Corporate Directory'
                $worker.HostName | Should -Be 'host-a'
                $worker.Version | Should -Be '1.2.3'
                $worker.InstanceId | Should -Be 'host-a:1234'
                $worker.Detail | Should -BeNullOrEmpty
            }
        }

        It 'Keeps a never-seen service present with its fields null' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { $script:healthWire }

                $scheduler = @(Get-JIMServiceHealth) | Where-Object Service -eq 'Scheduler'

                $scheduler | Should -Not -BeNullOrEmpty
                $scheduler.State | Should -Be 'NotSeen'
                $scheduler.Reason | Should -Be 'Never reported'
                $scheduler.LastSeenAt | Should -BeNullOrEmpty
                $scheduler.HostName | Should -BeNullOrEmpty
            }
        }

        It 'With -Summary returns one object carrying Overall, WebVersion, GeneratedAt and Services' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { $script:healthWire }

                $summary = @(Get-JIMServiceHealth -Summary)

                $summary.Count | Should -Be 1
                $summary[0].PSObject.TypeNames[0] | Should -Be 'JIM.ServiceHealthSummary'
                $summary[0].PSObject.Properties.Name | Should -Be @('Overall', 'WebVersion', 'GeneratedAt', 'Services')
                $summary[0].Overall | Should -Be 'NotSeen'
                $summary[0].WebVersion | Should -Be '1.2.3'
                @($summary[0].Services).Count | Should -Be 3
                @($summary[0].Services)[0].PSObject.TypeNames[0] | Should -Be 'JIM.ServiceHealth'
            }
        }

        It 'Emits nothing when the API returns nothing' {
            InModuleScope JIM {
                Mock Invoke-JIMApi { }

                @(Get-JIMServiceHealth).Count | Should -Be 0
                @(Get-JIMServiceHealth -Summary).Count | Should -Be 0
            }
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Get-JIMServiceHealth -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }

        It 'Should include a monitoring example that fails when Overall is not Running' {
            $codes = @($help.Examples.Example | ForEach-Object { $_.Code })
            ($codes -match "Overall -ne 'Running'") | Should -Not -BeNullOrEmpty
        }

        It 'Should document the Summary parameter' {
            $help.Parameters.Parameter | Where-Object { $_.Name -eq 'Summary' } | Should -Not -BeNullOrEmpty
        }
    }
}
