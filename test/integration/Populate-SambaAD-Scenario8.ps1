<#
.SYNOPSIS
    Populate Samba AD with test data for Scenario 8: Cross-domain Entitlement Sync

.DESCRIPTION
    Creates users and entitlement groups in Source AD for cross-domain group synchronisation.
    This script is self-contained - it creates its own users and groups in the Source AD,
    which will then be synced to the Target AD by JIM.

    Structure created:
    - OU=Corp,DC=sourcedomain,DC=local
      - OU=Users (test users)
      - OU=Entitlements (entitlement groups)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER Instance
    Which Samba AD instance to populate (Source or Target)
    - Source: Populates users and groups
    - Target: Only creates OU structure (groups will be provisioned by JIM)

.EXAMPLE
    ./Populate-SambaAD-Scenario8.ps1 -Template Nano -Instance Source

.EXAMPLE
    ./Populate-SambaAD-Scenario8.ps1 -Template Small -Instance Source
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Source", "Target")]
    [string]$Instance = "Source",

    [Parameter(Mandatory=$false)]
    [string]$Container = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"
. "$PSScriptRoot/utils/Test-GroupHelpers.ps1"

Write-TestSection "Scenario 8: Populating Samba AD ($Instance) with $Template template"

# Get scales
$groupScale = Get-Scenario8GroupScale -Template $Template

# Define consistent company and department lists for Scenario 8
# These must match the lists used in group creation to ensure membership filtering works
# Keys are technical names (no spaces), values are display names (with spaces)
$scenario8CompanyNames = @{
    "Subatomic" = "Subatomic"
    "NexusDynamics" = "Nexus Dynamics"
    "OrbitalSystems" = "Orbital Systems"
    "QuantumBridge" = "Quantum Bridge"
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

# Container and domain mapping
$containerMap = @{
    Source = @{
        Container = "samba-ad-source"
        Domain = "SOURCEDOMAIN"
        DomainDN = "DC=sourcedomain,DC=local"
        DomainSuffix = "sourcedomain.local"
    }
    Target = @{
        Container = "samba-ad-target"
        Domain = "TARGETDOMAIN"
        DomainDN = "DC=targetdomain,DC=local"
        DomainSuffix = "targetdomain.local"
    }
}

$config = $containerMap[$Instance]
$container = if ($Container) { $Container } else { $config.Container }
$domain = $config.Domain
$domainDN = $config.DomainDN
$domainSuffix = $config.DomainSuffix

Write-Host "Container:       $container" -ForegroundColor Gray
Write-Host "Domain:          $domain" -ForegroundColor Gray
Write-Host "Users to create: $($groupScale.Users)" -ForegroundColor Gray
Write-Host "Groups to create: $($groupScale.TotalGroups)" -ForegroundColor Gray
Write-Host "  - Companies:   $($groupScale.Companies)" -ForegroundColor Gray
Write-Host "  - Departments: $($groupScale.Departments)" -ForegroundColor Gray
Write-Host "  - Locations:   $($groupScale.Locations)" -ForegroundColor Gray
Write-Host "  - Projects:    $($groupScale.Projects)" -ForegroundColor Gray

# ============================================================================
# Step 1: Create Organisational Units
# ============================================================================
Write-TestStep "Step 1" "Creating organisational units"

# Create Corp base OU
$corpOU = "OU=Corp,$domainDN"
Write-Host "  Creating OU: Corp" -ForegroundColor Gray
$result = docker exec $container samba-tool ou create $corpOU 2>&1
if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
    Write-Host "    Warning: Failed to create OU Corp: $result" -ForegroundColor Yellow
}
else {
    Write-Host "    ✓ OU created: Corp" -ForegroundColor Green
}

# Create Users OU under Corp
$usersOU = "OU=Users,$corpOU"
Write-Host "  Creating OU: Users (under Corp)" -ForegroundColor Gray
$result = docker exec $container samba-tool ou create $usersOU 2>&1
if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
    Write-Host "    Warning: Failed to create OU Users: $result" -ForegroundColor Yellow
}
else {
    Write-Host "    ✓ OU created: Users" -ForegroundColor Green
}

