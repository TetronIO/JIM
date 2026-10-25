# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for Scenario 032's data model: the expected managers and nested memberships the scenario asserts on.
#>

BeforeAll {
    . "$PSScriptRoot/Scenario32-Helpers.ps1"
}

Describe 'Get-Scenario32ManagerIndex' {
    It 'gives the first user no manager' {
        Get-Scenario32ManagerIndex -Index 0 | Should -Be -1
    }

    It 'gives every other user a manager earlier in the index order' {
        foreach ($i in 1..500) {
            $manager = Get-Scenario32ManagerIndex -Index $i
            $manager | Should -BeLessThan $i
            $manager | Should -BeGreaterOrEqual 0
        }
    }

    It 'gives each manager at most five direct reports' {
        $reports = 1..500 | Group-Object { Get-Scenario32ManagerIndex -Index $_ }
        ($reports | Measure-Object -Property Count -Maximum).Maximum | Should -Be 5
    }
}

Describe 'Get-Scenario32NestedGroupIndexes' {
    It 'nests two groups under a parent with room for both' {
        Get-Scenario32NestedGroupIndexes -Index 0 -GroupCount 7 | Should -Be @(1, 2)
    }

    It 'nests only the groups that exist' {
        Get-Scenario32NestedGroupIndexes -Index 1 -GroupCount 4 | Should -Be @(3)
        @(Get-Scenario32NestedGroupIndexes -Index 3 -GroupCount 4) | Should -HaveCount 0
    }

    It 'nests every group but the root exactly once' {
        $children = 0..254 | ForEach-Object { Get-Scenario32NestedGroupIndexes -Index $_ -GroupCount 255 }
        $children | Sort-Object | Should -Be (1..254)
    }
}

Describe 'Get-Scenario32GroupUserMemberIndexes' {
    It 'returns a wrapping run of distinct users' {
        $members = Get-Scenario32GroupUserMemberIndexes -Index 2 -UserCount 6 -MembersPerGroup 4
        $members | Should -Be @(2, 3, 4, 5) -Because 'the run starts at 2 * 4 mod 6'
    }

    It 'never asks for more members than there are users' {
        @(Get-Scenario32GroupUserMemberIndexes -Index 0 -UserCount 3 -MembersPerGroup 10) | Should -HaveCount 3
    }
}

Describe 'Get-Scenario32Scale' {
    It 'keeps every template a full binary tree of groups' {
        foreach ($template in 'Nano', 'Micro', 'Small', 'Medium', 'Large') {
            $groups = (Get-Scenario32Scale -Template $template).Groups
            [Math]::Log($groups + 1, 2) % 1 | Should -Be 0 -Because "$template has $groups groups"
        }
    }
}
