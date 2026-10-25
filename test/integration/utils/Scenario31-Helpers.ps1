# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The data model of Scenario 031 (Intra-type References, #2492), shared by its populate script and its assertions.

.DESCRIPTION
    The populate script writes the directory from these functions and the scenario asserts the target against them,
    so the expected manager and nested membership of every object is computed once, in one place.
#>

function Get-Scenario31Scale {
    <#
    .SYNOPSIS
        The users, groups and user members per group a template creates.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Nano", "Micro", "Small", "Medium", "Large")]
        [string]$Template
    )

    switch ($Template) {
        "Nano"   { @{ Users = 6;     Groups = 3;   UsersPerGroup = 2 } }
        "Micro"  { @{ Users = 20;    Groups = 7;   UsersPerGroup = 3 } }
        "Small"  { @{ Users = 200;   Groups = 31;  UsersPerGroup = 10 } }
        "Medium" { @{ Users = 2000;  Groups = 255; UsersPerGroup = 25 } }
        "Large"  { @{ Users = 10000; Groups = 1023; UsersPerGroup = 50 } }
    }
}

function Get-Scenario31UserUid {
    param([Parameter(Mandatory)][int]$Index)
    return "s31user$Index"
}

function Get-Scenario31GroupName {
    param([Parameter(Mandatory)][int]$Index)
    return "S31-Group-$Index"
}

function Get-Scenario31ManagerIndex {
    <#
    .SYNOPSIS
        The index of user Index's manager: a tree five reports wide. User 0 has no manager; returns -1 for it.
    #>
    param([Parameter(Mandatory)][int]$Index)
    if ($Index -le 0) { return -1 }
    return [Math]::Floor(($Index - 1) / 5)
}

function Get-Scenario31NestedGroupIndexes {
    <#
    .SYNOPSIS
        The groups group Index contains: 2 * Index + 1 and 2 * Index + 2, where they exist (a binary tree).
    #>
    param(
        [Parameter(Mandatory)][int]$Index,
        [Parameter(Mandatory)][int]$GroupCount
    )
    return @((2 * $Index + 1), (2 * $Index + 2) | Where-Object { $_ -lt $GroupCount })
}

function Get-Scenario31GroupUserMemberIndexes {
    <#
    .SYNOPSIS
        The users group Index contains directly: a contiguous, wrapping run of MembersPerGroup users starting at
        Index * MembersPerGroup, so groups overlap little and every user index appears in some group.
    #>
    param(
        [Parameter(Mandatory)][int]$Index,
        [Parameter(Mandatory)][int]$UserCount,
        [Parameter(Mandatory)][int]$MembersPerGroup
    )
    $count = [Math]::Min($MembersPerGroup, $UserCount)
    return @(0..($count - 1) | ForEach-Object { ($Index * $MembersPerGroup + $_) % $UserCount })
}