# Create Entitlements OU under Corp
$entitlementsOU = "OU=Entitlements,$corpOU"
Write-Host "  Creating OU: Entitlements (under Corp)" -ForegroundColor Gray
$result = docker exec $container samba-tool ou create $entitlementsOU 2>&1
if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
    Write-Host "    Warning: Failed to create OU Entitlements: $result" -ForegroundColor Yellow
}
else {
    Write-Host "    ✓ OU created: Entitlements" -ForegroundColor Green
}

# For Target instance, we only create the OU structure (JIM will provision the rest)
if ($Instance -eq "Target") {
    # Also create CorpManaged structure for target
    $corpManagedOU = "OU=CorpManaged,$domainDN"
    Write-Host "  Creating OU: CorpManaged" -ForegroundColor Gray
    $result = docker exec $container samba-tool ou create $corpManagedOU 2>&1
    if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
        Write-Host "    Warning: Failed to create OU CorpManaged: $result" -ForegroundColor Yellow
    }
    else {
        Write-Host "    ✓ OU created: CorpManaged" -ForegroundColor Green
    }

    $targetUsersOU = "OU=Users,$corpManagedOU"
    Write-Host "  Creating OU: Users (under CorpManaged)" -ForegroundColor Gray
    $result = docker exec $container samba-tool ou create $targetUsersOU 2>&1
    if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
        Write-Host "    Warning: Failed to create OU Users: $result" -ForegroundColor Yellow
    }
    else {
        Write-Host "    ✓ OU created: Users" -ForegroundColor Green
    }

    $targetEntitlementsOU = "OU=Entitlements,$corpManagedOU"
    Write-Host "  Creating OU: Entitlements (under CorpManaged)" -ForegroundColor Gray
    $result = docker exec $container samba-tool ou create $targetEntitlementsOU 2>&1
    if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
        Write-Host "    Warning: Failed to create OU Entitlements: $result" -ForegroundColor Yellow
    }
    else {
        Write-Host "    ✓ OU created: Entitlements" -ForegroundColor Green
    }

    Write-TestSection "Target Population Complete"
    Write-Host "Template:       $Template" -ForegroundColor Cyan
    Write-Host "OU structure created - JIM will provision users and groups" -ForegroundColor Gray
    Write-Host ""
    Write-Host "✓ Target AD population complete (OU structure only)" -ForegroundColor Green
    exit 0
}

# ============================================================================
# Step 2: Create Users (Source only) via LDIF bulk import
# ============================================================================
Write-TestStep "Step 2" "Creating $($groupScale.Users) users"

$createdUsers = @()
$sortedCompanyKeys = $scenario8CompanyNames.Keys | Sort-Object
$sortedDepartmentKeys = $scenario8DepartmentNames.Keys | Sort-Object

# OPTIMISATION: Generate user data in parallel across cores, then build LDIF and import in chunks
# Step 1: Generate all user data objects in parallel (CPU-bound, benefits from multiple cores)
# Step 2: Build LDIF strings and import sequentially (ldbadd is single-writer)
Write-Host "  Generating user data (parallel)..." -ForegroundColor Gray

$userGenStart = Get-Date
$indices = 0..($groupScale.Users - 1)
$sortedCompanyKeysArray = @($sortedCompanyKeys)
$sortedDepartmentKeysArray = @($sortedDepartmentKeys)
$companyCount = $scenario8CompanyNames.Count
$departmentCount = $scenario8DepartmentNames.Count

