<#
.SYNOPSIS
    Populate OpenLDAP with test data for Scenario 8: Cross-domain Entitlement Sync

.DESCRIPTION
    Creates users and entitlement groups in Source OpenLDAP (Yellowstone suffix) for
    cross-domain group synchronisation testing. Groups use the groupOfNames object class,
    which requires at least one member (RFC 4519 MUST constraint).

    The Target suffix (Glitterband) gets only OU structure — JIM provisions groups there.

    Structure created in Source (dc=yellowstone,dc=local):
    - ou=People (test users)
    - ou=Entitlements (entitlement groups)

    Structure created in Target (dc=glitterband,dc=local):
    - ou=Entitlements (empty — JIM provisions groups here)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

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
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
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
    [void]$userLdifBuilder.AppendLine("employeeNumber: S8-$i")
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

# Company groups
$companyCount = [Math]::Min($groupScale.Companies, $sortedCompanyKeys.Length)
for ($g = 0; $g -lt $companyCount; $g++) {
    $companyKey = $sortedCompanyKeys[$g]
    $groupName = "Company-$companyKey"
    $dn = "cn=$groupName,$($config.GroupsOU)"

    # Find first user in this company for initial member (groupOfNames MUST constraint)
    $companyUsers = @($createdUsers | Where-Object { $_.CompanyKey -eq $companyKey })
    $initialMember = if ($companyUsers.Count -gt 0) { $companyUsers[0].DN } else { $createdUsers[0].DN }

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: groupOfNames")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: Company group for $($scenario8CompanyNames[$companyKey])")
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

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: groupOfNames")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: Department group for $($scenario8DepartmentNames[$deptKey])")
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

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: groupOfNames")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: Location group for $locName")
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
$projectNames = Get-ProjectNames -Count $groupScale.Projects
for ($g = 0; $g -lt $groupScale.Projects; $g++) {
    $projectName = $projectNames[$g]
    $groupName = "Project-$projectName"
    $dn = "cn=$groupName,$($config.GroupsOU)"

    $initialMember = $createdUsers[$g % $createdUsers.Count].DN

    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: groupOfNames")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("description: Project group for $projectName")
    [void]$groupLdifBuilder.AppendLine("member: $initialMember")
    [void]$groupLdifBuilder.AppendLine("")

    $createdGroups += @{
        Name        = $groupName
        DN          = $dn
        Category    = "Project"
        FilterKey   = $projectName
        FilterField = $null
        Members     = @($initialMember)
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
# ============================================================================
Write-TestStep "Step 4" "Assigning group memberships"

$totalMemberships = 0

foreach ($group in $createdGroups) {
    $membersToAdd = @()

    if ($group.FilterField -and $group.FilterKey) {
        # Company/Department groups — add all matching users
        $matchingUsers = @($createdUsers | Where-Object { $_[$group.FilterField] -eq $group.FilterKey })
        $membersToAdd = @($matchingUsers | ForEach-Object { $_.DN })
    }
    elseif ($group.Category -eq "Location") {
        # Location groups — assign 30-50% of users based on index
        $locIndex = $locationNames.IndexOf($group.FilterKey)
        $membersToAdd = @($createdUsers | Where-Object {
            ($createdUsers.IndexOf($_) % $locCount) -eq $locIndex
        } | ForEach-Object { $_.DN })
    }
    elseif ($group.Category -eq "Project") {
        # Project groups — assign varied sizes (use modular selection)
        $projectIndex = $createdGroups.IndexOf($group)
        $memberCount = [Math]::Max(1, [Math]::Min($createdUsers.Count, ($projectIndex + 1) * 2))
        $membersToAdd = @($createdUsers | Select-Object -First $memberCount | ForEach-Object { $_.DN })
    }

    # Remove the initial member (already in the group from creation)
    $existingMembers = $group.Members
    $newMembers = @($membersToAdd | Where-Object { $_ -notin $existingMembers })

    if ($newMembers.Count -eq 0) {
        continue
    }

    # Add members via ldapmodify in chunks
    $chunkSize = 500
    for ($c = 0; $c -lt $newMembers.Count; $c += $chunkSize) {
        $chunk = @($newMembers | Select-Object -Skip $c -First $chunkSize)
        $modifyLdif = [System.Text.StringBuilder]::new()

        foreach ($memberDn in $chunk) {
            [void]$modifyLdif.AppendLine("dn: $($group.DN)")
            [void]$modifyLdif.AppendLine("changetype: modify")
            [void]$modifyLdif.AppendLine("add: member")
            [void]$modifyLdif.AppendLine("member: $memberDn")
            [void]$modifyLdif.AppendLine("")
        }

        $modLdifPath = [System.IO.Path]::GetTempFileName()
        Set-Content -Path $modLdifPath -Value $modifyLdif.ToString() -NoNewline

        try {
            $result = bash -c "cat '$modLdifPath' | docker exec -i $containerName ldapmodify -x -H $ldapUri -D '$($config.AdminDN)' -w '$($config.Password)' -c" 2>&1
            if ($LASTEXITCODE -ne 0 -and "$result" -notmatch "already exists" -and "$result" -notmatch "Type or value exists") {
                Write-Verbose "  Warning adding members to $($group.Name): $result"
            }
        }
        finally {
            Remove-Item -Path $modLdifPath -Force -ErrorAction SilentlyContinue
        }

        $totalMemberships += $chunk.Count
    }

    Write-Verbose "  $($group.Name): +$($newMembers.Count) members (total: $($existingMembers.Count + $newMembers.Count))"
}

Write-Host "  Assigned $totalMemberships memberships across $($createdGroups.Count) groups" -ForegroundColor Green

# ============================================================================
# Summary
# ============================================================================
Write-TestSection "Source Population Complete"
Write-Host "Template:         $Template" -ForegroundColor Cyan
Write-Host "Users created:    $($createdUsers.Count)" -ForegroundColor Cyan
Write-Host "Groups created:   $($createdGroups.Count)" -ForegroundColor Cyan
Write-Host "  - Companies:    $companyCount" -ForegroundColor Cyan
Write-Host "  - Departments:  $deptCount" -ForegroundColor Cyan
Write-Host "  - Locations:    $locCount" -ForegroundColor Cyan
Write-Host "  - Projects:     $($groupScale.Projects)" -ForegroundColor Cyan
Write-Host "Total memberships: $totalMemberships" -ForegroundColor Cyan
Write-Host ""
Write-Host "Source OpenLDAP population complete" -ForegroundColor Green
