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

        It 'Should have a Wait switch parameter' {
            # -Wait became implementable once the execute endpoint started returning an Activity ID
            # to poll (issue #1112 follow-up); it previously shipped as an unimplemented warning and
            # was removed. The progress endpoint (issue #202) is what it polls.
            $command.Parameters['Wait'].SwitchParameter | Should -BeTrue
        }

        It 'Should have a Timeout parameter for use with Wait' {
            $command.Parameters['Timeout'] | Should -Not -BeNullOrEmpty
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

    Context 'PassThru output' {

        It 'Should surface the ActivityId and TaskId from the queue response' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    [PSCustomObject]@{
                        activityId = '11111111-1111-1111-1111-111111111111'
                        taskId = '22222222-2222-2222-2222-222222222222'
                        message = "Data Generation Template 'Test Users' has been queued for execution."
                    }
                }

                $result = Invoke-JIMExampleDataTemplate -Id 1 -PassThru -Confirm:$false

                $result | Should -Not -BeNullOrEmpty
                @($result.PSObject.Properties.Name | Sort-Object) | Should -Be @('ActivityId', 'Message', 'Status', 'TaskId', 'TemplateId')
                $result.ActivityId | Should -Be '11111111-1111-1111-1111-111111111111'
                $result.TaskId | Should -Be '22222222-2222-2222-2222-222222222222'
                $result.Status | Should -Be 'Queued'
                $result.TemplateId | Should -Be 1
            }
        }
    }

    Context 'Wait behaviour' {

        It 'Polls the lightweight progress endpoint until the Activity completes' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:templateWaitPollCount = 0
                Mock Invoke-JIMApi {
                    if ($Endpoint -like '*/execute') {
                        return [PSCustomObject]@{
                            activityId = '11111111-1111-1111-1111-111111111111'
                            taskId = '22222222-2222-2222-2222-222222222222'
                            message = 'Queued'
                        }
                    }
                    if ($Endpoint -like '*/progress') {
                        $script:templateWaitPollCount++
                        $status = if ($script:templateWaitPollCount -ge 2) { 'Complete' } else { 'InProgress' }
                        return [PSCustomObject]@{
                            status = $status
                            objectsProcessed = 5000 * $script:templateWaitPollCount
                            objectsToProcess = 10000
                            percentComplete = 50 * $script:templateWaitPollCount
                            estimatedSecondsRemaining = 8
                            objectsPerSecond = 625
                            message = 'Generating objects'
                        }
                    }
                    return $null
                }

                # -Timeout bounds the red case: without the -Wait implementation the call would
                # otherwise return immediately and the poll assertions below fail.
                Invoke-JIMExampleDataTemplate -Id 1 -Wait -Timeout 30 -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 2 -Exactly -ParameterFilter { $Endpoint -like '*/progress' }
                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter {
                    $Endpoint -like '*/activities/*' -and $Endpoint -notlike '*/progress'
                }
            }
        }

        It 'Reports the Activity''s final status with -PassThru once it has waited, not Queued' {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                Mock Invoke-JIMApi {
                    if ($Endpoint -like '*/execute') {
                        return [PSCustomObject]@{
                            activityId = '11111111-1111-1111-1111-111111111111'
                            taskId = '22222222-2222-2222-2222-222222222222'
                            message = 'Queued'
                        }
                    }
                    [PSCustomObject]@{ status = 'CompleteWithWarning'; objectsProcessed = 10; objectsToProcess = 10; message = '' }
                }

                $result = Invoke-JIMExampleDataTemplate -Id 1 -Wait -Timeout 30 -PassThru -Confirm:$false

                $result.Status | Should -Be 'CompleteWithWarning'
                $result.ActivityId | Should -Be '11111111-1111-1111-1111-111111111111'
            }
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

        It 'Should document the Wait parameter' {
            $help.Parameters.Parameter.Name | Should -Contain 'Wait'
        }
    }
}

