<#
.SYNOPSIS
    Populate Samba AD with test data

.DESCRIPTION
    Creates organisational structure, users, and groups in Samba AD
    based on the specified template scale

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER Instance
    Which Samba AD instance to populate (Primary, Source, Target)

.EXAMPLE
    ./Populate-SambaAD.ps1 -Template Small -Instance Primary
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Primary", "Source", "Target")]
    [string]$Instance = "Primary"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Populating Samba AD ($Instance) with $Template template"

# Get scale for template
$scale = Get-TemplateScale -Template $Template

# Container and domain mapping
$containerMap = @{
    Primary = @{
        Container = "samba-ad-primary"
        Domain = "SUBATOMIC"
        DomainDN = "DC=subatomic,DC=local"
    }
    Source = @{
        Container = "samba-ad-source"
        Domain = "SOURCEDOMAIN"
        DomainDN = "DC=sourcedomain,DC=local"
    }
    Target = @{
        Container = "samba-ad-target"
        Domain = "TARGETDOMAIN"
        DomainDN = "DC=targetdomain,DC=local"
    }
}

$config = $containerMap[$Instance]
$container = $config.Container
$domain = $config.Domain
$domainDN = $config.DomainDN

Write-Host "Container: $container" -ForegroundColor Gray
Write-Host "Domain: $domain" -ForegroundColor Gray
Write-Host "Users to create: $($scale.Users)" -ForegroundColor Gray
Write-Host "Groups to create: $($scale.Groups)" -ForegroundColor Gray

# Create OUs
Write-TestStep "Step 1" "Creating organisational units"

# Base OUs for test users and groups
$baseOus = @("TestUsers", "TestGroups")
foreach ($ou in $baseOus) {
    Write-Host "  Creating OU: $ou" -ForegroundColor Gray

    $result = docker exec $container samba-tool ou create "OU=$ou,$domainDN" 2>&1
    if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
        Write-Host "    Warning: Failed to create OU $ou : $result" -ForegroundColor Yellow
    }
    else {
        Write-Host "    ✓ OU created: $ou" -ForegroundColor Green
    }
}

# Create the Corp base OU - this is the OU that will be selected in JIM for partition/container testing
# Structure: OU=Corp,DC=subatomic,DC=local
#   - OU=Users,OU=Corp,DC=subatomic,DC=local  (for user objects)
#   - OU=Groups,OU=Corp,DC=subatomic,DC=local (for group objects)
Write-Host "  Creating Corp base OU..." -ForegroundColor Gray
$result = docker exec $container samba-tool ou create "OU=Corp,$domainDN" 2>&1
if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
    Write-Host "    Warning: Failed to create OU 'Corp': $result" -ForegroundColor Yellow
}
else {
    Write-Host "    ✓ OU created: Corp" -ForegroundColor Green
}

# Create the Users OU under Corp
Write-Host "  Creating Users OU under Corp..." -ForegroundColor Gray
$result = docker exec $container samba-tool ou create "OU=Users,OU=Corp,$domainDN" 2>&1
if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
    Write-Host "    Warning: Failed to create OU 'Users': $result" -ForegroundColor Yellow
}
else {
    Write-Host "    ✓ OU created: Users (under Corp)" -ForegroundColor Green
}

# Create the Groups OU under Corp
Write-Host "  Creating Groups OU under Corp..." -ForegroundColor Gray
$result = docker exec $container samba-tool ou create "OU=Groups,OU=Corp,$domainDN" 2>&1
if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
    Write-Host "    Warning: Failed to create OU 'Groups': $result" -ForegroundColor Yellow
}
else {
    Write-Host "    ✓ OU created: Groups (under Corp)" -ForegroundColor Green
}

# Note: Department OUs (Finance, IT, Marketing, etc.) can be auto-created under OU=Users,OU=Corp
# by the LDAP Connector's "Create containers as needed?" setting when provisioning users.
# This tests the container creation functionality and matches real-world use cases
# where department OUs may not exist initially.

# Legacy department OUs at root level (kept for backward compatibility with any existing tests)
# These will be removed in a future cleanup once all tests use the /Corp/Users/{Department} structure
$departmentOus = @("Marketing", "Operations", "Finance", "Sales", "Human Resources", "Procurement",
                   "Information Technology", "Research & Development", "Executive", "Legal", "Facilities", "Catering")