# Generate user data in parallel using ForEach-Object -Parallel
# Each parallel runspace calls New-TestUser and computes company/department assignment
$createdUsers = $indices | ForEach-Object -Parallel {
    $i = $_
    $helperPath = $using:PSScriptRoot
    . "$helperPath/utils/Test-Helpers.ps1"

    $user = New-TestUser -Index $i -Domain $using:domainSuffix

    $companyTechnicalName = ($using:sortedCompanyKeysArray)[$i % $using:companyCount]
    $departmentTechnicalName = ($using:sortedDepartmentKeysArray)[$i % $using:departmentCount]

    [PSCustomObject]@{
        Index              = $i
        SamAccountName     = $user.SamAccountName
        DisplayName        = $user.DisplayName
        FirstName          = $user.FirstName
        LastName           = $user.LastName
        Email              = $user.Email
        Title              = $user.Title
        Pronouns           = $user.Pronouns
        Department         = $departmentTechnicalName
        Company            = $companyTechnicalName
        DN                 = "CN=$($user.DisplayName),$using:usersOU"
    }
} -ThrottleLimit ([Math]::Min(8, [Environment]::ProcessorCount))

# Sort by index to ensure deterministic order for LDIF generation
$createdUsers = @($createdUsers | Sort-Object -Property Index)

$userGenDuration = ((Get-Date) - $userGenStart).TotalSeconds
Write-Host "  ✓ Generated $($createdUsers.Count) user records in $([Math]::Round($userGenDuration, 1))s" -ForegroundColor Green

# Build LDIF and import in chunks (ldbadd is single-writer, must be sequential)
Write-Host "  Importing users via ldbadd..." -ForegroundColor Gray

$ldifChunkSize = 5000
$totalAdded = 0
$ldifBuilder = [System.Text.StringBuilder]::new()
$chunkIndex = 0

for ($i = 0; $i -lt $createdUsers.Count; $i++) {
    $u = $createdUsers[$i]
    $companyDisplayName = $scenario8CompanyNames[$u.Company]
    $departmentDisplayName = $scenario8DepartmentNames[$u.Department]

    # Build LDIF entry
    [void]$ldifBuilder.AppendLine("dn: $($u.DN)")
    [void]$ldifBuilder.AppendLine("objectClass: top")
    [void]$ldifBuilder.AppendLine("objectClass: person")
    [void]$ldifBuilder.AppendLine("objectClass: organizationalPerson")
    [void]$ldifBuilder.AppendLine("objectClass: user")
    [void]$ldifBuilder.AppendLine("cn: $($u.DisplayName)")
    [void]$ldifBuilder.AppendLine("sn: $($u.LastName)")
    [void]$ldifBuilder.AppendLine("givenName: $($u.FirstName)")
    [void]$ldifBuilder.AppendLine("sAMAccountName: $($u.SamAccountName)")
    [void]$ldifBuilder.AppendLine("displayName: $($u.DisplayName)")
    [void]$ldifBuilder.AppendLine("userPrincipalName: $($u.Email)")
    [void]$ldifBuilder.AppendLine("mail: $($u.Email)")
    [void]$ldifBuilder.AppendLine("department: $departmentDisplayName")
    [void]$ldifBuilder.AppendLine("title: $($u.Title)")
    [void]$ldifBuilder.AppendLine("company: $companyDisplayName")

    if ($null -ne $u.Pronouns) {
        [void]$ldifBuilder.AppendLine("extensionAttribute1: $($u.Pronouns)")
    }

    [void]$ldifBuilder.AppendLine("")

    # Import in chunks to avoid ldbadd OOM on very large LDB databases
    if ((($i + 1) % $ldifChunkSize -eq 0) -or ($i -eq $createdUsers.Count - 1)) {
        $chunkIndex++
        $chunkCount = if (($i + 1) % $ldifChunkSize -eq 0) { $ldifChunkSize } else { ($i + 1) % $ldifChunkSize }
        $ldifPath = [System.IO.Path]::GetTempFileName()
        [System.IO.File]::WriteAllText($ldifPath, $ldifBuilder.ToString())

        Write-Host "  Importing chunk $chunkIndex ($chunkCount users, total $($i + 1)/$($createdUsers.Count))..." -ForegroundColor Gray
        docker cp $ldifPath "${container}:/tmp/users.ldif" 2>&1 | Out-Null
        $result = docker exec $container ldbadd -H /usr/local/samba/private/sam.ldb /tmp/users.ldif 2>&1
        $exitCode = $LASTEXITCODE
        docker exec $container rm -f /tmp/users.ldif 2>&1 | Out-Null
        Remove-Item $ldifPath -Force -ErrorAction SilentlyContinue

        $resultText = if ($result -is [array]) { $result -join "`n" } else { "$result" }

        if ($resultText -match "Added (\d+) records") {
            $totalAdded += [int]$Matches[1]
        }
        elseif ($resultText -match "already exists") {
            Write-Host "    ⚠ Some users in chunk already exist (idempotent)" -ForegroundColor Yellow
        }
        elseif ($exitCode -eq 0 -and [string]::IsNullOrWhiteSpace($resultText)) {
            $totalAdded += $chunkCount
            Write-Host "    ✓ Chunk $chunkIndex imported (exit code 0, no output)" -ForegroundColor Gray
        }
        else {
            throw "LDIF import failed for chunk $chunkIndex (users $($i + 1 - $chunkCount) to ${i}), exit code ${exitCode}: ${resultText}"
        }

        $ldifBuilder.Clear() | Out-Null
    }
}