Describe 'New-JIMExampleDataTemplate' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command New-JIMExampleDataTemplate
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory ObjectType hashtable array parameter' {
            $param = $command.Parameters['ObjectType']
            $param.ParameterType.Name | Should -Be 'Hashtable[]'
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have an optional ChangeReason parameter' {
            $param = $command.Parameters['ChangeReason']
            $param | Should -Not -BeNullOrEmpty
            ($param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }) | Should -BeNullOrEmpty
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

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null

                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        $script:capturedBody = $Body
                        return [PSCustomObject]@{ id = 99; name = $Body.name }
                    }
                    switch -Wildcard ($Endpoint) {
                        '/api/v1/metaverse/object-types*' {
                            # List endpoints return a paginated response; the resolvers read .items.
                            return [PSCustomObject]@{ items = @(
                                [PSCustomObject]@{ id = 1; name = 'User' },
                                [PSCustomObject]@{ id = 2; name = 'Group' }
                            ) }
                        }
                        '/api/v1/metaverse/attributes*' {
                            return [PSCustomObject]@{ items = @(
                                [PSCustomObject]@{ id = 10; name = 'Firstname' },
                                [PSCustomObject]@{ id = 11; name = 'Employee Type' },
                                [PSCustomObject]@{ id = 12; name = 'Lastname' }
                            ) }
                        }
                        '/api/v1/example-data/example-data-sets*' {
                            return [PSCustomObject]@{ items = @(
                                [PSCustomObject]@{ id = 3; name = 'Firstnames' },
                                [PSCustomObject]@{ id = 4; name = 'Surnames' }
                            ) }
                        }
                    }
                    return $null
                }
            }
        }

        It 'POSTs the template name to the templates endpoint' {
            InModuleScope JIM {
                New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{ MetaverseObjectType = 1 } -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates' -and $Method -eq 'POST'
                }
                $script:capturedBody.name | Should -Be 'Demo Users'
            }
        }

        It 'Resolves a Metaverse Object Type name to its id and defaults ObjectsToCreate to 1' {
            InModuleScope JIM {
                New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{ MetaverseObjectType = 'User' } -Confirm:$false

                $script:capturedBody.objectTypes.Count | Should -Be 1
                $script:capturedBody.objectTypes[0].metaverseObjectTypeId | Should -Be 1
                $script:capturedBody.objectTypes[0].objectsToCreate | Should -Be 1
            }
        }

        It 'Uses a Metaverse Object Type id without a lookup' {
            InModuleScope JIM {
                New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{ MetaverseObjectType = 2; ObjectsToCreate = 25 } -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter {
                    $Endpoint -like '/api/v1/metaverse/object-types*'
                }
                $script:capturedBody.objectTypes[0].metaverseObjectTypeId | Should -Be 2
                $script:capturedBody.objectTypes[0].objectsToCreate | Should -Be 25
            }
        }

        It 'Resolves attribute Metaverse Attribute and Example Data Set names, assigning data set order from array position' {
            InModuleScope JIM {
                New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{
                    MetaverseObjectType = 'User'
                    ObjectsToCreate     = 100
                    Attributes          = @(
                        @{ MetaverseAttribute = 'Firstname'; Pattern = '{0} {1}'; ExampleDataSets = @('Firstnames', 'Surnames') }
                    )
                } -Confirm:$false

                $attribute = $script:capturedBody.objectTypes[0].attributes[0]
                $attribute.metaverseAttributeId | Should -Be 10
                $attribute.pattern | Should -Be '{0} {1}'
                $attribute.exampleDataSets.Count | Should -Be 2
                $attribute.exampleDataSets[0].exampleDataSetId | Should -Be 3
                $attribute.exampleDataSets[0].order | Should -Be 0
                $attribute.exampleDataSets[1].exampleDataSetId | Should -Be 4
                $attribute.exampleDataSets[1].order | Should -Be 1
            }
        }

        It 'Throws on an unknown key in an -ObjectType hashtable' {
            InModuleScope JIM {
                {
                    New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{ MetaverseObjectType = 1; ObjectsToCraete = 5 } -Confirm:$false -ErrorAction Stop
                } | Should -Throw '*ObjectsToCraete*'
            }
        }

        It 'Throws on a ConnectedSystemObjectTypeAttributeId key in an attribute hashtable, which would generate nothing' {
            InModuleScope JIM {
                {
                    New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{
                        MetaverseObjectType = 1
                        Attributes          = @(@{ ConnectedSystemObjectTypeAttributeId = 55; Pattern = '{0}' })
                    } -Confirm:$false -ErrorAction Stop
                } | Should -Throw '*ConnectedSystemObjectTypeAttributeId*'
            }
        }

        It 'Throws on an unknown key in an attribute hashtable' {
            InModuleScope JIM {
                {
                    New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{
                        MetaverseObjectType = 1
                        Attributes          = @(@{ MetaverseAttribute = 10; Patern = '{0}' })
                    } -Confirm:$false -ErrorAction Stop
                } | Should -Throw '*Patern*'
            }
        }

        It 'Sends changeReason in the POST body when -ChangeReason is specified' {
            InModuleScope JIM {
                New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{ MetaverseObjectType = 1 } -ChangeReason 'Seeding demo data (CHG0200)' -Confirm:$false

                $script:capturedBody.changeReason | Should -Be 'Seeding demo data (CHG0200)'
            }
        }

        It 'Omits changeReason from the POST body when -ChangeReason is not specified' {
            InModuleScope JIM {
                New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{ MetaverseObjectType = 1 } -Confirm:$false

                $script:capturedBody.ContainsKey('changeReason') | Should -BeFalse
            }
        }

        It 'Sends nothing when -WhatIf is specified' {
            InModuleScope JIM {
                New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{ MetaverseObjectType = 'User' } -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{ MetaverseObjectType = 1 } -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMExampleDataTemplate -Full
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

Describe 'Set-JIMExampleDataTemplate' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Set-JIMExampleDataTemplate
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
            $command = Get-Command Set-JIMExampleDataTemplate
        }

        It 'Should have a mandatory Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.ParameterType.Name | Should -Be 'Int32'
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory Name parameter in the ByName set' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory -and $_.ParameterSetName -eq 'ByName' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have an ObjectType hashtable array parameter' {
            $command.Parameters['ObjectType'].ParameterType.Name | Should -Be 'Hashtable[]'
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

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null

                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') {
                        $script:capturedBody = $Body
                        return [PSCustomObject]@{ id = 7; name = 'Demo Users' }
                    }
                    switch -Wildcard ($Endpoint) {
                        '/api/v1/example-data/templates*' {
                            return [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 7; name = 'Demo Users' }) }
                        }
                        '/api/v1/metaverse/object-types*' {
                            return [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 1; name = 'User' }) }
                        }
                    }
                    return $null
                }
            }
        }

        It 'PUTs the new name to the template endpoint and omits objectTypes when -ObjectType is not supplied' {
            InModuleScope JIM {
                Set-JIMExampleDataTemplate -Id 7 -NewName 'Demo Users v2' -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates/7' -and $Method -eq 'PUT'
                }
                $script:capturedBody.name | Should -Be 'Demo Users v2'
                $script:capturedBody.ContainsKey('objectTypes') | Should -BeFalse
            }
        }

        It 'Replaces the whole Object Type graph when -ObjectType is supplied' {
            InModuleScope JIM {
                Set-JIMExampleDataTemplate -Id 7 -ObjectType @{ MetaverseObjectType = 'User'; ObjectsToCreate = 10 } -Confirm:$false

                $script:capturedBody.objectTypes.Count | Should -Be 1
                $script:capturedBody.objectTypes[0].metaverseObjectTypeId | Should -Be 1
                $script:capturedBody.objectTypes[0].objectsToCreate | Should -Be 10
                $script:capturedBody.ContainsKey('name') | Should -BeFalse
            }
        }

        It 'Resolves -Name to the template id' {
            InModuleScope JIM {
                Set-JIMExampleDataTemplate -Name 'Demo Users' -NewName 'Demo Users v2' -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates/7' -and $Method -eq 'PUT'
                }
            }
        }

        It 'Errors when no updatable parameter is supplied' {
            InModuleScope JIM {
                { Set-JIMExampleDataTemplate -Id 7 -Confirm:$false -ErrorAction Stop } | Should -Throw '*-NewName*'

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'PUT' }
            }
        }

        It 'Sends changeReason in the PUT body when -ChangeReason is specified' {
            InModuleScope JIM {
                Set-JIMExampleDataTemplate -Id 7 -NewName 'Demo Users v2' -ChangeReason 'Renaming for clarity (CHG0201)' -Confirm:$false

                $script:capturedBody.changeReason | Should -Be 'Renaming for clarity (CHG0201)'
            }
        }

        It 'Omits changeReason from the PUT body when -ChangeReason is not specified' {
            InModuleScope JIM {
                Set-JIMExampleDataTemplate -Id 7 -NewName 'Demo Users v2' -Confirm:$false

                $script:capturedBody.ContainsKey('changeReason') | Should -BeFalse
            }
        }

        It 'Sends nothing when -WhatIf is specified' {
            InModuleScope JIM {
                Set-JIMExampleDataTemplate -Id 7 -NewName 'Demo Users v2' -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMExampleDataTemplate -Id 7 -NewName 'Demo Users v2' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMExampleDataTemplate -Full
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

Describe 'Copy-JIMExampleDataTemplate' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Copy-JIMExampleDataTemplate
        }

        It 'Should have a ById parameter set as default' {
            $command.DefaultParameterSet | Should -Be 'ById'
        }

        It 'Should have a ByName parameter set' {
            $command.ParameterSets.Name | Should -Contain 'ByName'
        }

        It 'Should require NewName' {
            $param = $command.Parameters['NewName']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Request composition' {

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi {
                    if ($Method -eq 'POST') {
                        return [PSCustomObject]@{ id = 42; name = $Body.name; builtIn = $false; Warnings = @() }
                    }
                    switch -Wildcard ($Endpoint) {
                        '/api/v1/example-data/templates/*' {
                            return [PSCustomObject]@{ id = 7; name = 'Users & Groups' }
                        }
                        '/api/v1/example-data/templates*' {
                            return [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 7; name = 'Users & Groups' }) }
                        }
                    }
                    return $null
                }
            }
        }

        It 'POSTs the new name to the source template''s copy endpoint' {
            InModuleScope JIM {
                Copy-JIMExampleDataTemplate -Id 7 -NewName 'My Users'

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates/7/copy' -and $Method -eq 'POST' -and $Body.name -eq 'My Users' -and -not $Body.ContainsKey('changeReason')
                }
            }
        }

        It 'Sends changeReason in the body when specified' {
            InModuleScope JIM {
                Copy-JIMExampleDataTemplate -Id 7 -NewName 'My Users' -ChangeReason 'Customising (CHG0300)'

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'POST' -and $Body.changeReason -eq 'Customising (CHG0300)'
                }
            }
        }

        It 'Resolves -Name to the source template id' {
            InModuleScope JIM {
                Copy-JIMExampleDataTemplate -Name 'Users & Groups' -NewName 'My Users'

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates/7/copy' -and $Method -eq 'POST'
                }
            }
        }

        It 'Accepts a piped template and copies it' {
            InModuleScope JIM {
                [PSCustomObject]@{ id = 7; name = 'Users & Groups' } | Copy-JIMExampleDataTemplate -NewName 'My Users'

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates/7/copy' -and $Method -eq 'POST'
                }
            }
        }

        It 'Returns the new template only with -PassThru' {
            InModuleScope JIM {
                Copy-JIMExampleDataTemplate -Id 7 -NewName 'My Users' | Should -BeNullOrEmpty
                (Copy-JIMExampleDataTemplate -Id 7 -NewName 'My Users' -PassThru).id | Should -Be 42
            }
        }

        It 'Sends no POST when -WhatIf is specified' {
            InModuleScope JIM {
                Copy-JIMExampleDataTemplate -Id 7 -NewName 'My Users' -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'POST' }
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Copy-JIMExampleDataTemplate -Id 7 -NewName 'Copy' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Copy-JIMExampleDataTemplate -Full
        }

        It 'Should have a synopsis' {
            $help.Synopsis | Should -Not -BeNullOrEmpty
        }

        It 'Should have examples' {
            $help.Examples.Example.Count | Should -BeGreaterThan 0
        }
    }
}