Write-Host "  Creating legacy department OUs at root level..." -ForegroundColor Gray
foreach ($deptOu in $departmentOus) {
    $result = docker exec $container samba-tool ou create "OU=$deptOu,$domainDN" 2>&1
    if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
        Write-Host "    Warning: Failed to create OU $deptOu : $result" -ForegroundColor Yellow
    }
    else {
        Write-Host "    ✓ OU created: $deptOu" -ForegroundColor Green
    }
}

# Create users via LDIF bulk import (much faster than individual samba-tool calls)
Write-TestStep "Step 2" "Creating $($scale.Users) users via LDIF bulk import"

$departments = @("IT", "HR", "Sales", "Finance", "Operations", "Marketing", "Legal", "Engineering", "Support", "Admin")
$titles = @("Manager", "Director", "Analyst", "Specialist", "Coordinator", "Administrator", "Engineer", "Developer", "Consultant", "Associate")

$createdUsers = @()
$usersWithExpiry = 0
$usersOU = "OU=TestUsers,$domainDN"

# Windows FILETIME epoch: January 1, 1601 00:00:00 UTC
$fileTimeEpoch = [DateTime]::new(1601, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)

$ldifBuilder = [System.Text.StringBuilder]::new()

for ($i = 1; $i -lt $scale.Users + 1; $i++) {
    $user = New-TestUser -Index $i -Domain ($domain.ToLower() + ".local")

    # Build DN directly in TestUsers OU (no need for create-then-move)
    $dn = "CN=$($user.DisplayName),$usersOU"

    [void]$ldifBuilder.AppendLine("dn: $dn")
    [void]$ldifBuilder.AppendLine("objectClass: top")
    [void]$ldifBuilder.AppendLine("objectClass: person")
    [void]$ldifBuilder.AppendLine("objectClass: organizationalPerson")
    [void]$ldifBuilder.AppendLine("objectClass: user")
    [void]$ldifBuilder.AppendLine("cn: $($user.DisplayName)")
    [void]$ldifBuilder.AppendLine("sn: $($user.LastName)")
    [void]$ldifBuilder.AppendLine("givenName: $($user.FirstName)")
    [void]$ldifBuilder.AppendLine("sAMAccountName: $($user.SamAccountName)")
    [void]$ldifBuilder.AppendLine("displayName: $($user.DisplayName)")
    [void]$ldifBuilder.AppendLine("userPrincipalName: $($user.Email)")
    [void]$ldifBuilder.AppendLine("mail: $($user.Email)")
    [void]$ldifBuilder.AppendLine("department: $($user.Department)")
    [void]$ldifBuilder.AppendLine("title: $($user.Title)")

    # Set accountExpires directly in LDIF (Windows FILETIME format)
    if ($null -ne $user.AccountExpires) {
        $fileTime = ($user.AccountExpires.ToUniversalTime() - $fileTimeEpoch).Ticks
        [void]$ldifBuilder.AppendLine("accountExpires: $fileTime")
        $usersWithExpiry++
    }

    [void]$ldifBuilder.AppendLine("")

    $createdUsers += $user

    if (($i % 1000) -eq 0) {
        Write-Host "    Prepared $i / $($scale.Users) users for import..." -ForegroundColor Gray
    }
}

# Write LDIF to temp file and import via ldbadd
$ldifPath = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($ldifPath, $ldifBuilder.ToString())
    $ldifSizeKB = [Math]::Round((Get-Item $ldifPath).Length / 1024, 1)
    Write-Host "    LDIF file: $ldifSizeKB KB for $($scale.Users) users" -ForegroundColor Gray

    # Copy LDIF into container and import
    docker cp $ldifPath "${container}:/tmp/users.ldif" 2>&1 | Out-Null
    $result = docker exec $container ldbadd -H /usr/local/samba/private/sam.ldb /tmp/users.ldif 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host "    Warning: LDIF import returned errors: $result" -ForegroundColor Yellow
        # Check if some users already exist (partial import)
        if ($result -match "already exists") {
            Write-Host "    Some users already exist (idempotent)" -ForegroundColor Gray
        }
    }

    # Clean up container temp file
    docker exec $container rm -f /tmp/users.ldif 2>&1 | Out-Null
}
finally {
    Remove-Item $ldifPath -Force -ErrorAction SilentlyContinue
}

Write-Host "  ✓ Imported $($createdUsers.Count) users ($usersWithExpiry with account expiry)" -ForegroundColor Green

# Create groups via LDIF bulk import
Write-TestStep "Step 3" "Creating $($scale.Groups) groups via LDIF bulk import"

$createdGroups = @()
$groupsOU = "OU=TestGroups,$domainDN"

$groupLdifBuilder = [System.Text.StringBuilder]::new()

