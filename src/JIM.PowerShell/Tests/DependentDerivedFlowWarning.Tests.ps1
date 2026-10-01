# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the Metaverse-Derived Attribute Flow dependant warnings (#1750, FR 3).

.DESCRIPTION
    Every cmdlet whose change can take away the last contributor of an attribute a derived Attribute Flow reads
    warns about each derived flow the change left with a missing input: a summary line, then one line per flow. It
    never prompts and never blocks; the change has already been made. The private helper composes the lines; the
    cmdlet tests prove each cmdlet hands it the right part of the response.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    # One direct and one indirect dependant, as the API returns them.
    $script:TwoDependants = @(
        [PSCustomObject]@{
            MappingId                    = 101
            TargetMetaverseAttributeName = 'Email'
            SyncRuleId                   = 1
            SyncRuleName                 = 'HR Import'
            ConnectedSystemId            = 10
            ConnectedSystemName          = 'HR'
            MissingInputs                = @([PSCustomObject]@{ MetaverseAttributeName = 'Account Name'; Indirect = $false; Via = @() })
        },
        [PSCustomObject]@{
            MappingId                    = 102
            TargetMetaverseAttributeName = 'User Principal Name'
            SyncRuleId                   = 1
            SyncRuleName                 = 'HR Import'
            ConnectedSystemId            = 10
            ConnectedSystemName          = 'HR'
            MissingInputs                = @([PSCustomObject]@{ MetaverseAttributeName = 'Account Name'; Indirect = $true; Via = @('Email') })
        }
    )
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Write-JIMDependentDerivedFlowWarning' {

    It 'Writes nothing when there are no dependants' {
        InModuleScope JIM {
            $warnings = @(Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows @() 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })
            $warnings | Should -BeNullOrEmpty
        }
    }

    It 'Writes nothing when the response carried no list at all' {
        InModuleScope JIM {
            $warnings = @(Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows $null 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })
            $warnings | Should -BeNullOrEmpty
        }
    }

    It 'Writes a summary line, then one line per derived flow' {
        InModuleScope JIM -Parameters @{ Dependants = $script:TwoDependants } {
            param($Dependants)
            $warnings = @(Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows $Dependants 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            @($warnings).Count | Should -Be 3
            "$($warnings[0])" | Should -Be ("This change left 2 Attribute Flow(s) deriving Metaverse attributes with a missing input. " +
                "The change went ahead; each flow's Missing Input Behaviour now decides what it contributes.")
            "$($warnings[1])" | Should -Be ("Email (Synchronisation Rule 'HR Import', Connected System 'HR', mapping 101) " +
                "reads Account Name, which no longer has an enabled contributor.")
            "$($warnings[2])" | Should -Be ("User Principal Name (Synchronisation Rule 'HR Import', Connected System 'HR', mapping 102) " +
                "reads Account Name through Email, which no longer has an enabled contributor.")
        }
    }

    It 'Words the summary as a prediction for a preview' {
        InModuleScope JIM -Parameters @{ Dependants = $script:TwoDependants } {
            param($Dependants)
            $warnings = @(Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows $Dependants -Prospective 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            "$($warnings[0])" | Should -Be ("This change would leave 2 Attribute Flow(s) deriving Metaverse attributes with a missing input. " +
                "Each flow's Missing Input Behaviour would then decide what it contributes.")
            "$($warnings[1])" | Should -Match 'which would no longer have an enabled contributor\.$'
        }
    }

    It 'Joins several missing inputs of one flow' {
        InModuleScope JIM {
            $dependant = [PSCustomObject]@{
                MappingId                    = 7
                TargetMetaverseAttributeName = 'Email'
                SyncRuleName                 = 'HR Import'
                ConnectedSystemName          = 'HR'
                MissingInputs                = @(
                    [PSCustomObject]@{ MetaverseAttributeName = 'Region'; Indirect = $false; Via = @() },
                    [PSCustomObject]@{ MetaverseAttributeName = 'Account Name'; Indirect = $true; Via = @('Display Name', 'Mail Nickname') }
                )
            }

            $warnings = @(Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows @($dependant) 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            "$($warnings[1])" | Should -Be ("Email (Synchronisation Rule 'HR Import', Connected System 'HR', mapping 7) reads Region, " +
                "and Account Name through Display Name, then Mail Nickname, which no longer have an enabled contributor.")
        }
    }
}

Describe 'Cmdlets that warn about derived dependants' {

    BeforeEach {
        InModuleScope JIM {
            $script:JIMConnection = [PSCustomObject]@{ Url = 'https://jim.example.com'; AuthMethod = 'ApiKey' }
        }
    }

    It 'Remove-JIMSyncRuleMapping warns from the deletion response and never prompts beyond its own confirmation' {
        InModuleScope JIM -Parameters @{ Dependants = $script:TwoDependants } {
            param($Dependants)
            Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedValueCount = 0; DependentDerivedFlows = $Dependants } }

            $warnings = @(Remove-JIMSyncRuleMapping -SyncRuleId 2 -MappingId 5 -Force 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            @($warnings).Count | Should -Be 3
            "$($warnings[1])" | Should -Match '^Email '
        }
    }

    It 'Remove-JIMSyncRuleMapping writes no warning when nothing is left without an input' {
        InModuleScope JIM {
            Mock Invoke-JIMApi { [PSCustomObject]@{ AffectedValueCount = 0; DependentDerivedFlows = @() } }

            $warnings = @(Remove-JIMSyncRuleMapping -SyncRuleId 2 -MappingId 5 -Force 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            $warnings | Should -BeNullOrEmpty
        }
    }

    It 'Set-JIMSyncRuleMapping warns from the update response' {
        InModuleScope JIM -Parameters @{ Dependants = $script:TwoDependants } {
            param($Dependants)
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 5; Warnings = @(); DependentDerivedFlows = $Dependants } }

            $warnings = @(Set-JIMSyncRuleMapping -SyncRuleId 2 -MappingId 5 -Enabled $false -Confirm:$false 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            @($warnings).Count | Should -Be 3
        }
    }

    It 'Set-JIMSyncRule writes the save warnings and the dependants' {
        InModuleScope JIM -Parameters @{ Dependants = $script:TwoDependants } {
            param($Dependants)
            Mock Invoke-JIMApi {
                [PSCustomObject]@{
                    Id                    = 2
                    Name                  = 'AD Import'
                    Warnings              = @('The Attribute Flow to Email calls Now().')
                    DependentDerivedFlows = $Dependants
                }
            }

            $warnings = @(Set-JIMSyncRule -Id 2 -Disable -Confirm:$false 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            @($warnings).Count | Should -Be 4
            "$($warnings[0])" | Should -Be 'The Attribute Flow to Email calls Now().'
            "$($warnings[1])" | Should -Match '^This change left 2 Attribute Flow'
        }
    }

    It 'Remove-JIMSyncRule warns from the immediate deletion response' {
        InModuleScope JIM -Parameters @{ Dependants = $script:TwoDependants } {
            param($Dependants)
            Mock Invoke-JIMApi {
                if ($Method -eq 'DELETE') { return [PSCustomObject]@{ AffectedValueCount = 0; DependentDerivedFlows = $Dependants } }
                [PSCustomObject]@{ Id = 2; Name = 'AD Import' }
            }

            $warnings = @(Remove-JIMSyncRule -Id 2 -Force 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            @($warnings).Count | Should -Be 3
        }
    }

    It 'Remove-JIMSyncRule warns from the queued recall response too' {
        InModuleScope JIM -Parameters @{ Dependants = $script:TwoDependants } {
            param($Dependants)
            Mock Invoke-JIMApi {
                if ($Method -eq 'DELETE') {
                    return [PSCustomObject]@{ RecallActivityId = [guid]::NewGuid(); AffectedValueCount = 3; DependentDerivedFlows = $Dependants }
                }
                [PSCustomObject]@{ Id = 2; Name = 'AD Import' }
            }

            $warnings = @(Remove-JIMSyncRule -Id 2 -Force 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            @($warnings).Count | Should -Be 3
        }
    }

    It 'Import-JIMConnectedSystemSchema -Preview warns that the refresh would leave derived flows without an input' {
        InModuleScope JIM -Parameters @{ Dependants = $script:TwoDependants } {
            param($Dependants)
            Mock Invoke-JIMApi { [PSCustomObject]@{ HasRemovalsOrDefinitionChanges = $true; Dependents = [PSCustomObject]@{ DependentDerivedFlows = $Dependants } } }

            $output = @(Import-JIMConnectedSystemSchema -Id 3 -Preview 3>&1)
            $warnings = @($output | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object { $_.Message })
            $preview = @($output | Where-Object { $_ -isnot [System.Management.Automation.WarningRecord] })

            $preview.Count | Should -Be 1 -Because 'the preview result is still returned'
            $preview[0].HasRemovalsOrDefinitionChanges | Should -BeTrue
            "$($warnings[0])" | Should -Match '^This change would leave 2 Attribute Flow'
        }
    }

    It 'Import-JIMConnectedSystemSchema -DisableDependents warns from the import response' {
        InModuleScope JIM -Parameters @{ Dependants = $script:TwoDependants } {
            param($Dependants)
            Mock Invoke-JIMApi { [PSCustomObject]@{ Id = 3; ObjectTypes = @(); DependentDerivedFlows = $Dependants } }

            $warnings = @(Import-JIMConnectedSystemSchema -Id 3 -DisableDependents -Confirm:$false 3>&1 |
                Where-Object { $_ -is [System.Management.Automation.WarningRecord] } |
                ForEach-Object { $_.Message })

            @($warnings).Count | Should -Be 3
            "$($warnings[0])" | Should -Match '^This change left 2 Attribute Flow'
        }
    }
}
