# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Get-JIMConnectedSystemPasswordPolicy, and for the -Generate switch on
    Set-JIMConnectedSystemObjectPassword.

.DESCRIPTION
    Both close the same gap: IPasswordGeneratorService has always lived on JIM's Application tier and the
    portal called it directly, so automation had to invent its own compliant password or read the target's
    policy and implement the rules by hand.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMConnectedSystemPasswordPolicy' {

    Context 'Parameter Validation' {

        BeforeAll {
            $command = Get-Command Get-JIMConnectedSystemPasswordPolicy
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
            (Get-Module JIM).ExportedFunctions.Keys | Should -Contain 'Get-JIMConnectedSystemPasswordPolicy'
        }
    }

    Context 'Requires Connection' {

        It 'Should error when not connected' {
            { Get-JIMConnectedSystemPasswordPolicy -Id 3 -ErrorAction Stop } | Should -Throw
        }
    }
}

Describe 'Set-JIMConnectedSystemObjectPassword -Generate' {

    BeforeAll {
        $command = Get-Command Set-JIMConnectedSystemObjectPassword
    }

    It 'Should expose a Generate switch' {
        $command.Parameters['Generate'].SwitchParameter | Should -BeTrue
    }

    <#
        The two are mutually exclusive on purpose. Supplying a password and asking JIM to generate one are
        different intentions, and silently preferring either would set a password the caller did not choose.
    #>
    It 'Should put Password and Generate in different parameter sets, so they cannot be given together' {
        $passwordSets = $command.Parameters['Password'].ParameterSets.Keys
        $generateSets = $command.Parameters['Generate'].ParameterSets.Keys

        $passwordSets | Should -Not -Contain '__AllParameterSets'
        $generateSets | Should -Not -Contain '__AllParameterSets'
        ($passwordSets | Where-Object { $generateSets -contains $_ }) | Should -BeNullOrEmpty
    }

    <#
        Deliberately NOT tested by invoking the cmdlet with neither parameter. Password is mandatory in the
        default set, so PowerShell's binder prompts for it rather than failing, and a prompt hangs a CI run:
        this exact test passed locally under `pwsh -NonInteractive` (where the binder throws instead) and hung
        the build-and-test job, which does not pass that switch. That the binder enforces mandatory parameters
        is PowerShell's behaviour to test, not JIM's; what is JIM's is the attribute that declares it.
    #>
    It 'Should make the supplied password mandatory in its own set, so it cannot be omitted silently' {
        $attribute = $command.Parameters['Password'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.ParameterSetName -eq 'SuppliedPassword' }

        $attribute.Mandatory | Should -BeTrue
    }

    <#
        Whichever way the password was arrived at, the cmdlet takes and returns SecureStrings, so a script
        composing the two never has to convert between them.
    #>
    It 'Should take the supplied password as a SecureString' {
        $command.Parameters['Password'].ParameterType | Should -Be ([securestring])
    }
}

Describe 'Set-JIMMetaverseObjectPassword -Generate' {

    BeforeAll {
        $command = Get-Command Set-JIMMetaverseObjectPassword
    }

    It 'Should expose a Generate switch' {
        $command.Parameters['Generate'].SwitchParameter | Should -BeTrue
    }

    <#
        Which accounts and where the password comes from are two independent choices, and neither may be
        inferred: defaulting the account choice would silently reset systems the caller never named, and
        defaulting the password source would set one they did not choose. Four sets is what that costs.
    #>
    It 'Should let Generate combine with either way of choosing accounts' {
        $generateSets = $command.Parameters['Generate'].ParameterSets.Keys

        $generateSets | Should -Contain 'BySystemGeneratedPassword'
        $generateSets | Should -Contain 'AllAccountsGeneratedPassword'
    }

    It 'Should not let Password and Generate be given together' {
        $passwordSets = $command.Parameters['Password'].ParameterSets.Keys
        $generateSets = $command.Parameters['Generate'].ParameterSets.Keys

        ($passwordSets | Where-Object { $generateSets -contains $_ }) | Should -BeNullOrEmpty
    }

    It 'Should still require the accounts to be chosen when generating' {
        $systemSets = $command.Parameters['ConnectedSystemId'].ParameterSets.Keys
        $allSets = $command.Parameters['AllAccounts'].ParameterSets.Keys

        $systemSets | Should -Contain 'BySystemGeneratedPassword'
        $allSets | Should -Contain 'AllAccountsGeneratedPassword'
    }
}
