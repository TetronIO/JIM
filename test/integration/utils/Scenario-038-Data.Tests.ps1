# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Scenario-038-Data.ps1: the names Scenario 038 (hybrid and Source of Authority, #3029) gives what it
    creates in Active Directory, which must fit Active Directory's limits and stay inside the run's own organisational
    unit. Neither Active Directory nor Entra ID is called.
#>

BeforeAll {
    Set-StrictMode -Version Latest
    . (Join-Path $PSScriptRoot 'EntraIdConnectorLab-Helpers.ps1')
    . (Join-Path $PSScriptRoot 'Scenario-038-Data.ps1')
    # A GitHub run id and attempt, the longest prefix the scenario meets in Actions.
    $script:ActionsPrefix = 'jimit-452638152201-'
    $script:SyncOu = 'OU=CloudSync,DC=meridian,DC=local'
}

Describe 'Get-Scenario038SamAccountName' {

    It 'fits Active Directory''s 20 characters for every object the scenario makes, from a GitHub run prefix' {
        $names = @('p01', 'p02', 'p03', 'staff', 'switched') | ForEach-Object { Get-Scenario038SamAccountName -Prefix $script:ActionsPrefix -Name $_ }

        $names | ForEach-Object { $_.Length | Should -BeLessOrEqual 20 }
    }

    It 'differs between two consecutive GitHub runs' {
        $first = Get-Scenario038SamAccountName -Prefix 'jimit-452638152201-' -Name 'p01'
        $second = Get-Scenario038SamAccountName -Prefix 'jimit-452638152211-' -Name 'p01'

        $first | Should -Not -Be $second
    }

    It 'keeps a short run id whole' {
        Get-Scenario038SamAccountName -Prefix 'jimit-a1b2c3-' -Name 'p01' | Should -Be 'ja1b2c3-p01'
    }

    It 'refuses anything but a run prefix' {
        { Get-Scenario038SamAccountName -Prefix 'jimit-' -Name 'p01' } | Should -Throw '*not a run prefix*'
    }
}

Describe 'Get-Scenario038RunOuDn' {

    It 'puts the run''s unit directly under the synchronised one' {
        Get-Scenario038RunOuDn -Prefix 'jimit-a1b2c3-' -SyncOuDn $script:SyncOu | Should -Be "OU=jimit-a1b2c3,$($script:SyncOu)"
    }
}

Describe 'New-Scenario038People and New-Scenario038Groups' {

    BeforeAll {
        $script:RunOu = Get-Scenario038RunOuDn -Prefix $script:ActionsPrefix -SyncOuDn $script:SyncOu
        $script:People = @(New-Scenario038People -Prefix $script:ActionsPrefix -RunOuDn $script:RunOu)
        $script:Groups = @(New-Scenario038Groups -Prefix $script:ActionsPrefix -RunOuDn $script:RunOu)
    }

    It 'places every object in the run''s own unit, so deleting the unit deletes the run' {
        @($script:People) + @($script:Groups) | ForEach-Object { $_.Dn | Should -BeLike "*,$($script:RunOu)" }
    }

    It 'names every object with the run prefix, which the inbound rules scope on' {
        @($script:People) + @($script:Groups) | ForEach-Object { $_.DisplayName | Should -BeLike "$($script:ActionsPrefix)*" }
    }

    It 'gives every object its own account name' {
        $names = @(@($script:People) + @($script:Groups) | ForEach-Object { $_.SamAccountName })

        @($names | Sort-Object -Unique).Count | Should -Be $names.Count
    }

    It 'lists only the scenario''s own people as members' {
        $keys = @($script:People | ForEach-Object { $_.Key })

        $script:Groups | ForEach-Object { $_.Members | ForEach-Object { $_ | Should -BeIn $keys } }
    }
}
