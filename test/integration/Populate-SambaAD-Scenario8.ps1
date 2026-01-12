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
    [string]$Instance = "Source"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"
. "$PSScriptRoot/utils/Test-GroupHelpers.ps1"

Write-TestSection "Scenario 8: Populating Samba AD ($Instance) with $Template template"

# Get scales
$groupScale = Get-Scenario8GroupScale -Template $Template
$userScale = Get-TemplateScale -Template $Template

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
$container = $config.Container
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
# Step 2: Create Users (Source only)
# ============================================================================
Write-TestStep "Step 2" "Creating $($groupScale.Users) users"

$createdUsers = @()

$userOperation = Start-TimedOperation -Name "Creating users" -TotalSteps $groupScale.Users

for ($i = 0; $i -lt $groupScale.Users; $i++) {
    $user = New-TestUser -Index $i -Domain $domainSuffix

    $userPrincipalName = "$($user.SamAccountName)@$domainSuffix"

    # Override company and department with Scenario 8 consistent values
    # This ensures membership filtering works correctly in group assignments
    # Use technical names (keys) for filtering, display names (values) for LDAP attributes
    $companyTechnicalName = ($scenario8CompanyNames.Keys | Sort-Object)[$i % $scenario8CompanyNames.Count]
    $departmentTechnicalName = ($scenario8DepartmentNames.Keys | Sort-Object)[$i % $scenario8DepartmentNames.Count]

    $companyDisplayName = $scenario8CompanyNames[$companyTechnicalName]
    $departmentDisplayName = $scenario8DepartmentNames[$departmentTechnicalName]

    # Create user with samba-tool directly in the Users OU
    # LDAP stores display names (with spaces)
    $result = docker exec $container samba-tool user create `
        $user.SamAccountName `
        "Password123!" `
        --userou="OU=Users,OU=Corp" `
        --given-name="$($user.FirstName)" `
        --surname="$($user.LastName)" `
        --mail-address="$($user.Email)" `
        --department="$departmentDisplayName" `
        --job-title="$($user.Title)" `
        --company="$companyDisplayName" 2>&1

    if ($LASTEXITCODE -eq 0) {
        # Store technical name for filtering, display name for tracking
        $createdUsers += @{
            SamAccountName = $user.SamAccountName
            DisplayName = $user.DisplayName
            Department = $departmentTechnicalName
            DepartmentDisplay = $departmentDisplayName
            Title = $user.Title
            Company = $companyTechnicalName
            CompanyDisplay = $companyDisplayName
            DN = "CN=$($user.DisplayName),$usersOU"
        }
    }
    elseif ($result -match "already exists") {
        # User already exists - add to list for membership processing
        $createdUsers += @{
            SamAccountName = $user.SamAccountName
            DisplayName = $user.DisplayName
            Department = $departmentTechnicalName
            DepartmentDisplay = $departmentDisplayName
            Title = $user.Title
            Company = $companyTechnicalName
            CompanyDisplay = $companyDisplayName
            DN = "CN=$($user.DisplayName),$usersOU"
        }
    }
    else {
        Write-Verbose "Failed to create user $($user.SamAccountName): $result"
    }

    if (($i % 10) -eq 0 -or $i -eq ($groupScale.Users - 1)) {
        Update-OperationProgress -Operation $userOperation -CurrentStep ($i + 1) -Status "$($i + 1)/$($groupScale.Users) users"
    }
}

Complete-TimedOperation -Operation $userOperation -Message "Created $($createdUsers.Count) users"

# ============================================================================
# Step 3: Create Groups (Source only)
# ============================================================================
Write-TestStep "Step 3" "Creating $($groupScale.TotalGroups) groups"

# Generate group set
$groups = New-Scenario8GroupSet -Template $Template -Domain $domainSuffix

$createdGroups = @()
$groupOperation = Start-TimedOperation -Name "Creating groups" -TotalSteps $groups.Count

for ($i = 0; $i -lt $groups.Count; $i++) {
    $group = $groups[$i]

    # Format display names and descriptions for company and department groups
    $displayName = $group.DisplayName
    $description = $group.Description

    if ($group.Category -eq "Company") {
        # Format company group display name with spaces
        $technicalName = $group.Name -replace "^Company-", ""  # Extract the company name part
        $displayName = "Company-" + ($scenario8CompanyNames[$technicalName] -replace " ", " ")  # Preserve spaces
        $description = "Company-wide group for $($scenario8CompanyNames[$technicalName])"
    }
    elseif ($group.Category -eq "Department") {
        # Format department group display name with spaces
        $technicalName = $group.Name -replace "^Dept-", ""  # Extract the department name part
        $displayName = "Dept-" + ($scenario8DepartmentNames[$technicalName] -replace " ", " ")  # Preserve spaces
        $description = "Department group for $($scenario8DepartmentNames[$technicalName])"
    }

    # Convert scope and type to samba-tool format
    $scopeArg = Get-ADGroupScopeString -Scope $group.Scope
    $typeArg = Get-ADGroupTypeString -Type $group.Type

    # Create group with samba-tool directly in Entitlements OU
    $result = docker exec $container samba-tool group add `
        $group.SAMAccountName `
        --description="$description" `
        --group-scope="$scopeArg" `
        --group-type="$typeArg" `
        --groupou="OU=Entitlements,OU=Corp" 2>&1

    if ($LASTEXITCODE -eq 0 -or $result -match "already exists") {

        # Store created group info
        $createdGroup = @{
            Name = $group.Name
            SAMAccountName = $group.SAMAccountName
            Category = $group.Category
            Type = $group.Type
            Scope = $group.Scope
            MailEnabled = $group.MailEnabled
            HasManagedBy = $group.HasManagedBy
            DN = "CN=$($group.CN),$entitlementsOU"
        }
        $createdGroups += $createdGroup

        # Set displayName attribute (samba-tool doesn't support this)
        $displayNameLdif = @"
dn: CN=$($group.CN),$entitlementsOU
changetype: modify
replace: displayName
displayName: $displayName
"@
        $ldifFile = "/tmp/group_displayname_$i.ldif"
        docker exec $container bash -c "echo '$displayNameLdif' > $ldifFile" 2>&1 | Out-Null
        docker exec $container ldapmodify -x -H ldap://localhost -D "CN=Administrator,CN=Users,$domainDN" -w "Test@123!" -f $ldifFile 2>&1 | Out-Null
        docker exec $container rm -f $ldifFile 2>&1 | Out-Null

        # Set mail attributes if mail-enabled
        if ($group.MailEnabled -and $group.Mail) {
            # Use ldapmodify to set mail attribute
            $ldifContent = @"
dn: CN=$($group.CN),$entitlementsOU
changetype: modify
replace: mail
mail: $($group.Mail)
"@
            # Write LDIF to temp file and apply
            $ldifFile = "/tmp/group_mail_$i.ldif"
            docker exec $container bash -c "echo '$ldifContent' > $ldifFile" 2>&1 | Out-Null
            docker exec $container ldapmodify -x -H ldap://localhost -D "CN=Administrator,CN=Users,$domainDN" -w "Test@123!" -f $ldifFile 2>&1 | Out-Null
            docker exec $container rm -f $ldifFile 2>&1 | Out-Null
        }
    }
    else {
        Write-Verbose "Failed to create group $($group.Name): $result"
    }

    if (($i % 5) -eq 0 -or $i -eq ($groups.Count - 1)) {
        Update-OperationProgress -Operation $groupOperation -CurrentStep ($i + 1) -Status "$($i + 1)/$($groups.Count) groups"
    }
}

