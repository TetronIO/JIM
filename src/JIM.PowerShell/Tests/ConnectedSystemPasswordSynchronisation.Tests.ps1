# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMConnectedSystemPasswordSynchronisation and
    Set-JIMConnectedSystemPasswordSynchronisation.

.DESCRIPTION
    Password Synchronisation configuration has to be scriptable as well as clickable (#1119, requirement 32):
    an operator rolling it out across many Connected Systems will do so from a script, and an auditor asking
    "which systems are switched on?" wants one command rather than a click per system.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemPasswordSynchronisation' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystemPasswordSynchronisation
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should accept a Connected System from the pipeline' {
            $param = $command.Parameters['InputObject']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ValueFromPipeline } | Should -Not -BeNullOrEmpty
        }

        It 'Should be exported from the module' {
            (Get-Module JIM).ExportedFunctions.Keys | Should -Contain 'Get-JIMConnectedSystemPasswordSynchronisation'
        }
    }

    Context 'Requires Connection' {

        It 'Should error when not connected' {
            { Get-JIMConnectedSystemPasswordSynchronisation -Id 3 -ErrorAction Stop } | Should -Throw
        }
    }
}

Describe 'Set-JIMConnectedSystemPasswordSynchronisation' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Set-JIMConnectedSystemPasswordSynchronisation
        }

        It 'Should have a mandatory Id parameter' {
            $param = $command.Parameters['Id']
            $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
        }

        It 'Should support ShouldProcess' {
            # Enabling Password Synchronisation delivers everything queued while it was off, which is a real
            # change to a live directory and belongs behind -WhatIf and -Confirm.
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }

        It 'Should expose <Name>' -ForEach @(
            @{ Name = 'Enabled' }
            @{ Name = 'TargetObjectType' }
            @{ Name = 'MaxRetries' }
            @{ Name = 'RetryBackoffBase' }
            @{ Name = 'RequireSecureTransport' }
            @{ Name = 'ChangeReason' }
            @{ Name = 'PassThru' }
        ) {
            $command.Parameters[$Name] | Should -Not -BeNullOrEmpty
        }

        It 'Should accept TargetObjectTypeId as an alias for TargetObjectType' {
            # The REST field is targetObjectTypeId, so somebody translating a request body reaches for that name.
            $command.Parameters['TargetObjectType'].Aliases | Should -Contain 'TargetObjectTypeId'
        }

        It 'Should take Enabled as a bool rather than a switch' {
            # A switch could only ever turn it on: -Enabled:$false is unidiomatic, and disabling has to be as
            # easy as enabling because disabling is how an administrator pauses delivery during maintenance.
            $command.Parameters['Enabled'].ParameterType | Should -Be ([bool])
        }

        It 'Should be exported from the module' {
            (Get-Module JIM).ExportedFunctions.Keys | Should -Contain 'Set-JIMConnectedSystemPasswordSynchronisation'
        }

        It 'Should not offer a way to remove the configuration' {
            # Deliberate: removing a configuration would discard the queue of password changes accumulated
            # against it, whereas disabling it is reversible and keeps them.
            Get-Command -Module JIM -Name 'Remove-JIMConnectedSystemPasswordSynchronisation' -ErrorAction SilentlyContinue |
                Should -BeNullOrEmpty
        }
    }

    Context 'Requires Connection' {

        It 'Should error when not connected' {
            { Set-JIMConnectedSystemPasswordSynchronisation -Id 3 -Enabled $true -Confirm:$false -ErrorAction Stop } | Should -Throw
        }
    }
}
