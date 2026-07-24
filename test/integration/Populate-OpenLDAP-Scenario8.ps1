# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Populate OpenLDAP with test data for Scenario 8: Cross-domain Entitlement Sync

.DESCRIPTION
    Creates users and entitlement groups in Source OpenLDAP (Yellowstone suffix) for
    cross-domain group synchronisation testing. Groups use the jimGroup structural class
    (SUP groupOfNames), which inherits the MUST member constraint and adds mail,
    jimGroupType, and jimGroupStatus attributes. Users use the jimPerson structural class
    (SUP inetOrgPerson), which adds jimEmployeeEndDate (Generalized Time) so the
    LeaverCohort scenario step (#908) can scope the inbound user rule on a relative-date
    criterion; every user gets a fixed far-future end date, and a ~1% cohort (spread across
    the user index space, never a group's initial member) is marked with jimLeaverCohort=TRUE
    for the step to discover and deprovision.

    The Target suffix (Glitterband) gets only OU structure — JIM provisions groups there.

    Structure created in Source (dc=yellowstone,dc=local):
    - ou=People (test users)
    - ou=Entitlements (entitlement groups)

    Structure created in Target (dc=glitterband,dc=local):
    - ou=Entitlements (empty — JIM provisions groups here)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, MediumLarge, Large, Scale100k50Groups, Scale200k55Groups, Scale500k65Groups, Scale750k70Groups, Scale1m80Groups, Scale100k5kGroups, Scale200k10kGroups, Scale500k25kGroups, Scale750k40kGroups, Scale1m60kGroups)

.PARAMETER Instance
    Which suffix to populate (Source or Target)
    - Source: Populates users and groups in Yellowstone
    - Target: Creates OU structure only in Glitterband (JIM provisions the rest)

.EXAMPLE
    ./Populate-OpenLDAP-Scenario8.ps1 -Template Nano -Instance Source

.EXAMPLE
    ./Populate-OpenLDAP-Scenario8.ps1 -Template Small -Instance Source
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Source", "Target")]
    [string]$Instance = "Source",

    [Parameter(Mandatory=$false)]
    [string]$Container = "openldap-primary"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"
. "$PSScriptRoot/utils/Test-GroupHelpers.ps1"

Write-TestSection "Scenario 8: Populating OpenLDAP ($Instance) with $Template template"

# Get scales
$groupScale = Get-Scenario8GroupScale -Template $Template

# Directory configuration
$containerName = $Container
$ldapUri = "ldap://localhost:1389"

$configMap = @{
    Source = @{
        Suffix       = "dc=yellowstone,dc=local"
        AdminDN      = "cn=admin,dc=yellowstone,dc=local"
        Password     = "Test@123!"
        PeopleOU     = "ou=People,dc=yellowstone,dc=local"
        GroupsOU     = "ou=Groups,dc=yellowstone,dc=local"
        Domain       = "yellowstone.local"
    }
    Target = @{
        Suffix       = "dc=glitterband,dc=local"
        AdminDN      = "cn=admin,dc=glitterband,dc=local"
        Password     = "Test@123!"
        PeopleOU     = "ou=People,dc=glitterband,dc=local"
        GroupsOU     = "ou=Groups,dc=glitterband,dc=local"
        Domain       = "glitterband.local"
    }
}

$config = $configMap[$Instance]

Write-Host "Container:       $containerName" -ForegroundColor Gray
Write-Host "Suffix:          $($config.Suffix)" -ForegroundColor Gray
Write-Host "Users to create: $($groupScale.Users)" -ForegroundColor Gray
Write-Host "Groups to create: $($groupScale.TotalGroups)" -ForegroundColor Gray

# Company and department lists matching Samba AD S8 pattern
$scenario8CompanyNames = @{
    "Panoply" = "Panoply"
    "NexusDynamics" = "Nexus Dynamics"
    "OrbitalSystems" = "Akinya"
    "QuantumBridge" = "Rockhopper"
    "StellarLogistics" = "Stellar Logistics"
}

$scenario8DepartmentNames = @{
    "Engineering" = "Engineering"
    "Finance" = "Finance"
    "Human-Resources" = "Human Resources"
    "Information-Technology" = "Information Technology"
    "Legal" = "Legal"
    "Marketing" = "Marketing"
    "Operations" = "Operations"
    "Procurement" = "Procurement"
    "Research-Development" = "Research & Development"
    "Sales" = "Sales"
}

# ============================================================================
# Step 1: Verify Organisational Units
# ============================================================================
Write-TestStep "Step 1" "Verifying organisational units"

# People and Groups OUs already exist from base OpenLDAP setup.
# S8 uses ou=Groups for entitlement groups (matching the existing OpenLDAP hierarchy).
Write-Host "  ou=People and ou=Groups already exist from base setup" -ForegroundColor Green

# For Target instance, no additional setup needed (JIM will provision the rest)
if ($Instance -eq "Target") {
    Write-TestSection "Target Population Complete"
    Write-Host "Template:       $Template" -ForegroundColor Cyan
    Write-Host "OU structure ready - JIM will provision users and groups" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Target OpenLDAP population complete (OU structure only)" -ForegroundColor Green
    exit 0
}

# ============================================================================
# Step 2: Create Users (Source only) via LDIF bulk import
# ============================================================================
Write-TestStep "Step 2" "Creating $($groupScale.Users) users"

$createdUsers = @()
$sortedCompanyKeys = $scenario8CompanyNames.Keys | Sort-Object
$sortedDepartmentKeys = $scenario8DepartmentNames.Keys | Sort-Object

# First names and last names for user generation
$firstNames = @("Alice", "Bob", "Charlie", "Diana", "Eve", "Frank", "Grace", "Henry", "Iris",
    "Jack", "Kate", "Liam", "Mia", "Noah", "Olivia", "Paul", "Quinn", "Rose", "Sam", "Tina")
$lastNames = @("Smith", "Jones", "Williams", "Brown", "Taylor", "Wilson", "Davis", "Clark",
    "Lewis", "Walker", "Hall", "Allen", "Young", "King", "Wright", "Green", "Baker", "Hill",
    "Scott", "Adams")

$titles = @("Manager", "Director", "Analyst", "Specialist", "Coordinator",
    "Engineer", "Developer", "Consultant", "Associate", "Architect")

# Generate user data
$userLdifBuilder = [System.Text.StringBuilder]::new()

for ($i = 0; $i -lt $groupScale.Users; $i++) {
    $firstName = $firstNames[$i % $firstNames.Length]
    $lastName = $lastNames[$i % $lastNames.Length]
    $uid = "$($firstName.ToLower()).$($lastName.ToLower())$i"
    $displayName = "$firstName $lastName (S8-$i)"
    $companyKey = $sortedCompanyKeys[$i % $sortedCompanyKeys.Length]
    $deptKey = $sortedDepartmentKeys[$i % $sortedDepartmentKeys.Length]
    $company = $scenario8CompanyNames[$companyKey]
    $department = $scenario8DepartmentNames[$deptKey]
    $title = $titles[$i % $titles.Length]
    $mail = "$uid@$($config.Domain)"
    $dn = "uid=$uid,$($config.PeopleOU)"

    [void]$userLdifBuilder.AppendLine("dn: $dn")
    # jimPerson (SUP inetOrgPerson STRUCTURAL) adds jimEmployeeEndDate so the LeaverCohort
    # step (#908) can scope the inbound user rule on a relative-date criterion.
    [void]$userLdifBuilder.AppendLine("objectClass: jimPerson")
    [void]$userLdifBuilder.AppendLine("uid: $uid")
    [void]$userLdifBuilder.AppendLine("cn: $displayName")
    [void]$userLdifBuilder.AppendLine("sn: $lastName")
    [void]$userLdifBuilder.AppendLine("givenName: $firstName")
    [void]$userLdifBuilder.AppendLine("displayName: $displayName")
    [void]$userLdifBuilder.AppendLine("mail: $mail")
    [void]$userLdifBuilder.AppendLine("title: $title")
    [void]$userLdifBuilder.AppendLine("departmentNumber: $department")
    [void]$userLdifBuilder.AppendLine("o: $company")
    [void]$userLdifBuilder.AppendLine("employeeNumber: S8-$i")
    # employeeType: 90% Active, 10% Archived (matching Samba AD userAccountControl distribution)
    $employeeType = if ($i -eq 0) { "Archived" } elseif (($i % 10) -eq 9) { "Archived" } else { "Active" }
    [void]$userLdifBuilder.AppendLine("employeeType: $employeeType")
    # Every user is currently employed: a fixed far-future end date keeps everyone inside the
    # "jimEmployeeEndDate >= now" scope window. The LeaverCohort step later moves the cohort's
    # end dates to a near-future instant; a fixed constant (rather than now+offset) keeps the
    # populate output deterministic for snapshot images.
    [void]$userLdifBuilder.AppendLine("jimEmployeeEndDate: 20991231235959Z")
    [void]$userLdifBuilder.AppendLine("userPassword: Test@123!")
    [void]$userLdifBuilder.AppendLine("")

    $createdUsers += @{
        Uid        = $uid
        DN         = $dn
        FirstName  = $firstName
        LastName   = $lastName
        Company    = $company
        CompanyKey = $companyKey
        Department = $department
        DeptKey    = $deptKey
        Title      = $title
    }
}

# Import users via stdin piping
$userLdifContent = $userLdifBuilder.ToString()
$ldifPath = [System.IO.Path]::GetTempFileName()
Set-Content -Path $ldifPath -Value $userLdifContent -NoNewline

try {
    $result = bash -c "cat '$ldifPath' | docker exec -i $containerName ldapadd -x -H $ldapUri -D '$($config.AdminDN)' -w '$($config.Password)' -c" 2>&1
    if ($LASTEXITCODE -ne 0 -and "$result" -notmatch "already exists") {
        Write-Host "  Warning during user import: $result" -ForegroundColor Yellow
    }
}
finally {
    Remove-Item -Path $ldifPath -Force -ErrorAction SilentlyContinue
}

Write-Host "  Created $($createdUsers.Count) users" -ForegroundColor Green

# ============================================================================
# Step 3: Create Groups (Source only) via LDIF bulk import
# ============================================================================
Write-TestStep "Step 3" "Creating $($groupScale.TotalGroups) groups"

$createdGroups = @()
$groupLdifBuilder = [System.Text.StringBuilder]::new()

# Group status distribution: 80% Active, 10% Pending Review, 10% Archived (deterministic)
$groupStatuses = @("Active", "Active", "Active", "Active", "Active", "Active", "Active", "Active", "Pending Review", "Archived")
$groupIndex = 0

# Company groups
$companyCount = [Math]::Min($groupScale.Companies, $sortedCompanyKeys.Length)
for ($g = 0; $g -lt $companyCount; $g++) {
    $companyKey = $sortedCompanyKeys[$g]
    $groupName = "Company-$companyKey"
    $dn = "cn=$groupName,$($config.GroupsOU)"

    # Find first user in this company for initial member (jimGroup inherits groupOfNames MUST constraint)
    $companyUsers = @($createdUsers | Where-Object { $_.CompanyKey -eq $companyKey })
    $initialMember = if ($companyUsers.Count -gt 0) { $companyUsers[0].DN } else { $createdUsers[0].DN }

    $groupMail = "$($groupName.ToLower())@$($config.Domain)"
    $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
    $groupIndex++

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: Company group for $($scenario8CompanyNames[$companyKey])")
    [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
    [void]$groupLdifBuilder.AppendLine("jimGroupType: Managed")
    [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
    [void]$groupLdifBuilder.AppendLine("member: $initialMember")
    [void]$groupLdifBuilder.AppendLine("")

    $createdGroups += @{
        Name        = $groupName
        DN          = $dn
        Category    = "Company"
        FilterKey   = $companyKey
        FilterField = "CompanyKey"
        Members     = @($initialMember)
    }
}

# Department groups
#
# Naming:
#   Slots 0..N-1 (where N = $sortedDepartmentKeys.Length) reuse the user-side
#   department keys, so the membership rule "users where DeptKey == FilterKey"
#   keeps matching real users in those groups (legacy template behaviour).
#
#   Slots N..count-1 take names from Get-DepartmentNames, deliberately skipping
#   anything that overlaps with the keyed names so we don't end up with two
#   groups sharing a name. These groups have no DeptKey filter; the membership
#   assignment below uses Get-LongTailGroupSize to size them with the realistic
#   long-tail distribution intended by the new template.
$deptKeyCount = $sortedDepartmentKeys.Length
$deptNonKeyCount = $groupScale.Departments - $deptKeyCount
if ($deptNonKeyCount -lt 0) { $deptNonKeyCount = 0 }
$deptKeyCountToUse = [Math]::Min($groupScale.Departments, $deptKeyCount)

# Pre-compute long-tail department names, excluding any that collide with the
# user-side keys. Over-request from Get-DepartmentNames and then filter so we
# always have enough non-colliding names to fill $deptNonKeyCount slots.
$deptLongTailNames = @()
if ($deptNonKeyCount -gt 0) {
    $keySet = @{}
    foreach ($k in $sortedDepartmentKeys) { $keySet[$k] = $true }
    $oversample = $deptNonKeyCount + $deptKeyCount + 50
    $deptLongTailNames = @(Get-DepartmentNames -Count $oversample | Where-Object { -not $keySet.ContainsKey($_) })
}

$deptLongTailCursor = 0
for ($g = 0; $g -lt $groupScale.Departments; $g++) {
    if ($g -lt $deptKeyCountToUse) {
        # Keyed department (user-attribute filter still applies)
        $deptKey = $sortedDepartmentKeys[$g]
        $groupName = "Dept-$deptKey"
        $deptUsers = @($createdUsers | Where-Object { $_.DeptKey -eq $deptKey })
        $initialMember = if ($deptUsers.Count -gt 0) { $deptUsers[0].DN } else { $createdUsers[0].DN }
        $description = "Department group for $($scenario8DepartmentNames[$deptKey])"
        $filterKey = $deptKey
        $filterField = "DeptKey"
        $categoryIndexForGroup = $g
    } else {
        # Long-tail department (sized via Get-LongTailGroupSize at membership time)
        if ($deptLongTailCursor -ge $deptLongTailNames.Count) {
            throw "Ran out of non-colliding department names. Available: $($deptLongTailNames.Count), requested: $deptNonKeyCount. Extend `$script:DepartmentNames or `$script:DepartmentQualifiers."
        }
        $deptName = $deptLongTailNames[$deptLongTailCursor]
        $deptLongTailCursor++
        $groupName = "Dept-$deptName"
        $initialMember = $createdUsers[$g % $createdUsers.Count].DN
        $description = "Department group: $deptName"
        $filterKey = $null
        $filterField = $null
        $categoryIndexForGroup = $g - $deptKeyCountToUse  # index within long-tail tiers
    }

    $dn = "cn=$groupName,$($config.GroupsOU)"
    $groupMail = "$($groupName.ToLower())@$($config.Domain)"
    $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
    $groupIndex++

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: $description")
    [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
    [void]$groupLdifBuilder.AppendLine("jimGroupType: Managed")
    [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
    [void]$groupLdifBuilder.AppendLine("member: $initialMember")
    [void]$groupLdifBuilder.AppendLine("")

    $createdGroups += @{
        Name          = $groupName
        DN            = $dn
        Category      = "Department"
        FilterKey     = $filterKey
        FilterField   = $filterField
        CategoryIndex = $categoryIndexForGroup
        Members       = @($initialMember)
    }
}
$deptCount = $groupScale.Departments

# Location groups
#
# Drop the historical inline 10-entry list and use the helper's curated pool
# of 20 names. Membership is now sized via Get-LongTailGroupSize for variety
# (previously every location ended up with $createdUsers.Count / $locCount
# members, i.e. all the same size).
$locCount = $groupScale.Locations
for ($g = 0; $g -lt $locCount; $g++) {
    $baseLoc = $script:LocationNames[$g % $script:LocationNames.Count]
    $locRotation = [Math]::Floor($g / $script:LocationNames.Count)
    $locName = if ($locRotation -eq 0) { $baseLoc } else { "${baseLoc}-${locRotation}" }
    $groupName = "Location-$locName"
    $dn = "cn=$groupName,$($config.GroupsOU)"

    # Assign a subset of users to location groups
    $initialMember = $createdUsers[$g % $createdUsers.Count].DN

    $groupMail = "$($groupName.ToLower())@$($config.Domain)"
    $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
    $groupIndex++

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: Location group for $locName")
    [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
    [void]$groupLdifBuilder.AppendLine("jimGroupType: Managed")
    [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
    [void]$groupLdifBuilder.AppendLine("member: $initialMember")
    [void]$groupLdifBuilder.AppendLine("")

    $createdGroups += @{
        Name          = $groupName
        DN            = $dn
        Category      = "Location"
        FilterKey     = $locName
        FilterField   = $null
        CategoryIndex = $g
        Members       = @($initialMember)
    }
}

# Project groups
if ($groupScale.Projects -gt 0) {
    $projectNames = Get-ProjectNames -Count $groupScale.Projects
    for ($g = 0; $g -lt $groupScale.Projects; $g++) {
        $projectName = $projectNames[$g]
        $groupName = "Project-$projectName"
        $dn = "cn=$groupName,$($config.GroupsOU)"

        $initialMember = $createdUsers[$g % $createdUsers.Count].DN

        $groupMail = "$($groupName.ToLower())@$($config.Domain)"
        $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
        $groupIndex++

        [void]$groupLdifBuilder.AppendLine("dn: $dn")
        [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
        [void]$groupLdifBuilder.AppendLine("cn: $groupName")
        [void]$groupLdifBuilder.AppendLine("description: Project group for $projectName")
        [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
        [void]$groupLdifBuilder.AppendLine("jimGroupType: Self-Service")
        [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
        [void]$groupLdifBuilder.AppendLine("member: $initialMember")
        [void]$groupLdifBuilder.AppendLine("")

        $createdGroups += @{
            Name        = $groupName
            DN          = $dn
            Category    = "Project"
            FilterKey   = $projectName
            FilterField = $null
            CategoryIndex = $g
            Members     = @($initialMember)
        }
    }
}

# ----------------------------------------------------------------------------
# Long-tail categories (Scale100k5kGroups / Scale200k10kGroups /
# Scale500k25kGroups / Scale750k40kGroups / Scale1m60kGroups). Each group's
# category-internal index drives Get-LongTailGroupSize during membership
# assignment below.
# ----------------------------------------------------------------------------

# Read possibly-missing scale fields with default 0 (legacy templates don't
# define the long-tail categories).
function Read-ScaleCount { param($H, [string]$K) if ($H.ContainsKey($K)) { return [int]$H[$K] } return 0 }
$allStaffCount     = Read-ScaleCount $groupScale 'AllStaff'
$divisionCount     = Read-ScaleCount $groupScale 'Divisions'
$applicationCount  = Read-ScaleCount $groupScale 'Applications'
$distributionCount = Read-ScaleCount $groupScale 'DistributionLists'
$roleCount         = Read-ScaleCount $groupScale 'Roles'

# AllStaff groups: very large, all/most users
for ($g = 0; $g -lt $allStaffCount; $g++) {
    $name = $script:AllStaffNames[$g % $script:AllStaffNames.Count]
    $groupName = "AllStaff-$name"
    $dn = "cn=$groupName,$($config.GroupsOU)"
    $initialMember = $createdUsers[$g % $createdUsers.Count].DN
    $groupMail = "$($groupName.ToLower())@$($config.Domain)"
    $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
    $groupIndex++

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: All-staff group: $name")
    [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
    [void]$groupLdifBuilder.AppendLine("jimGroupType: Managed")
    [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
    [void]$groupLdifBuilder.AppendLine("member: $initialMember")
    [void]$groupLdifBuilder.AppendLine("")

    $createdGroups += @{
        Name = $groupName; DN = $dn; Category = "AllStaff"
        FilterKey = $null; FilterField = $null
        CategoryIndex = $g; Members = @($initialMember)
    }
}

# Division groups: users assigned to a division by index modulo $divisionCount
for ($g = 0; $g -lt $divisionCount; $g++) {
    $name = $script:DivisionNames[$g % $script:DivisionNames.Count]
    $groupName = "Division-$name"
    $dn = "cn=$groupName,$($config.GroupsOU)"
    $initialMember = $createdUsers[$g % $createdUsers.Count].DN
    $groupMail = "$($groupName.ToLower())@$($config.Domain)"
    $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
    $groupIndex++

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: Division group for $name")
    [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
    [void]$groupLdifBuilder.AppendLine("jimGroupType: Managed")
    [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
    [void]$groupLdifBuilder.AppendLine("member: $initialMember")
    [void]$groupLdifBuilder.AppendLine("")

    $createdGroups += @{
        Name = $groupName; DN = $dn; Category = "Division"
        FilterKey = $g; FilterField = $null  # FilterKey here is the division index
        CategoryIndex = $g; Members = @($initialMember)
    }
}

# Application access groups
if ($applicationCount -gt 0) {
    $appNames = Get-CombinatorialNames -Count $applicationCount `
        -Prefixes $script:ApplicationCoreNames `
        -Suffixes $script:ApplicationAccessLevels
    for ($g = 0; $g -lt $applicationCount; $g++) {
        $name = $appNames[$g]
        $groupName = "App-$name"
        $dn = "cn=$groupName,$($config.GroupsOU)"
        $initialMember = $createdUsers[$g % $createdUsers.Count].DN
        $groupMail = "$($groupName.ToLower())@$($config.Domain)"
        $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
        $groupIndex++

        [void]$groupLdifBuilder.AppendLine("dn: $dn")
        [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
        [void]$groupLdifBuilder.AppendLine("cn: $groupName")
        [void]$groupLdifBuilder.AppendLine("description: Application access group: $name")
        [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
        [void]$groupLdifBuilder.AppendLine("jimGroupType: Self-Service")
        [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
        [void]$groupLdifBuilder.AppendLine("member: $initialMember")
        [void]$groupLdifBuilder.AppendLine("")

        $createdGroups += @{
            Name = $groupName; DN = $dn; Category = "Application"
            FilterKey = $null; FilterField = $null
            CategoryIndex = $g; Members = @($initialMember)
        }
    }
}

# Distribution lists
if ($distributionCount -gt 0) {
    $dlNames = Get-CombinatorialNames -Count $distributionCount `
        -Prefixes $script:DistributionListTopics `
        -Suffixes $script:DistributionListThemes
    for ($g = 0; $g -lt $distributionCount; $g++) {
        $name = $dlNames[$g]
        $groupName = "DL-$name"
        $dn = "cn=$groupName,$($config.GroupsOU)"
        $initialMember = $createdUsers[$g % $createdUsers.Count].DN
        $groupMail = "$($groupName.ToLower())@$($config.Domain)"
        $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
        $groupIndex++

        [void]$groupLdifBuilder.AppendLine("dn: $dn")
        [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
        [void]$groupLdifBuilder.AppendLine("cn: $groupName")
        [void]$groupLdifBuilder.AppendLine("description: Distribution list: $name")
        [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
        [void]$groupLdifBuilder.AppendLine("jimGroupType: Self-Service")
        [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
        [void]$groupLdifBuilder.AppendLine("member: $initialMember")
        [void]$groupLdifBuilder.AppendLine("")

        $createdGroups += @{
            Name = $groupName; DN = $dn; Category = "DistributionList"
            FilterKey = $null; FilterField = $null
            CategoryIndex = $g; Members = @($initialMember)
        }
    }
}

# Roles
if ($roleCount -gt 0) {
    $roleNames = Get-CombinatorialNames -Count $roleCount `
        -Prefixes $script:RoleQualifiers `
        -Suffixes $script:RoleNouns
    for ($g = 0; $g -lt $roleCount; $g++) {
        $name = $roleNames[$g]
        $groupName = "Role-$name"
        $dn = "cn=$groupName,$($config.GroupsOU)"
        $initialMember = $createdUsers[$g % $createdUsers.Count].DN
        $groupMail = "$($groupName.ToLower())@$($config.Domain)"
        $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
        $groupIndex++

        [void]$groupLdifBuilder.AppendLine("dn: $dn")
        [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
        [void]$groupLdifBuilder.AppendLine("cn: $groupName")
        [void]$groupLdifBuilder.AppendLine("description: RBAC role: $name")
        [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
        [void]$groupLdifBuilder.AppendLine("jimGroupType: Managed")
        [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
        [void]$groupLdifBuilder.AppendLine("member: $initialMember")
        [void]$groupLdifBuilder.AppendLine("")

        $createdGroups += @{
            Name = $groupName; DN = $dn; Category = "Role"
            FilterKey = $null; FilterField = $null
            CategoryIndex = $g; Members = @($initialMember)
        }
    }
}

# Import groups via stdin piping
$groupLdifContent = $groupLdifBuilder.ToString()
$groupLdifPath = [System.IO.Path]::GetTempFileName()
Set-Content -Path $groupLdifPath -Value $groupLdifContent -NoNewline

try {
    $result = bash -c "cat '$groupLdifPath' | docker exec -i $containerName ldapadd -x -H $ldapUri -D '$($config.AdminDN)' -w '$($config.Password)' -c" 2>&1
    if ($LASTEXITCODE -ne 0 -and "$result" -notmatch "already exists") {
        Write-Host "  Warning during group import: $result" -ForegroundColor Yellow
    }
}
finally {
    Remove-Item -Path $groupLdifPath -Force -ErrorAction SilentlyContinue
}

Write-Host "  Created $($createdGroups.Count) groups" -ForegroundColor Green

# ============================================================================
# Step 3b: Select and mark the leaver cohort (Source only, #908)
#
# The LeaverCohort scenario step deprovisions a date-driven cohort via the Temporal
# Scope Reconciler and asserts the resulting membership removals reach the target.
# The cohort is chosen HERE because only the populate script holds every group's
# membership plan in memory: excluding each group's initial member from the cohort
# guarantees no group is ever emptied by the cohort's removal (groupOfNames requires
# at least one member value). Cohort users are marked in the directory itself
# (jimLeaverCohort=TRUE) so the choice survives snapshot images and needs no
# host-side state; the scenario step discovers them with a single LDAP search.
# ============================================================================
Write-TestStep "Step 3b" "Selecting and marking the leaver cohort"

# ~1% of users, at least 1, capped at 10,000 (the reconciler burst size worth measuring).
$cohortTargetSize = [int][Math]::Min(10000, [Math]::Max(1, [Math]::Ceiling($createdUsers.Count / 100.0)))

$initialMemberDns = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($grp in $createdGroups) {
    [void]$initialMemberDns.Add([string]$grp.Members[0])
}

# Spread the cohort across the user index space (memberships are assigned as contiguous
# index ranges, so a stride-spread cohort touches many groups rather than one block).
# Higher phases first: initial members cluster at low indices, so this minimises skips.
$cohortUsers = [System.Collections.Generic.List[object]]::new()
$userCountForCohort = $createdUsers.Count
$cohortStride = [int][Math]::Max(1, [Math]::Floor($userCountForCohort / $cohortTargetSize))
:cohortSearch for ($phase = $cohortStride - 1; $phase -ge 0; $phase--) {
    for ($idx = $phase; $idx -lt $userCountForCohort; $idx += $cohortStride) {
        $candidate = $createdUsers[$idx]
        if ($initialMemberDns.Contains([string]$candidate.DN)) { continue }
        $cohortUsers.Add($candidate)
        if ($cohortUsers.Count -ge $cohortTargetSize) { break cohortSearch }
    }
}

if ($cohortUsers.Count -eq 0) {
    throw "Leaver cohort selection found no eligible users: every user is the initial member of some group. This template's user count is too small relative to its group count."
}
if ($cohortUsers.Count -lt $cohortTargetSize) {
    Write-Host "  WARNING Cohort reduced to $($cohortUsers.Count) of $cohortTargetSize target (users that are initial group members are excluded)" -ForegroundColor Yellow
}

$cohortLdifBuilder = [System.Text.StringBuilder]::new()
foreach ($cohortUser in $cohortUsers) {
    [void]$cohortLdifBuilder.AppendLine("dn: $($cohortUser.DN)")
    [void]$cohortLdifBuilder.AppendLine("changetype: modify")
    [void]$cohortLdifBuilder.AppendLine("replace: jimLeaverCohort")
    [void]$cohortLdifBuilder.AppendLine("jimLeaverCohort: TRUE")
    [void]$cohortLdifBuilder.AppendLine("")
}

$cohortLdifPath = [System.IO.Path]::GetTempFileName()
Set-Content -Path $cohortLdifPath -Value $cohortLdifBuilder.ToString() -NoNewline
try {
    $result = bash -c "cat '$cohortLdifPath' | docker exec -i $containerName ldapmodify -x -H $ldapUri -D '$($config.AdminDN)' -w '$($config.Password)' -c" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to mark leaver cohort users (exit code $LASTEXITCODE): $result"
    }
}
finally {
    Remove-Item -Path $cohortLdifPath -Force -ErrorAction SilentlyContinue
}

Write-Host "  Marked $($cohortUsers.Count) leaver-cohort users (jimLeaverCohort=TRUE), e.g. $($cohortUsers[0].Uid)" -ForegroundColor Green

# ============================================================================
# Step 4: Assign group memberships (Source only)
#
# Membership assignment is batched across multiple groups per docker exec call.
# Each ldapmodify invocation processes ~5000 member lines spanning many groups,
# which is essential for the long-tail templates (Scale100k5kGroups through
# Scale1m60kGroups, up to ~60k groups and ~10M memberships) and improves
# smaller templates harmlessly.
# ============================================================================
Write-TestStep "Step 4" "Assigning group memberships (batched ldapmodify)"

$userCount = $createdUsers.Count
$totalMemberships = 0
$groupsProcessed = 0
$flushBatchSize = 5000   # member lines per docker exec call

# Pre-compute all (group, members-to-add) work items.
$workItems = [System.Collections.Generic.List[object]]::new()

foreach ($group in $createdGroups) {
    $catIdx = if ($group.ContainsKey('CategoryIndex')) { [int]$group.CategoryIndex } else { 0 }
    $membersToAdd = @()

    switch ($group.Category) {
        "Company" {
            $matching = @($createdUsers | Where-Object { $_.CompanyKey -eq $group.FilterKey })
            $membersToAdd = @($matching | ForEach-Object { $_.DN })
        }
        "Department" {
            # Two flavours: keyed departments (FilterKey set) keep the original
            # rule of "all users with matching DeptKey", which preserves legacy
            # template behaviour. Long-tail departments (FilterKey null) are
            # sized via Get-LongTailGroupSize like Application/Role.
            if ($group.FilterField -eq "DeptKey" -and $null -ne $group.FilterKey) {
                $matching = @($createdUsers | Where-Object { $_.DeptKey -eq $group.FilterKey })
                $membersToAdd = @($matching | ForEach-Object { $_.DN })
            } else {
                $targetSize = Get-LongTailGroupSize -Category Department -Index $catIdx -UserCount $userCount
                $offset = ($catIdx * 19) % [Math]::Max(1, $userCount)
                $membersToAdd = @(
                    for ($u = 0; $u -lt $targetSize; $u++) {
                        $createdUsers[($offset + $u) % $userCount].DN
                    }
                )
            }
        }
        "Location" {
            # Long-tail sizing: produces varied location sizes (1k-10k typical)
            # rather than the previous uniform "$userCount / $locCount" buckets.
            $targetSize = Get-LongTailGroupSize -Category Location -Index $catIdx -UserCount $userCount
            $offset = ($catIdx * 29) % [Math]::Max(1, $userCount)
            $membersToAdd = @(
                for ($u = 0; $u -lt $targetSize; $u++) {
                    $createdUsers[($offset + $u) % $userCount].DN
                }
            )
        }
        "Project" {
            # Long-tail sizing: small groups by default, occasional larger outliers.
            $targetSize = Get-LongTailGroupSize -Category Project -Index $catIdx -UserCount $userCount
            $offset = ($catIdx * 11) % [Math]::Max(1, $userCount)
            $membersToAdd = @(
                for ($u = 0; $u -lt $targetSize; $u++) {
                    $createdUsers[($offset + $u) % $userCount].DN
                }
            )
        }
        "AllStaff" {
            $targetSize = Get-LongTailGroupSize -Category AllStaff -Index $catIdx -UserCount $userCount
            # Pick the first $targetSize users (deterministic; for AllStaff this is
            # effectively all users for tier 0, ~80% of users for tier 1).
            $membersToAdd = @(
                for ($u = 0; $u -lt $targetSize; $u++) {
                    $createdUsers[$u].DN
                }
            )
        }
        "Division" {
            # Long-tail sizing: tiers in Get-LongTailGroupSize spread divisions
            # across 5%-25% of users instead of the previous uniform 10% buckets.
            $targetSize = Get-LongTailGroupSize -Category Division -Index $catIdx -UserCount $userCount
            $offset = ($catIdx * 31) % [Math]::Max(1, $userCount)
            $membersToAdd = @(
                for ($u = 0; $u -lt $targetSize; $u++) {
                    $createdUsers[($offset + $u) % $userCount].DN
                }
            )
        }
        "Application" {
            $targetSize = Get-LongTailGroupSize -Category Application -Index $catIdx -UserCount $userCount
            $offset = ($catIdx * 17) % [Math]::Max(1, $userCount)
            $membersToAdd = @(
                for ($u = 0; $u -lt $targetSize; $u++) {
                    $createdUsers[($offset + $u) % $userCount].DN
                }
            )
        }
        "DistributionList" {
            $targetSize = Get-LongTailGroupSize -Category DistributionList -Index $catIdx -UserCount $userCount
            $offset = ($catIdx * 23) % [Math]::Max(1, $userCount)
            $membersToAdd = @(
                for ($u = 0; $u -lt $targetSize; $u++) {
                    $createdUsers[($offset + $u) % $userCount].DN
                }
            )
        }
        "Role" {
            $targetSize = Get-LongTailGroupSize -Category Role -Index $catIdx -UserCount $userCount
            $offset = ($catIdx * 13) % [Math]::Max(1, $userCount)
            $membersToAdd = @(
                for ($u = 0; $u -lt $targetSize; $u++) {
                    $createdUsers[($offset + $u) % $userCount].DN
                }
            )
        }
    }

    # Exclude the initial member already added at group creation
    $existing = $group.Members
    $newMembers = @($membersToAdd | Where-Object { $_ -notin $existing })

    if ($newMembers.Count -gt 0) {
        [void]$workItems.Add(@{ GroupDN = $group.DN; GroupName = $group.Name; Members = $newMembers })
    }
}

Write-Host "  Computed memberships for $($workItems.Count) groups" -ForegroundColor Gray

# Flush helper inlined below as a script block to keep the test credential
# out of a separately-callable function (PSScriptAnalyzer flags any function
# parameter named *Password*; inlining sidesteps the issue while preserving
# the single-codepath property we want for the batched LDIF flush).
$flushBatch = {
    param([string]$LdifContent)
    if ([string]::IsNullOrEmpty($LdifContent)) { return }
    $tmp = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $tmp -Value $LdifContent -NoNewline
    try {
        $result = bash -c "cat '$tmp' | docker exec -i $containerName ldapmodify -x -H $ldapUri -D '$($config.AdminDN)' -w '$($config.Password)' -c" 2>&1
        if ($LASTEXITCODE -ne 0 -and "$result" -notmatch "already exists" -and "$result" -notmatch "Type or value exists") {
            Write-Verbose "  ldapmodify warnings: $result"
        }
    }
    finally {
        Remove-Item -Path $tmp -Force -ErrorAction SilentlyContinue
    }
}

# Stream work items into a single LDIF buffer, flush at $flushBatchSize lines.
#
# CRITICAL: emit ONE modify operation per group-chunk carrying all of that
# chunk's member values, NOT one modify per member. back-mdb stores a group as
# a single entry, so every "add: member" rewrites the whole entry; issuing one
# modify per member makes building an N-member group O(N^2). At 500k users the
# 315k-member AllStaff group ground slapd for hours at ~0.1s/member and never
# finished. A single multi-valued modify applies all values in one entry
# rewrite, restoring ~O(N) behaviour. Do NOT "simplify" this back to one modify
# per member. Chunking at $flushBatchSize also keeps each request under
# OpenLDAP's authenticated socket-buffer cap (olcSockbufMaxIncomingAuth, ~4MB)
# and bounds the temp-file size for very large groups.
$batch = [System.Text.StringBuilder]::new()
$batchMemberLineCount = 0

foreach ($wi in $workItems) {
    $members = $wi.Members
    # Chunk very large groups so each modify request stays within the socket
    # buffer limit; each chunk is still a single multi-valued modify. Chunk
    # members are distinct (Get-LongTailGroupSize never exceeds the user count,
    # so the wrapped index window cannot repeat) and the initial member is
    # excluded above, so the atomic add never trips "Type or value exists".
    $chunkSize = $flushBatchSize
    for ($c = 0; $c -lt $members.Count; $c += $chunkSize) {
        $end = [Math]::Min($c + $chunkSize, $members.Count) - 1
        $chunk = $members[$c..$end]

        [void]$batch.AppendLine("dn: $($wi.GroupDN)")
        [void]$batch.AppendLine("changetype: modify")
        [void]$batch.AppendLine("add: member")
        foreach ($memberDn in $chunk) {
            [void]$batch.AppendLine("member: $memberDn")
        }
        [void]$batch.AppendLine("")
        $batchMemberLineCount += $chunk.Count

        $totalMemberships += $chunk.Count

        if ($batchMemberLineCount -ge $flushBatchSize) {
            & $flushBatch $batch.ToString()
            $batch.Clear() | Out-Null
            $batchMemberLineCount = 0
        }
    }
    $groupsProcessed++
    if (($groupsProcessed % 500) -eq 0) {
        Write-Host "    Progress: $groupsProcessed/$($workItems.Count) groups, $totalMemberships memberships..." -ForegroundColor Gray
    }
}

# Final flush
if ($batchMemberLineCount -gt 0) {
    & $flushBatch $batch.ToString()
}

Write-Host "  Assigned $totalMemberships memberships across $($workItems.Count) groups" -ForegroundColor Green

# ============================================================================
# Summary
# ============================================================================
Write-TestSection "Source Population Complete"
Write-Host "Template:         $Template" -ForegroundColor Cyan
Write-Host "Users created:    $($createdUsers.Count)" -ForegroundColor Cyan
Write-Host "Groups created:   $($createdGroups.Count)" -ForegroundColor Cyan
if ($companyCount       -gt 0) { Write-Host "  - Companies:        $companyCount" -ForegroundColor Cyan }
if ($allStaffCount      -gt 0) { Write-Host "  - AllStaff:         $allStaffCount" -ForegroundColor Cyan }
if ($divisionCount      -gt 0) { Write-Host "  - Divisions:        $divisionCount" -ForegroundColor Cyan }
if ($deptCount          -gt 0) { Write-Host "  - Departments:      $deptCount" -ForegroundColor Cyan }
if ($locCount           -gt 0) { Write-Host "  - Locations:        $locCount" -ForegroundColor Cyan }
if ($groupScale.Projects -gt 0) { Write-Host "  - Projects:         $($groupScale.Projects)" -ForegroundColor Cyan }
if ($applicationCount   -gt 0) { Write-Host "  - Applications:     $applicationCount" -ForegroundColor Cyan }
if ($distributionCount  -gt 0) { Write-Host "  - DistributionLists: $distributionCount" -ForegroundColor Cyan }
if ($roleCount          -gt 0) { Write-Host "  - Roles:            $roleCount" -ForegroundColor Cyan }
Write-Host "Total memberships: $totalMemberships" -ForegroundColor Cyan
Write-Host "Leaver cohort:    $($cohortUsers.Count) users (jimLeaverCohort=TRUE)" -ForegroundColor Cyan
Write-Host ""
Write-Host "Source OpenLDAP population complete" -ForegroundColor Green