Complete-TimedOperation -Operation $groupOperation -Message "Created $($createdGroups.Count) groups"

# ============================================================================
# Step 4: Assign Group Members
# ============================================================================
Write-TestStep "Step 4" "Assigning group members"

$totalMemberships = 0
$membershipOperation = Start-TimedOperation -Name "Assigning memberships" -TotalSteps $createdGroups.Count

# Intelligent membership assignment based on group category and user attributes
for ($g = 0; $g -lt $createdGroups.Count; $g++) {
    $group = $createdGroups[$g]
    $memberCount = 0

    # Select candidate users based on group category
    $candidates = @()

    switch ($group.Category) {
        "Company" {
            # Company groups contain users from that company
            $companyName = $group.Name  # Group name is already the company name
            $candidates = $createdUsers | Where-Object { $_.Company -eq $companyName }
        }
        "Department" {
            # Department groups contain users from that department
            $deptName = $group.Name  # Group name is already the department name
            $candidates = $createdUsers | Where-Object { $_.Department -eq $deptName }
        }
        "Location" {
            # Location groups: distribute users with location matching
            # For now, use a percentage-based distribution (~30% of users)
            $targetPercent = 0.3
            $targetMembers = [Math]::Max(1, [Math]::Floor($createdUsers.Count * $targetPercent))
            $offset = ($g * 7) % $createdUsers.Count
            for ($u = 0; $u -lt $targetMembers; $u++) {
                $userIndex = ($offset + $u) % $createdUsers.Count
                $candidates += $createdUsers[$userIndex]
            }
        }
        "Project" {
            # Project groups: distribute users with project assignment (~20% of users)
            $targetPercent = 0.2
            $targetMembers = [Math]::Max(1, [Math]::Floor($createdUsers.Count * $targetPercent))
            $offset = ($g * 11) % $createdUsers.Count
            for ($u = 0; $u -lt $targetMembers; $u++) {
                $userIndex = ($offset + $u) % $createdUsers.Count
                $candidates += $createdUsers[$userIndex]
            }
        }
    }

    # Add selected candidates to the group
    foreach ($user in $candidates) {
        $result = docker exec $container samba-tool group addmembers `
            $group.SAMAccountName `
            $user.SamAccountName 2>&1

        if ($LASTEXITCODE -eq 0 -or $result -match "already a member") {
            $memberCount++
            $totalMemberships++
        }
    }

    if (($g % 5) -eq 0 -or $g -eq ($createdGroups.Count - 1)) {
        Update-OperationProgress -Operation $membershipOperation -CurrentStep ($g + 1) -Status "$totalMemberships memberships"
    }
}

