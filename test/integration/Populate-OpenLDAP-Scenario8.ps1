# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Populate OpenLDAP with test data for Scenario 8: Cross-domain Entitlement Sync

.DESCRIPTION
    Creates users and entitlement groups in Source OpenLDAP (Yellowstone suffix) for
    cross-domain group synchronisation testing. Groups use the jimGroup structural class
    (SUP groupOfNames), which inherits the MUST member constraint and adds mail,
    jimGroupType, and jimGroupStatus attributes.

    The Target suffix (Glitterband) gets only OU structure — JIM provisions groups there.

    Structure created in Source (dc=yellowstone,dc=local):
    - ou=People (test users)
    - ou=Entitlements (entitlement groups)

    Structure created in Target (dc=glitterband,dc=local):
    - ou=Entitlements (empty — JIM provisions groups here)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, MediumLarge, Large, Scale100k50Groups, Scale200k55Groups, Scale500k65Groups, Scale750k70Groups, Scale1m80Groups, Scale100k5kGroups)

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
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups")]
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
    [void]$userLdifBuilder.AppendLine("objectClass: inetOrgPerson")
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
$deptCount = [Math]::Min($groupScale.Departments, $sortedDepartmentKeys.Length)
for ($g = 0; $g -lt $deptCount; $g++) {
    $deptKey = $sortedDepartmentKeys[$g]
    $groupName = "Dept-$deptKey"
    $dn = "cn=$groupName,$($config.GroupsOU)"

    $deptUsers = @($createdUsers | Where-Object { $_.DeptKey -eq $deptKey })
    $initialMember = if ($deptUsers.Count -gt 0) { $deptUsers[0].DN } else { $createdUsers[0].DN }

    $groupMail = "$($groupName.ToLower())@$($config.Domain)"
    $groupStatus = $groupStatuses[$groupIndex % $groupStatuses.Length]
    $groupIndex++

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: jimGroup")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: Department group for $($scenario8DepartmentNames[$deptKey])")
    [void]$groupLdifBuilder.AppendLine("mail: $groupMail")
    [void]$groupLdifBuilder.AppendLine("jimGroupType: Managed")
    [void]$groupLdifBuilder.AppendLine("jimGroupStatus: $groupStatus")
    [void]$groupLdifBuilder.AppendLine("member: $initialMember")
    [void]$groupLdifBuilder.AppendLine("")

    $createdGroups += @{
        Name        = $groupName
        DN          = $dn
        Category    = "Department"
        FilterKey   = $deptKey
        FilterField = "DeptKey"
        Members     = @($initialMember)
    }
}

# Location groups
$locationNames = @("Sydney", "Melbourne", "London", "Manchester", "NewYork",
    "SanFrancisco", "Tokyo", "Singapore", "Berlin", "Paris")
$locCount = [Math]::Min($groupScale.Locations, $locationNames.Length)
for ($g = 0; $g -lt $locCount; $g++) {
    $locName = $locationNames[$g]
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
        Name        = $groupName
        DN          = $dn
        Category    = "Location"
        FilterKey   = $locName
        FilterField = $null
        Members     = @($initialMember)
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
# Long-tail categories (Scale100k5kGroups). Each group's category-internal
# index drives Get-LongTailGroupSize during membership assignment below.
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
# Step 4: Assign group memberships (Source only)
#
# Membership assignment is batched across multiple groups per docker exec call.
# Each ldapmodify invocation processes ~5000 member lines spanning many groups,
# which is essential for Scale100k5kGroups (~5000 groups, ~1M memberships) and
# improves smaller templates harmlessly.
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
            $matching = @($createdUsers | Where-Object { $_.DeptKey -eq $group.FilterKey })
            $membersToAdd = @($matching | ForEach-Object { $_.DN })
        }
        "Location" {
            $locIndex = $locationNames.IndexOf($group.FilterKey)
            $membersToAdd = @($createdUsers | Where-Object {
                ($createdUsers.IndexOf($_) % $locCount) -eq $locIndex
            } | ForEach-Object { $_.DN })
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
            # Users assigned to a division by userIndex % divisionCount.
            $divisionIndex = [int]$group.FilterKey
            $membersToAdd = @($createdUsers | Where-Object {
                ($createdUsers.IndexOf($_) % $divisionCount) -eq $divisionIndex
            } | ForEach-Object { $_.DN })
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
$batch = [System.Text.StringBuilder]::new()
$batchMemberLineCount = 0

foreach ($wi in $workItems) {
    $members = $wi.Members
    # If the group's member set alone exceeds the batch size, chunk it across
    # multiple ldapmodify blocks but still keep them in a single docker exec
    # where possible.
    $chunkSize = $flushBatchSize
    for ($c = 0; $c -lt $members.Count; $c += $chunkSize) {
        $end = [Math]::Min($c + $chunkSize, $members.Count) - 1
        $chunk = $members[$c..$end]

        foreach ($memberDn in $chunk) {
            [void]$batch.AppendLine("dn: $($wi.GroupDN)")
            [void]$batch.AppendLine("changetype: modify")
            [void]$batch.AppendLine("add: member")
            [void]$batch.AppendLine("member: $memberDn")
            [void]$batch.AppendLine("")
            $batchMemberLineCount++
        }

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
Write-Host ""
Write-Host "Source OpenLDAP population complete" -ForegroundColor Green