Write-Host "  ✓ Created $totalAdded users via LDIF bulk import ($chunkIndex chunks)" -ForegroundColor Green

# ============================================================================
# Step 3: Create Groups (Source only) via LDIF bulk import
# ============================================================================
Write-TestStep "Step 3" "Creating $($groupScale.TotalGroups) groups"

# Generate group set
$groups = New-Scenario8GroupSet -Template $Template -Domain $domainSuffix

$createdGroups = @()

# OPTIMISATION: Generate all group LDIF in memory, then bulk import via ldbadd
# This replaces 4-7 docker exec calls per group with a single ldbadd + ldbmodify
Write-Host "  Generating group LDIF..." -ForegroundColor Gray

$groupLdifBuilder = [System.Text.StringBuilder]::new()
$groupModifyBuilder = [System.Text.StringBuilder]::new()

for ($i = 0; $i -lt $groups.Count; $i++) {
    $group = $groups[$i]

    # Format display names and descriptions for company and department groups
    $displayName = $group.DisplayName
    $description = $group.Description

    if ($group.Category -eq "Company") {
        $technicalName = $group.Name -replace "^Company-", ""
        $displayName = "Company-" + ($scenario8CompanyNames[$technicalName] -replace " ", " ")
        $description = "Company-wide group for $($scenario8CompanyNames[$technicalName])"
    }
    elseif ($group.Category -eq "Department") {
        $technicalName = $group.Name -replace "^Dept-", ""
        $displayName = "Dept-" + ($scenario8DepartmentNames[$technicalName] -replace " ", " ")
        $description = "Department group for $($scenario8DepartmentNames[$technicalName])"
    }

    $dn = "CN=$($group.CN),$entitlementsOU"

    # Build LDIF entry for group creation (ldbadd)
    [void]$groupLdifBuilder.AppendLine("dn: $dn")
    [void]$groupLdifBuilder.AppendLine("objectClass: top")
    [void]$groupLdifBuilder.AppendLine("objectClass: group")
    [void]$groupLdifBuilder.AppendLine("cn: $($group.CN)")
    [void]$groupLdifBuilder.AppendLine("sAMAccountName: $($group.SAMAccountName)")
    [void]$groupLdifBuilder.AppendLine("groupType: $($group.GroupType)")
    [void]$groupLdifBuilder.AppendLine("description: $description")
    [void]$groupLdifBuilder.AppendLine("displayName: $displayName")
    if ($group.MailEnabled -and $group.Mail) {
        [void]$groupLdifBuilder.AppendLine("mail: $($group.Mail)")
    }
    [void]$groupLdifBuilder.AppendLine("")

    # Store created group info
    $createdGroups += @{
        Name = $group.Name
        SAMAccountName = $group.SAMAccountName
        Category = $group.Category
        Type = $group.Type
        Scope = $group.Scope
        MailEnabled = $group.MailEnabled
        HasManagedBy = $group.HasManagedBy
        DN = $dn
    }
}

