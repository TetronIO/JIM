# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Populate OpenLDAP with test data for Scenario 031: Intra-type References

.DESCRIPTION
    Creates users and groups in the Source suffix (Yellowstone) whose references point at objects of their own Object
    Type, which no ordering of types can resolve within the Full Synchronisation's page loop (#2492):

    - Every user but the first has a manager, another user. User i reports to user (i - 1) / 5, so the managers form
      a tree five wide, and since Connected System Object ids are random, about half of the users are processed
      before their manager and are deferred to the cross-page reference step.
    - Every group has user members and up to two nested groups: group g contains groups 2g + 1 and 2g + 2, a binary
      tree, so about half of the groups are processed before a group they contain.

    The Target suffix (Glitterband) is left as the base image creates it, with empty ou=People and ou=Groups: JIM
    provisions everything there, so the managers and nested memberships it exports are what the scenario asserts on.

    Deterministic: the same template always produces the same directory, so a run can be timed against another.

.PARAMETER Template
    Data scale template. Nano, Micro, Small, Medium and Large are supported.

.PARAMETER Container
    The Docker container name running OpenLDAP (default: openldap-primary).

.EXAMPLE
    ./Populate-OpenLDAP-Scenario-031.ps1 -Template Medium
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "Large")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$Container = "openldap-primary"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot/utils/Test-Helpers.ps1"
. "$PSScriptRoot/utils/Scenario31-Helpers.ps1"

$scale = Get-Scenario31Scale -Template $Template
$directoryConfig = Get-DirectoryConfig -DirectoryType OpenLDAP -Instance Source
$ldapUri = "$($directoryConfig.LdapSearchScheme)://localhost:$($directoryConfig.LdapSearchPort)"
$peopleOU = "ou=People,dc=yellowstone,dc=local"
$groupsOU = "ou=Groups,dc=yellowstone,dc=local"

Write-TestSection "Scenario 031: Populating OpenLDAP (Source) with $Template template"
Write-Host "Users:  $($scale.Users) (each but the first with a manager)" -ForegroundColor Gray
Write-Host "Groups: $($scale.Groups) (nested two to a parent)" -ForegroundColor Gray

function Import-Ldif {
    param([string]$Content, [string]$What)

    $ldifPath = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $ldifPath -Value $Content -NoNewline
    try {
        $result = bash -c "cat '$ldifPath' | docker exec -i $Container ldapadd -x -H $ldapUri -D '$($directoryConfig.BindDN)' -w '$($directoryConfig.BindPassword)' -c" 2>&1
        if ($LASTEXITCODE -ne 0 -and "$result" -notmatch "already exists") {
            throw "Failed to import $What (exit code $LASTEXITCODE): $result"
        }
    }
    finally {
        Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
    }
}

# Users. Added managers first (index order), so every manager DN exists when its reports are added.
Write-TestStep "Step 1" "Creating $($scale.Users) users"
$builder = [System.Text.StringBuilder]::new()
for ($i = 0; $i -lt $scale.Users; $i++) {
    $uid = Get-Scenario31UserUid -Index $i
    $displayName = "Scenario 31 User $i"
    [void]$builder.AppendLine("dn: uid=$uid,$peopleOU")
    # jimPerson carries jimEmployeeEndDate, which Setup-Scenario-008.ps1's user import rule is scoped on.
    [void]$builder.AppendLine("objectClass: jimPerson")
    [void]$builder.AppendLine("uid: $uid")
    [void]$builder.AppendLine("cn: $displayName")
    [void]$builder.AppendLine("sn: User$i")
    [void]$builder.AppendLine("givenName: Scenario31")
    [void]$builder.AppendLine("displayName: $displayName")
    [void]$builder.AppendLine("mail: $uid@yellowstone.local")
    [void]$builder.AppendLine("title: Engineer")
    [void]$builder.AppendLine("departmentNumber: Engineering")
    [void]$builder.AppendLine("o: Panoply")
    [void]$builder.AppendLine("employeeNumber: S31-$i")
    [void]$builder.AppendLine("employeeType: Active")
    [void]$builder.AppendLine("jimEmployeeEndDate: 20991231235959Z")
    if ($i -gt 0) {
        $managerUid = Get-Scenario31UserUid -Index (Get-Scenario31ManagerIndex -Index $i)
        [void]$builder.AppendLine("manager: uid=$managerUid,$peopleOU")
    }
    [void]$builder.AppendLine("userPassword: Test@123!")
    [void]$builder.AppendLine("")
}
Import-Ldif -Content $builder.ToString() -What "users"
Write-Host "  Created $($scale.Users) users" -ForegroundColor Green

# Groups. Added leaves first (reverse index order), so every nested group DN exists when its parent is added.
Write-TestStep "Step 2" "Creating $($scale.Groups) groups"
[void]$builder.Clear()
for ($g = $scale.Groups - 1; $g -ge 0; $g--) {
    $name = Get-Scenario31GroupName -Index $g
    [void]$builder.AppendLine("dn: cn=$name,$groupsOU")
    [void]$builder.AppendLine("objectClass: jimGroup")
    [void]$builder.AppendLine("cn: $name")
    [void]$builder.AppendLine("description: Scenario 031 group $g")
    [void]$builder.AppendLine("mail: $($name.ToLower())@yellowstone.local")
    [void]$builder.AppendLine("jimGroupType: Managed")
    [void]$builder.AppendLine("jimGroupStatus: Active")
    foreach ($userIndex in Get-Scenario31GroupUserMemberIndexes -Index $g -UserCount $scale.Users -MembersPerGroup $scale.UsersPerGroup) {
        [void]$builder.AppendLine("member: uid=$(Get-Scenario31UserUid -Index $userIndex),$peopleOU")
    }
    foreach ($childIndex in Get-Scenario31NestedGroupIndexes -Index $g -GroupCount $scale.Groups) {
        [void]$builder.AppendLine("member: cn=$(Get-Scenario31GroupName -Index $childIndex),$groupsOU")
    }
    [void]$builder.AppendLine("")
}
Import-Ldif -Content $builder.ToString() -What "groups"
Write-Host "  Created $($scale.Groups) groups" -ForegroundColor Green

Write-TestSection "Scenario 031 OpenLDAP Population Complete"