Complete-TimedOperation -Operation $membershipOperation -Message "Assigned $totalMemberships memberships"

# ============================================================================
# Step 5: Assign managedBy
# ============================================================================
Write-TestStep "Step 5" "Assigning group owners (managedBy)"

$managedByCount = 0
$managedByOperation = Start-TimedOperation -Name "Assigning managedBy" -TotalSteps $createdGroups.Count

# Find users with Manager or Director titles for group ownership
$managers = @($createdUsers | Where-Object { $_.Title -match "Manager|Director" })
if ($managers.Count -eq 0) {
    $managers = $createdUsers  # Fallback to any user
}

for ($g = 0; $g -lt $createdGroups.Count; $g++) {
    $group = $createdGroups[$g]

    if ($group.HasManagedBy) {
        # Select a manager based on group index
        $managerIndex = $g % $managers.Count
        $manager = $managers[$managerIndex]

        # Set managedBy using ldapmodify
        $ldifContent = @"
dn: $($group.DN)
changetype: modify
replace: managedBy
managedBy: $($manager.DN)
"@
        $ldifFile = "/tmp/group_managedby_$g.ldif"
        docker exec $container bash -c "echo '$ldifContent' > $ldifFile" 2>&1 | Out-Null
        $result = docker exec $container ldapmodify -x -H ldap://localhost -D "CN=Administrator,CN=Users,$domainDN" -w "Test@123!" -f $ldifFile 2>&1
        docker exec $container rm -f $ldifFile 2>&1 | Out-Null

        if ($LASTEXITCODE -eq 0) {
            $managedByCount++
        }
    }

    if (($g % 5) -eq 0 -or $g -eq ($createdGroups.Count - 1)) {
        Update-OperationProgress -Operation $managedByOperation -CurrentStep ($g + 1) -Status "$managedByCount assigned"
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