# Write LDIF and bulk import groups
$groupLdifPath = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllText($groupLdifPath, $groupLdifBuilder.ToString())

Write-Host "  Importing $($groups.Count) groups via ldbadd..." -ForegroundColor Gray
docker cp $groupLdifPath "${container}:/tmp/groups.ldif" 2>&1 | Out-Null
$result = docker exec $container ldbadd -H /usr/local/samba/private/sam.ldb /tmp/groups.ldif 2>&1
docker exec $container rm -f /tmp/groups.ldif 2>&1 | Out-Null
Remove-Item $groupLdifPath -Force -ErrorAction SilentlyContinue

if ($result -match "Added (\d+) records") {
    $addedCount = [int]$Matches[1]
    Write-Host "  ✓ Created $addedCount groups via LDIF bulk import" -ForegroundColor Green
}
elseif ($result -match "already exists") {
    Write-Host "  ⚠ Some groups already exist (idempotent)" -ForegroundColor Yellow
}
else {
    Write-Warning "Group LDIF import result: $result"
}

# ============================================================================
# Step 4: Assign Group Members
# ============================================================================
Write-TestStep "Step 4" "Assigning group members"

$membershipOperation = Start-TimedOperation -Name "Assigning memberships" -TotalSteps $createdGroups.Count

# Pre-compute group -> member mappings (sequential — reads shared $createdUsers)
Write-Host "  Computing group memberships..." -ForegroundColor Gray

# Build lookup tables for fast filtering
$usersByCompany = @{}
$usersByDepartment = @{}
foreach ($u in $createdUsers) {
    if (-not $usersByCompany.ContainsKey($u.Company)) { $usersByCompany[$u.Company] = [System.Collections.Generic.List[string]]::new() }
    $usersByCompany[$u.Company].Add($u.SamAccountName)
    if (-not $usersByDepartment.ContainsKey($u.Department)) { $usersByDepartment[$u.Department] = [System.Collections.Generic.List[string]]::new() }
    $usersByDepartment[$u.Department].Add($u.SamAccountName)
}

# All SamAccountNames as array for index-based access (Location/Project groups)
$allSamNames = @($createdUsers.SamAccountName)
$userCount = $createdUsers.Count

# Build work items: array of @{ GroupName; Members } for parallel execution
$membershipWorkItems = [System.Collections.Generic.List[object]]::new()

for ($g = 0; $g -lt $createdGroups.Count; $g++) {
    $group = $createdGroups[$g]
    $memberNames = @()

    switch ($group.Category) {
        "Company" {
            $companyName = $group.Name -replace "^Company-", ""
            if ($usersByCompany.ContainsKey($companyName)) {
                $memberNames = @($usersByCompany[$companyName])
            }
        }
        "Department" {
            $deptName = $group.Name -replace "^Dept-", ""
            if ($usersByDepartment.ContainsKey($deptName)) {
                $memberNames = @($usersByDepartment[$deptName])
            }
        }
        "Location" {
            # Location groups: 30-50% of users, capped at 50K (tests upper MVA range)
            $targetMembers = [Math]::Min(50000, [Math]::Max(1, [Math]::Floor($userCount * (0.3 + ($g % 5) * 0.05))))
            $offset = ($g * 7) % $userCount
            $seen = [System.Collections.Generic.HashSet[string]]::new()
            for ($u = 0; $u -lt $targetMembers; $u++) {
                $userIndex = ($offset + $u) % $userCount
                [void]$seen.Add($allSamNames[$userIndex])
            }
            $memberNames = @($seen)
        }
        "Project" {
            # Project groups: varied sizes (50 to 5000) for broad coverage
            # Distribute across size tiers: tiny(50), small(200), medium(1000), large(3000), xlarge(5000)
            $sizeTiers = @(50, 200, 500, 1000, 2000, 3000, 5000)
            $tierIndex = $g % $sizeTiers.Count
            $targetMembers = [Math]::Min($sizeTiers[$tierIndex], $userCount)
            $offset = ($g * 11) % $userCount
            $seen = [System.Collections.Generic.HashSet[string]]::new()
            for ($u = 0; $u -lt $targetMembers; $u++) {
                $userIndex = ($offset + $u) % $userCount
                [void]$seen.Add($allSamNames[$userIndex])
            }
            $memberNames = @($seen)
        }
    }

    if ($memberNames.Count -gt 0) {
        $membershipWorkItems.Add(@{
            GroupName = $group.SAMAccountName
            Members  = $memberNames
        })
    }
}