Describe 'Remove-JIMExampleDataTemplate' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Remove-JIMExampleDataTemplate
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
            $command = Get-Command Remove-JIMExampleDataTemplate
        }

        It 'Should have a mandatory Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory Name parameter in the ByName set' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory -and $_.ParameterSetName -eq 'ByName' } | Should -Not -BeNullOrEmpty
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

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }

                Mock Invoke-JIMApi {
                    switch -Wildcard ($Endpoint) {
                        '/api/v1/example-data/templates/*' {
                            return [PSCustomObject]@{ id = 7; name = 'Demo Users' }
                        }
                        '/api/v1/example-data/templates*' {
                            return [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 7; name = 'Demo Users' }) }
                        }
                    }
                    return $null
                }
            }
        }

        It 'DELETEs the template endpoint' {
            InModuleScope JIM {
                Remove-JIMExampleDataTemplate -Id 7 -Force

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates/7' -and $Method -eq 'DELETE'
                }
            }
        }

        It 'Sends changeReason escaped as a query-string parameter on the DELETE' {
            InModuleScope JIM {
                Remove-JIMExampleDataTemplate -Id 7 -Force -ChangeReason 'Retiring demo template (CHG0202)'

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -eq '/api/v1/example-data/templates/7?changeReason=Retiring%20demo%20template%20%28CHG0202%29'
                }
            }
        }

        It 'Omits the changeReason query-string parameter on the DELETE when -ChangeReason is not specified' {
            InModuleScope JIM {
                Remove-JIMExampleDataTemplate -Id 7 -Force

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Method -eq 'DELETE' -and $Endpoint -notlike '*changeReason*'
                }
            }
        }

        It 'Resolves -Name to the template id before deleting' {
            InModuleScope JIM {
                Remove-JIMExampleDataTemplate -Name 'Demo Users' -Force

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates/7' -and $Method -eq 'DELETE'
                }
            }
        }

        It 'Sends no DELETE when -WhatIf is specified' {
            InModuleScope JIM {
                Remove-JIMExampleDataTemplate -Id 7 -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'DELETE' }
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Remove-JIMExampleDataTemplate -Id 7 -Force -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Remove-JIMExampleDataTemplate -Full
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

Describe 'Add-JIMExampleDataTemplateAttribute' {

    Context 'Parameter Sets' {

        BeforeAll {
            $command = Get-Command Add-JIMExampleDataTemplateAttribute
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
            $command = Get-Command Add-JIMExampleDataTemplateAttribute
        }

        It 'Should have a mandatory TemplateId parameter in the ById set' {
            $param = $command.Parameters['TemplateId']
            $param.ParameterType.Name | Should -Be 'Int32'
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory -and $_.ParameterSetName -eq 'ById' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory TemplateName parameter in the ByName set' {
            $param = $command.Parameters['TemplateName']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory -and $_.ParameterSetName -eq 'ByName' } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory ObjectType parameter' {
            $param = $command.Parameters['ObjectType']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have an optional ChangeReason parameter' {
            $param = $command.Parameters['ChangeReason']
            $param | Should -Not -BeNullOrEmpty
            ($param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }) | Should -BeNullOrEmpty
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

        BeforeEach {
            InModuleScope JIM {
                $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
                $script:capturedBody = $null

                $script:addAttrTemplate = [PSCustomObject]@{
                    id          = 7
                    name        = 'Demo Users'
                    builtIn     = $false
                    objectTypes = @(
                        [PSCustomObject]@{
                            id                      = 70
                            metaverseObjectTypeId   = 1
                            metaverseObjectTypeName = 'User'
                            objectsToCreate         = 100
                            templateAttributes      = @(
                                [PSCustomObject]@{
                                    id                                   = 700
                                    metaverseAttributeId                 = 10
                                    metaverseAttributeName               = 'Firstname'
                                    populatedValuesPercentage            = 90
                                    boolTrueDistribution                 = $null
                                    boolShouldBeRandom                   = $null
                                    minDate                              = $null
                                    maxDate                              = $null
                                    minNumber                            = $null
                                    maxNumber                            = $null
                                    sequentialNumbers                    = $null
                                    randomNumbers                        = $null
                                    pattern                              = '{0}'
                                    expression                           = $null
                                    exampleDataSetInstances              = @(
                                        [PSCustomObject]@{ id = 5000; exampleDataSetId = 3; exampleDataSetName = 'Firstnames'; order = 0 }
                                    )
                                    weightedStringValues                 = @(
                                        [PSCustomObject]@{ id = 6000; value = 'active'; weight = 0.8 }
                                    )
                                    managerDepthPercentage               = $null
                                    mvaRefMinAssignments                 = $null
                                    mvaRefMaxAssignments                 = $null
                                    referenceMetaverseObjectTypes        = @(
                                        [PSCustomObject]@{ id = 2; name = 'Group' }
                                    )
                                    attributeDependency                  = [PSCustomObject]@{
                                        id                     = 8000
                                        metaverseAttributeId   = 11
                                        metaverseAttributeName = 'Employee Type'
                                        comparisonType         = 'Equals'
                                        stringValue            = 'Employee'
                                    }
                                }
                            )
                        }
                    )
                }

                Mock Invoke-JIMApi {
                    if ($Method -eq 'PUT') {
                        $script:capturedBody = $Body
                        return $script:addAttrTemplate
                    }
                    switch -Wildcard ($Endpoint) {
                        '/api/v1/example-data/templates/*' { return $script:addAttrTemplate }
                        '/api/v1/example-data/templates*'  { return [PSCustomObject]@{ items = @($script:addAttrTemplate) } }
                        '/api/v1/metaverse/attributes*' {
                            return [PSCustomObject]@{ items = @(
                                [PSCustomObject]@{ id = 10; name = 'Firstname' },
                                [PSCustomObject]@{ id = 11; name = 'Employee Type' },
                                [PSCustomObject]@{ id = 12; name = 'Lastname' }
                            ) }
                        }
                        '/api/v1/example-data/example-data-sets*' {
                            return [PSCustomObject]@{ items = @(
                                [PSCustomObject]@{ id = 3; name = 'Firstnames' },
                                [PSCustomObject]@{ id = 4; name = 'Surnames' }
                            ) }
                        }
                    }
                    return $null
                }
            }
        }

        It 'Round-trips the existing graph in ids-only request shape and appends the new attribute' {
            InModuleScope JIM {
                Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 'Lastname' -Pattern '{0}' -ExampleDataSet 'Surnames' -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates/7' -and $Method -eq 'PUT'
                }

                $script:capturedBody.ContainsKey('name') | Should -BeFalse
                $script:capturedBody.objectTypes.Count | Should -Be 1

                $objectType = $script:capturedBody.objectTypes[0]
                $objectType.metaverseObjectTypeId | Should -Be 1
                $objectType.objectsToCreate | Should -Be 100
                $objectType.attributes.Count | Should -Be 2

                # The pre-existing attribute must round-trip unchanged, in ids-only shape.
                $existing = $objectType.attributes[0]
                $existing.metaverseAttributeId | Should -Be 10
                $existing.ContainsKey('metaverseAttributeName') | Should -BeFalse
                $existing.populatedValuesPercentage | Should -Be 90
                $existing.pattern | Should -Be '{0}'
                $existing.exampleDataSets.Count | Should -Be 1
                $existing.exampleDataSets[0].exampleDataSetId | Should -Be 3
                $existing.exampleDataSets[0].order | Should -Be 0
                $existing.exampleDataSets[0].ContainsKey('exampleDataSetName') | Should -BeFalse
                $existing.weightedStringValues.Count | Should -Be 1
                $existing.weightedStringValues[0].value | Should -Be 'active'
                $existing.weightedStringValues[0].weight | Should -Be 0.8
                @($existing.referenceMetaverseObjectTypeIds) | Should -Be @(2)
                $existing.attributeDependency.metaverseAttributeId | Should -Be 11
                $existing.attributeDependency.comparisonType | Should -BeExactly 'Equals'
                $existing.attributeDependency.stringValue | Should -Be 'Employee'
                $existing.attributeDependency.ContainsKey('metaverseAttributeName') | Should -BeFalse

                # The new attribute is appended with its names resolved to ids.
                $new = $objectType.attributes[1]
                $new.metaverseAttributeId | Should -Be 12
                $new.pattern | Should -Be '{0}'
                $new.exampleDataSets.Count | Should -Be 1
                $new.exampleDataSets[0].exampleDataSetId | Should -Be 4
                $new.exampleDataSets[0].order | Should -Be 0
            }
        }

        It 'Resolves the template by -TemplateName' {
            InModuleScope JIM {
                Add-JIMExampleDataTemplateAttribute -TemplateName 'Demo Users' -ObjectType 'User' -MetaverseAttribute 12 -Expression 'mv["Firstname"]' -Confirm:$false

                Should -Invoke Invoke-JIMApi -Times 1 -Exactly -ParameterFilter {
                    $Endpoint -eq '/api/v1/example-data/templates/7' -and $Method -eq 'PUT'
                }
                $script:capturedBody.objectTypes[0].attributes[1].expression | Should -Be 'mv["Firstname"]'
            }
        }

        It 'Locates the Object Type by Metaverse Object Type id' {
            InModuleScope JIM {
                Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 1 -MetaverseAttribute 12 -Pattern 'x' -Confirm:$false

                $script:capturedBody.objectTypes[0].attributes.Count | Should -Be 2
            }
        }

        It 'Sends the attribute dependency comparison type as an enum name' {
            InModuleScope JIM {
                Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 12 -Pattern 'x' -AttributeDependency @{ MetaverseAttribute = 'Employee Type'; ComparisonType = 'NotEquals'; StringValue = 'Contractor' } -Confirm:$false

                $dependency = $script:capturedBody.objectTypes[0].attributes[1].attributeDependency
                $dependency.metaverseAttributeId | Should -Be 11
                $dependency.comparisonType | Should -BeExactly 'NotEquals'
                $dependency.stringValue | Should -Be 'Contractor'
            }
        }

        It 'Sends a catalogue operator name for the attribute dependency' {
            InModuleScope JIM {
                Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 12 -Pattern 'x' -AttributeDependency @{ MetaverseAttribute = 11; ComparisonType = 'GreaterThanOrEquals'; StringValue = '30' } -Confirm:$false

                $script:capturedBody.objectTypes[0].attributes[1].attributeDependency.comparisonType | Should -BeExactly 'GreaterThanOrEquals'
            }
        }

        It 'Sends HasNoValue with no string value' {
            InModuleScope JIM {
                Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 12 -Pattern 'x' -AttributeDependency @{ MetaverseAttribute = 11; ComparisonType = 'HasNoValue' } -Confirm:$false

                $dependency = $script:capturedBody.objectTypes[0].attributes[1].attributeDependency
                $dependency.comparisonType | Should -BeExactly 'HasNoValue'
                $dependency.Contains('stringValue') | Should -BeFalse
            }
        }

        It 'Throws on a retired operator name, naming the one that replaced it' {
            InModuleScope JIM {
                {
                    Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 12 -Pattern 'x' -AttributeDependency @{ MetaverseAttribute = 11; ComparisonType = 'GreaterThanOrEqual'; StringValue = '30' } -Confirm:$false -ErrorAction Stop
                } | Should -Throw '*GreaterThanOrEquals*'
            }
        }

        It 'Throws on an invalid attribute dependency comparison type' {
            InModuleScope JIM {
                {
                    Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 12 -Pattern 'x' -AttributeDependency @{ MetaverseAttribute = 11; ComparisonType = 'Equalz'; StringValue = 'Employee' } -Confirm:$false -ErrorAction Stop
                } | Should -Throw '*Equalz*'
            }
        }

        It 'Has no -ConnectedSystemObjectTypeAttributeId parameter, since generation produces Metaverse Attribute values only' {
            (Get-Command Add-JIMExampleDataTemplateAttribute).Parameters.Keys | Should -Not -Contain 'ConnectedSystemObjectTypeAttributeId'
        }

        It 'Errors when -MetaverseAttribute is not supplied' {
            InModuleScope JIM {
                {
                    Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -Pattern 'x' -Confirm:$false -ErrorAction Stop
                } | Should -Throw '*-MetaverseAttribute*'

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'PUT' }
            }
        }

        It 'Errors when the Object Type is not in the template, listing the template''s Object Types' {
            InModuleScope JIM {
                {
                    Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'Locations' -MetaverseAttribute 12 -Pattern 'x' -Confirm:$false -ErrorAction Stop
                } | Should -Throw '*User*'

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly -ParameterFilter { $Method -eq 'PUT' }
            }
        }

        It 'Sends changeReason in the PUT body when -ChangeReason is specified' {
            InModuleScope JIM {
                Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 12 -Pattern 'x' -ChangeReason 'Adding surname generation (CHG0203)' -Confirm:$false

                $script:capturedBody.changeReason | Should -Be 'Adding surname generation (CHG0203)'
            }
        }

        It 'Omits changeReason from the PUT body when -ChangeReason is not specified' {
            InModuleScope JIM {
                Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 12 -Pattern 'x' -Confirm:$false

                $script:capturedBody.ContainsKey('changeReason') | Should -BeFalse
            }
        }

        It 'Sends nothing when -WhatIf is specified' {
            InModuleScope JIM {
                Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 12 -Pattern 'x' -WhatIf

                Should -Invoke Invoke-JIMApi -Times 0 -Exactly
            }
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 12 -Pattern 'x' -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Add-JIMExampleDataTemplateAttribute -Full
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

Describe 'Test-JIMExampleDataExpression' {
    It 'Posts the template attribute and samples to the template''s test-expression endpoint' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedEndpoint = $null
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedEndpoint = $Endpoint
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; outcome = 'Value'; value = 'ada.lovelace' }
            }

            $result = Test-JIMExampleDataExpression -TemplateId 4 -TemplateAttributeId 13 -Attributes @{ 'First Name' = 'Ada'; 'Last Name' = 'Lovelace' }

            $script:capturedEndpoint | Should -Be '/api/v1/example-data/templates/4/test-expression'
            $script:capturedBody.templateAttributeId | Should -Be 13
            $script:capturedBody.attributes.'First Name' | Should -Be 'Ada'
            $script:capturedBody.ContainsKey('expression') | Should -BeFalse
            $result.Value | Should -Be 'ada.lovelace'
        }
    }

    It 'Posts an expression to test in place of the saved one' {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:capturedBody = $null
            Mock Invoke-JIMApi {
                $script:capturedBody = $Body
                [PSCustomObject]@{ isValid = $true; outcome = 'Value'; value = 'ADA' }
            }

            Test-JIMExampleDataExpression -TemplateId 4 -TemplateAttributeId 13 -Expression 'Upper(mv["First Name"])' -Attributes @{ 'First Name' = 'Ada' } | Out-Null

            $script:capturedBody.expression | Should -Be 'Upper(mv["First Name"])'
        }
    }

    It 'Requires the template and the template attribute' {
        { Test-JIMExampleDataExpression -TemplateId 4 -ErrorAction Stop } | Should -Throw
    }
}