for ($i = 1; $i -lt $scale.Groups + 1; $i++) {
    $description = "Test group $i for integration testing"

    # Assign department-based groups
    $dept = $departments[$i % $departments.Length]
    $groupName = "Group-$dept-$i"

    # Build DN directly in TestGroups OU (no need for create-then-move)
    $dn = "CN=$groupName,$groupsOU"

    # Security Global group type (-2147483646)
    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: top")
    [void]$groupLdifBuilder.AppendLine("objectClass: group")
    [void]$groupLdifBuilder.AppendLine("cn: $groupName")
    [void]$groupLdifBuilder.AppendLine("sAMAccountName: $groupName")
    [void]$groupLdifBuilder.AppendLine("groupType: -2147483646")
    [void]$groupLdifBuilder.AppendLine("description: $description")
    [void]$groupLdifBuilder.AppendLine("")

    $createdGroups += @{
        Name = $groupName
        Department = $dept
    }
}

# Write LDIF to temp file and import via ldbadd
$groupLdifPath = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($groupLdifPath, $groupLdifBuilder.ToString())
    $ldifSizeKB = [Math]::Round((Get-Item $groupLdifPath).Length / 1024, 1)
    Write-Host "    LDIF file: $ldifSizeKB KB for $($scale.Groups) groups" -ForegroundColor Gray

    # Copy LDIF into container and import
    docker cp $groupLdifPath "${container}:/tmp/groups.ldif" 2>&1 | Out-Null
    $result = docker exec $container ldbadd -H /usr/local/samba/private/sam.ldb /tmp/groups.ldif 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host "    Warning: LDIF import returned errors: $result" -ForegroundColor Yellow
        if ($result -match "already exists") {
            Write-Host "    Some groups already exist (idempotent)" -ForegroundColor Gray
        }
    }

    # Clean up container temp file
    docker exec $container rm -f /tmp/groups.ldif 2>&1 | Out-Null
}
finally {
    Remove-Item $groupLdifPath -Force -ErrorAction SilentlyContinue
}

Write-Host "  ✓ Imported $($createdGroups.Count) groups" -ForegroundColor Green

# Add users to groups
Write-TestStep "Step 4" "Adding users to groups (avg: $($scale.AvgMemberships) memberships/user)"

# OPTIMISATION: Collect all memberships first, then batch by group
# This reduces Docker exec calls from O(users × groups) to O(groups)
$groupMemberships = @{}

foreach ($user in $createdUsers) {
    # Match users to groups by department
    $deptGroups = @($createdGroups | Where-Object { $_.Department -eq $user.Department })

    if ($deptGroups.Count -gt 0) {
        # Add to some department groups (randomised)
        $numGroups = [Math]::Min($scale.AvgMemberships, $deptGroups.Count)
        $selectedGroups = Get-RandomSubset -Items $deptGroups -Count $numGroups

        foreach ($group in $selectedGroups) {
            if (-not $groupMemberships.ContainsKey($group.Name)) {
                $groupMemberships[$group.Name] = @()
            }
            $groupMemberships[$group.Name] += $user.SamAccountName
        }
    }
}

# Now add all members to each group in a single batch operation
$totalMemberships = 0
$processedGroups = 0

foreach ($groupName in $groupMemberships.Keys) {
    $members = $groupMemberships[$groupName]
    # Build comma-separated member list (samba-tool requires commas, not spaces)
    $memberList = $members -join ','

    $result = docker exec $container samba-tool group addmembers `
        $groupName `
        $memberList 2>&1

    if ($LASTEXITCODE -eq 0 -or $result -match "already a member") {
        $totalMemberships += $members.Count
    }
    else {
        Write-Warning "Failed to add members to group ${groupName}: $result"
    }

    $processedGroups++
    if (($processedGroups % 10) -eq 0) {
        Write-Host "    Processed $processedGroups groups, $totalMemberships memberships..." -ForegroundColor Gray
    }
}

Write-Host "  ✓ Added $totalMemberships group memberships across $($groupMemberships.Count) groups" -ForegroundColor Green

# Summary
Write-TestSection "Population Summary"
Write-Host "Instance:       $Instance" -ForegroundColor Cyan
Write-Host "Template:       $Template" -ForegroundColor Cyan
Write-Host "Users:          $($createdUsers.Count) ($usersWithExpiry with expiry)" -ForegroundColor Cyan
Write-Host "Groups:         $($createdGroups.Count)" -ForegroundColor Cyan
Write-Host "Memberships:    $totalMemberships" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ Samba AD population complete" -ForegroundColor Green

# Exit with success - idempotent operation
exit 0
