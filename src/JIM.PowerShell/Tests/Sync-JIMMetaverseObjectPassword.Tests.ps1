# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Sync-JIMMetaverseObjectPassword.

.DESCRIPTION
    A synchronised password change has to be scriptable (#1119, requirement 31): the usual caller is a
    self-service portal or a service desk tool telling JIM that somebody's password has changed, and neither
    of those clicks a dialog.

    Deliberately a separate cmdlet from Set-JIMMetaverseObjectPassword, which sets a password you choose on
    whichever accounts you name, immediately, and reports whether each target accepted it. This one records
    that the person's password changed and lets delivery happen on its own clock. Collapsing the two would mean one
    cmdlet whose -AllAccounts and "synchronise" behaviours differ in retry semantics, target selection and
    what the return value means.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Sync-JIMMetaverseObjectPassword' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Sync-JIMMetaverseObjectPassword
        }

        It 'Should exist and be exported by the module' {
            $command | Should -Not -BeNullOrEmpty
        }

        It 'Should have a mandatory Id parameter accepting a Guid' {
            $param = $command.Parameters['Id']
            $param | Should -Not -BeNullOrEmpty
            $param.ParameterType | Should -Be ([guid])
            $param.Attributes.Where({ $_ -is [System.Management.Automation.ParameterAttribute] }).Mandatory |
                Should -Contain $true
        }

        It 'Should take the password as a SecureString' {
            # A plain string would sit in the session history and in memory as a readable value; every other
            # password-taking cmdlet in the module takes a SecureString for the same reason.
            $param = $command.Parameters['Password']
            $param | Should -Not -BeNullOrEmpty
            $param.ParameterType | Should -Be ([securestring])
        }

        It 'Should have a mandatory Password parameter' {
            $command.Parameters['Password'].Attributes.Where({ $_ -is [System.Management.Automation.ParameterAttribute] }).Mandatory |
                Should -Contain $true
        }

        It 'Should support ShouldProcess' {
            # It changes somebody's password in every system they have an account in. That belongs behind
            # -WhatIf and -Confirm.
            $command.Parameters['WhatIf'] | Should -Not -BeNullOrEmpty
        }

        It 'Should be a high-impact operation' {
            $binding = $command.ScriptBlock.Attributes.Where({ $_ -is [System.Management.Automation.CmdletBindingAttribute] })
            $binding.ConfirmImpact | Should -Be 'High'
        }

        It 'Should expose <Name>' -ForEach @(
            @{ Name = 'ExpiryBehaviour' }
            @{ Name = 'Force' }
        ) {
            $command.Parameters[$Name] | Should -Not -BeNullOrEmpty
        }

        It 'Should offer only expiry behaviours JIM understands' {
            $validateSet = $command.Parameters['ExpiryBehaviour'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
            $validateSet | Should -Not -BeNullOrEmpty
            $validateSet.ValidValues | Should -Contain 'RequireChangeAtNextSignIn'
            $validateSet.ValidValues | Should -Contain 'ExpiresAccordingToTargetPolicy'
            $validateSet.ValidValues | Should -Contain 'NeverExpires'
        }

        It 'Should accept the Metaverse Object from the pipeline by property name' {
            # So Get-JIMMetaverseObject | Sync-JIMMetaverseObjectPassword works, which is how a bulk change is
            # actually driven.
            $param = $command.Parameters['Id']
            $param.Attributes.Where({ $_ -is [System.Management.Automation.ParameterAttribute] }).ValueFromPipelineByPropertyName |
                Should -Contain $true
        }

        It 'Should not offer an account selection' -ForEach @(
            @{ Name = 'ConnectedSystemId' }
            @{ Name = 'AllAccounts' }
        ) {
            # Which systems receive a synchronised password is the Connected Systems' own configuration, not a
            # per-call choice. Offering a selection here would imply the caller can override it; they cannot.
            $command.Parameters[$Name] | Should -BeNullOrEmpty
        }
    }

    Context 'Connection Validation' {

        It 'Should error when not connected to JIM' {
            $password = ConvertTo-SecureString 'Correct-Horse-42' -AsPlainText -Force
            { Sync-JIMMetaverseObjectPassword -Id ([guid]::NewGuid()) -Password $password -Force -ErrorAction Stop } |
                Should -Throw -ExpectedMessage '*not connected to JIM*'
        }
    }
}