Describe 'Data Generation Template save warnings (#2055 Phase 3)' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
            $script:saveWarning = "The expression for 'Account Name' on 'User' reads mv[""Nickname""], which this template does not generate on 'User', so it never has a value."
            $script:warnedTemplate = [PSCustomObject]@{
                id          = 7
                name        = 'Demo Users'
                builtIn     = $false
                objectTypes = @(
                    [PSCustomObject]@{
                        id = 70; metaverseObjectTypeId = 1; metaverseObjectTypeName = 'User'; objectsToCreate = 10
                        templateAttributes = @()
                    }
                )
                warnings    = @($script:saveWarning)
            }

            Mock Invoke-JIMApi {
                if ($Method -in 'POST', 'PUT') { return $script:warnedTemplate }
                switch -Wildcard ($Endpoint) {
                    '/api/v1/example-data/templates/*' { return $script:warnedTemplate }
                    '/api/v1/metaverse/attributes*' {
                        return [PSCustomObject]@{ items = @([PSCustomObject]@{ id = 12; name = 'Account Name' }) }
                    }
                }
                return $null
            }
        }
    }

    It 'New-JIMExampleDataTemplate writes each save warning' {
        InModuleScope JIM {
            New-JIMExampleDataTemplate -Name 'Demo Users' -ObjectType @{ MetaverseObjectType = 1 } -Confirm:$false `
                -WarningVariable saveWarnings -WarningAction SilentlyContinue | Out-Null

            $saveWarnings | Should -HaveCount 1
            $saveWarnings[0].Message | Should -Be $script:saveWarning
        }
    }

    It 'Set-JIMExampleDataTemplate writes each save warning' {
        InModuleScope JIM {
            Set-JIMExampleDataTemplate -Id 7 -ObjectType @{ MetaverseObjectType = 1; ObjectsToCreate = 10 } -Confirm:$false `
                -WarningVariable saveWarnings -WarningAction SilentlyContinue | Out-Null

            $saveWarnings | Should -HaveCount 1
            $saveWarnings[0].Message | Should -Be $script:saveWarning
        }
    }

    It 'Add-JIMExampleDataTemplateAttribute writes each save warning' {
        InModuleScope JIM {
            Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType 'User' -MetaverseAttribute 'Account Name' -Expression 'Lower(mv["Nickname"])' `
                -Confirm:$false -WarningVariable saveWarnings -WarningAction SilentlyContinue | Out-Null

            $saveWarnings | Should -HaveCount 1
            $saveWarnings[0].Message | Should -Be $script:saveWarning
        }
    }
}