Write-Host "  ✓ Computed memberships for $($membershipWorkItems.Count) groups" -ForegroundColor Green

# Execute membership assignments sequentially (samba-tool holds an LDB write lock,
# so parallel docker exec calls serialise anyway — sequential is simpler)
Write-Host "  Assigning memberships ($($membershipWorkItems.Count) groups)..." -ForegroundColor Gray

$chunkSize = 500
$totalMemberships = 0

for ($w = 0; $w -lt $membershipWorkItems.Count; $w++) {
    $item = $membershipWorkItems[$w]
    $groupName = $item.GroupName
    $members = $item.Members

    for ($c = 0; $c -lt $members.Count; $c += $chunkSize) {
        $end = [Math]::Min($c + $chunkSize, $members.Count) - 1
        $chunk = $members[$c..$end]
        $memberList = $chunk -join ','
        $result = docker exec $container samba-tool group addmembers `
            $groupName `
            $memberList 2>&1

        if ($LASTEXITCODE -eq 0 -or "$result" -match "already a member") {
            $totalMemberships += $chunk.Count
        }
        else {
            Write-Warning "Failed to add members to group ${groupName}: ${result}"
            break
        }
    }

    if (($w + 1) % 100 -eq 0) {
        Write-Host "    Progress: $($w + 1)/$($membershipWorkItems.Count) groups..." -ForegroundColor Gray
    }
}

Write-Host "  ✓ Assigned $totalMemberships memberships across $($membershipWorkItems.Count) groups" -ForegroundColor Green

Complete-TimedOperation -Operation $membershipOperation -Message "Assigned $totalMemberships memberships"

# ============================================================================
# Step 5: Assign managedBy (batched into single ldbmodify call)
# ============================================================================
Write-TestStep "Step 5" "Assigning group owners (managedBy)"

$managedByOperation = Start-TimedOperation -Name "Assigning managedBy" -TotalSteps 1

# Find users with Manager or Director titles for group ownership
$managers = @($createdUsers | Where-Object { $_.Title -match "Manager|Director" })
if ($managers.Count -eq 0) {
    $managers = @($createdUsers)  # Fallback to any user
}

# Build a single LDIF modify file for all managedBy assignments
$modifyBuilder = [System.Text.StringBuilder]::new()
$managedByCount = 0

for ($g = 0; $g -lt $createdGroups.Count; $g++) {
    $group = $createdGroups[$g]
    if ($group.HasManagedBy) {
        $managerIndex = $g % $managers.Count
        $manager = $managers[$managerIndex]

        if ($managedByCount -gt 0) {
            # Separate entries with a blank line
            [void]$modifyBuilder.AppendLine("")
        }
        [void]$modifyBuilder.AppendLine("dn: $($group.DN)")
        [void]$modifyBuilder.AppendLine("changetype: modify")
        [void]$modifyBuilder.AppendLine("replace: managedBy")
        [void]$modifyBuilder.AppendLine("managedBy: $($manager.DN)")
        $managedByCount++
    }
}

