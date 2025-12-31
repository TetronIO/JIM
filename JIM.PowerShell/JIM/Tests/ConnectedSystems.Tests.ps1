#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Connected System cmdlets.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'New-JIMConnectedSystem' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command New-JIMConnectedSystem
        }

        It 'Should have a mandatory Name parameter' {
            $param = $command.Parameters['Name']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory ConnectorDefinitionId parameter' {
            $param = $command.Parameters['ConnectorDefinitionId']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Description parameter' {
            $command.Parameters['Description'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
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
            { New-JIMConnectedSystem -Name "Test" -ConnectorDefinitionId 1 -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help New-JIMConnectedSystem -Full
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

Describe 'Set-JIMConnectedSystem' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMConnectedSystem
        }

        It 'Should have a mandatory Id parameter in ById set' {
            $param = $command.Parameters['Id']
            $paramAttr = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and
                $_.Mandatory -and
                $_.ParameterSetName -eq 'ById'
            }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It 'Should have Id parameter that accepts pipeline by property name' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipelineByPropertyName } | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Name parameter' {
            $command.Parameters['Name'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a Description parameter' {
            $command.Parameters['Description'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a SettingValues parameter' {
            $command.Parameters['SettingValues'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have InputObject parameter that accepts pipeline input' {
            $param = $command.Parameters['InputObject']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected' {
            { Set-JIMConnectedSystem -Id 1 -Name "Test" -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMConnectedSystem -Full
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

Describe 'Set-JIMConnectedSystemAttribute' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMConnectedSystemAttribute
        }

        It 'Should have mandatory ConnectedSystemId parameter for both parameter sets' {
            $param = $command.Parameters['ConnectedSystemId']
            $mandatoryAttrs = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory
            }
            $mandatoryAttrs | Should -Not -BeNullOrEmpty
        }

        It 'Should have mandatory ObjectTypeId parameter for both parameter sets' {
            $param = $command.Parameters['ObjectTypeId']
            $mandatoryAttrs = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory
            }
            $mandatoryAttrs | Should -Not -BeNullOrEmpty
        }

        It 'Should have AttributeId parameter for Single parameter set' {
            $param = $command.Parameters['AttributeId']
            $paramAttr = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and
                $_.ParameterSetName -eq 'Single'
            }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It 'Should have AttributeUpdates parameter for Bulk parameter set' {
            $param = $command.Parameters['AttributeUpdates']
            $paramAttr = $param.Attributes | Where-Object {
                $_ -is [System.Management.Automation.ParameterAttribute] -and
                $_.ParameterSetName -eq 'Bulk'
            }
            $paramAttr | Should -Not -BeNullOrEmpty
        }

        It 'Should have Selected parameter' {
            $command.Parameters['Selected'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have IsExternalId parameter' {
            $command.Parameters['IsExternalId'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have IsSecondaryExternalId parameter' {
            $command.Parameters['IsSecondaryExternalId'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have a PassThru switch parameter' {
            $command.Parameters['PassThru'].SwitchParameter | Should -BeTrue
        }

        It 'Should support ShouldProcess' {
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
            $command.Parameters['Confirm'] | Should -Not -BeNullOrEmpty
        }

        It 'Should have Single as the default parameter set' {
            $command.DefaultParameterSet | Should -Be 'Single'
        }

        It 'Should have AttributeId with Id alias' {
            $param = $command.Parameters['AttributeId']
            $param.Aliases | Should -Contain 'Id'
        }
    }

    Context 'Requires Connection' {

        BeforeEach {
            Disconnect-JIM
        }

        It 'Should throw when not connected (Single mode)' {
            { Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 1 -AttributeId 1 -Selected $true -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }

        It 'Should throw when not connected (Bulk mode)' {
            $updates = @{ 1 = @{ selected = $true } }
            { Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 1 -AttributeUpdates $updates -ErrorAction Stop } | Should -Throw '*Connect-JIM*'
        }
    }

    Context 'Help Documentation' {

        BeforeAll {
            $help = Get-Help Set-JIMConnectedSystemAttribute -Full
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

        It 'Should document the Bulk parameter set in description' {
            $help.Description.Text | Should -Match 'Bulk'
        }
    }
}
