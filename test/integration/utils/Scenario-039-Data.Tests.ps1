# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Scenario-039-Data.ps1: the population Scenario 039 (the Entra ID Connector at scale, #3053)
    provisions to the JIM test tenant, and the check that it fits in the tenant before anything is written.
#>

BeforeAll {
    Set-StrictMode -Version Latest
    . (Join-Path $PSScriptRoot 'Scenario-039-Data.ps1')
}

Describe 'New-Scenario039Population' {

    It 'makes the people and groups asked for, every name under the run prefix' {
        $population = New-Scenario039Population -Prefix 'jimit-abc-' -Domain 'example.test' -Users 50 -Groups 6 -AverageMemberships 3

        $population.People.Count | Should -Be 50
        $population.Groups.Count | Should -Be 6
        @($population.People | Where-Object { -not $_.AccountName.StartsWith('jimit-abc-') }) | Should -BeNullOrEmpty
        @($population.Groups | Where-Object { -not $_.DisplayName.StartsWith('jimit-abc-') }) | Should -BeNullOrEmpty
        @($population.People | Where-Object { $_.Upn -notlike '*@example.test' }) | Should -BeNullOrEmpty
    }

    # One group holds everyone, so a run exports a group far beyond Graph's 20 members a request.
    It 'puts everyone in the first group, and each person in as many groups as asked on average, no group twice' {
        $population = New-Scenario039Population -Prefix 'jimit-abc-' -Domain 'example.test' -Users 50 -Groups 6 -AverageMemberships 3

        $population.Groups[0].Members.Count | Should -Be 50
        $memberships = @($population.Groups | ForEach-Object { $_.Members }).Count
        $memberships | Should -Be 150
        foreach ($group in $population.Groups) {
            @($group.Members | Sort-Object -Unique).Count | Should -Be $group.Members.Count
        }
        # The other 100 memberships spread evenly over the other five groups, so no group is left nearly empty.
        @($population.Groups | Select-Object -Skip 1 | ForEach-Object { $_.Members.Count }) | Should -Be @(20, 20, 20, 20, 20)
    }

    It 'gives no one more memberships than there are groups' {
        $population = New-Scenario039Population -Prefix 'jimit-abc-' -Domain 'example.test' -Users 3 -Groups 1 -AverageMemberships 5

        $population.Groups[0].Members.Count | Should -Be 3
    }

    It 'gives everyone but the first person a manager, who is someone earlier' {
        $population = New-Scenario039Population -Prefix 'jimit-abc-' -Domain 'example.test' -Users 30 -Groups 2 -AverageMemberships 1

        $population.People[0].Manager | Should -BeNullOrEmpty
        $index = @{}
        for ($i = 0; $i -lt $population.People.Count; $i++) { $index[$population.People[$i].AccountName] = $i }
        for ($i = 1; $i -lt $population.People.Count; $i++) {
            $index[$population.People[$i].Manager] | Should -BeLessThan $i
        }
    }

    It 'is the same every time for the same prefix and size' {
        $first = New-Scenario039Population -Prefix 'jimit-abc-' -Domain 'example.test' -Users 40 -Groups 5 -AverageMemberships 3
        $second = New-Scenario039Population -Prefix 'jimit-abc-' -Domain 'example.test' -Users 40 -Groups 5 -AverageMemberships 3

        ($first.Groups | ForEach-Object { $_.Members -join '|' }) -join ';' | Should -BeExactly (($second.Groups | ForEach-Object { $_.Members -join '|' }) -join ';')
    }
}

Describe 'Get-Scenario039Fit' {

    It 'fits when the run''s objects leave a tenth of the limit free' {
        $fit = Get-Scenario039Fit -Used 480 -Total 300000 -Users 10000 -Groups 500

        $fit.Fits | Should -BeTrue
    }

    # Purged users still count for a quarter for 30 days, so an earlier run's leave the room smaller.
    It 'does not fit when the run would take the tenant past nine tenths of its limit, and says how much room there is' {
        # 1,500 objects fit in the 6,000 the limit leaves, but not in the 1,000 left once a tenth is kept free.
        $fit = Get-Scenario039Fit -Used 44000 -Total 50000 -Users 1400 -Groups 100

        $fit.Fits | Should -BeFalse
        $fit.Reason | Should -BeLike '*needs 1500 objects*'
        $fit.Reason | Should -BeLike '*room for 1000.*'
    }
}