if ($managedByCount -gt 0) {
    Write-Host "  Applying $managedByCount managedBy assignments via ldbmodify..." -ForegroundColor Gray
    $modifyPath = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($modifyPath, $modifyBuilder.ToString())

    docker cp $modifyPath "${container}:/tmp/managedby.ldif" 2>&1 | Out-Null
    $result = docker exec $container ldbmodify -H /usr/local/samba/private/sam.ldb /tmp/managedby.ldif 2>&1
    $exitCode = $LASTEXITCODE
    docker exec $container rm -f /tmp/managedby.ldif 2>&1 | Out-Null
    Remove-Item $modifyPath -Force -ErrorAction SilentlyContinue

    $resultText = if ($result -is [array]) { $result -join "`n" } else { "$result" }

    if ($resultText -match "Modified (\d+) records") {
        $modifiedCount = [int]$Matches[1]
        Write-Host "  ✓ Set managedBy on $modifiedCount groups via ldbmodify" -ForegroundColor Green
    }
    elseif ($exitCode -eq 0) {
        Write-Host "  ✓ Set managedBy on $managedByCount groups via ldbmodify" -ForegroundColor Green
    }
    else {
        Write-Warning "managedBy ldbmodify returned exit code ${exitCode}: ${resultText}"
    }
}

Complete-TimedOperation -Operation $managedByOperation -Message "Assigned $managedByCount group owners"

# ============================================================================
# Summary
# ============================================================================
Write-TestSection "Population Summary"
Write-Host "Instance:         $Instance" -ForegroundColor Cyan
Write-Host "Template:         $Template" -ForegroundColor Cyan
Write-Host "Users:            $($createdUsers.Count)" -ForegroundColor Cyan
Write-Host "Groups:           $($createdGroups.Count)" -ForegroundColor Cyan

# Group breakdown by category
$companyCnt = @($createdGroups | Where-Object { $_.Category -eq "Company" }).Count
$deptCnt = @($createdGroups | Where-Object { $_.Category -eq "Department" }).Count
$locCnt = @($createdGroups | Where-Object { $_.Category -eq "Location" }).Count
$projCnt = @($createdGroups | Where-Object { $_.Category -eq "Project" }).Count
Write-Host "  - Companies:    $companyCnt" -ForegroundColor Gray
Write-Host "  - Departments:  $deptCnt" -ForegroundColor Gray
Write-Host "  - Locations:    $locCnt" -ForegroundColor Gray
Write-Host "  - Projects:     $projCnt" -ForegroundColor Gray

# Group type breakdown
$secUnivMail = @($createdGroups | Where-Object { $_.Type -eq "Security" -and $_.Scope -eq "Universal" -and $_.MailEnabled }).Count
$secUnivNoMail = @($createdGroups | Where-Object { $_.Type -eq "Security" -and $_.Scope -eq "Universal" -and -not $_.MailEnabled }).Count
$secGlobal = @($createdGroups | Where-Object { $_.Type -eq "Security" -and $_.Scope -eq "Global" }).Count
$secDomLocal = @($createdGroups | Where-Object { $_.Type -eq "Security" -and $_.Scope -eq "DomainLocal" }).Count
$distUniv = @($createdGroups | Where-Object { $_.Type -eq "Distribution" -and $_.Scope -eq "Universal" }).Count
$distGlobal = @($createdGroups | Where-Object { $_.Type -eq "Distribution" -and $_.Scope -eq "Global" }).Count
Write-Host "Group Types:" -ForegroundColor Yellow
Write-Host "  - Security Universal (mail):    $secUnivMail" -ForegroundColor Gray
Write-Host "  - Security Universal (no mail): $secUnivNoMail" -ForegroundColor Gray
Write-Host "  - Security Global:              $secGlobal" -ForegroundColor Gray
Write-Host "  - Security Domain Local:        $secDomLocal" -ForegroundColor Gray
Write-Host "  - Distribution Universal:       $distUniv" -ForegroundColor Gray
Write-Host "  - Distribution Global:          $distGlobal" -ForegroundColor Gray

Write-Host "Memberships:      $totalMemberships" -ForegroundColor Cyan
Write-Host "Groups with managedBy: $managedByCount" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ Samba AD population complete" -ForegroundColor Green

exit 0
