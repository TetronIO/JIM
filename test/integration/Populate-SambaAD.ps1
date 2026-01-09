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
        Domain = "TESTDOMAIN"
        DomainDN = "DC=testdomain,DC=local"
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

# Create the Borton Corp base OU - this is the OU that will be selected in JIM for partition/container testing
# Department OUs will be created dynamically by the LDAP connector when provisioning users
# (enabled by the "Create containers as needed?" connector setting)
Write-Host "  Creating Borton Corp base OU..." -ForegroundColor Gray
$result = docker exec $container samba-tool ou create "OU=Borton Corp,$domainDN" 2>&1
if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
    Write-Host "    Warning: Failed to create OU 'Borton Corp': $result" -ForegroundColor Yellow
}
else {
    Write-Host "    ✓ OU created: Borton Corp" -ForegroundColor Green
}

# Note: Department OUs (Finance, IT, Marketing, etc.) are no longer pre-created here.
# The LDAP Connector's "Create containers as needed?" setting will automatically create
# OUs under /Borton Corp/ when provisioning users, e.g.:
#   OU=Finance,OU=Borton Corp,DC=testdomain,DC=local
#   OU=IT,OU=Borton Corp,DC=testdomain,DC=local
# This tests the container creation functionality and matches real-world use cases
# where department OUs may not exist initially.

# Legacy department OUs at root level (kept for backward compatibility with any existing tests)
# These will be removed in a future cleanup once all tests use the /Borton Corp/{Department} structure
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

# Create users
Write-TestStep "Step 2" "Creating $($scale.Users) users"

$departments = @("IT", "HR", "Sales", "Finance", "Operations", "Marketing", "Legal", "Engineering", "Support", "Admin")
$titles = @("Manager", "Director", "Analyst", "Specialist", "Coordinator", "Administrator", "Engineer", "Developer", "Consultant", "Associate")

$createdUsers = @()
$usersWithExpiry = 0

for ($i = 1; $i -lt $scale.Users + 1; $i++) {
    $user = New-TestUser -Index $i -Domain ($domain.ToLower() + ".local")

    $userPrincipalName = "$($user.SamAccountName)@$($domain.ToLower()).local"

    # Create user with samba-tool
    $result = docker exec $container samba-tool user create `
        $user.SamAccountName `
        "Password123!" `
        --given-name=$($user.FirstName) `
        --surname=$($user.LastName) `
        --mail-address=$($user.Email) `
        --department=$($user.Department) `
        --job-title=$($user.Title) 2>&1

    if ($LASTEXITCODE -eq 0) {
        $createdUsers += $user

        # Move to TestUsers OU
        $userDN = "CN=$($user.DisplayName),CN=Users,$domainDN"
        $targetDN = "OU=TestUsers,$domainDN"
        docker exec $container samba-tool user move $userDN $targetDN 2>&1 | Out-Null

        # Set account expiry if specified (contractors and some employees with resignations)
        if ($null -ne $user.AccountExpires) {
            $expiryDate = $user.AccountExpires.ToString("yyyy-MM-dd")
            docker exec $container samba-tool user setexpiry $user.SamAccountName --expiry-time="$expiryDate" 2>&1 | Out-Null
            $usersWithExpiry++
        }

        if (($i % 100) -eq 0 -or $i -eq $scale.Users) {
            Write-Host "    Created $i / $($scale.Users) users..." -ForegroundColor Gray
        }
    }
    elseif ($result -match "already exists") {
        # User already exists - add to list anyway for membership processing
        $createdUsers += $user
    }
    else {
        Write-Host "    Warning: Failed to create user $($user.SamAccountName): $result" -ForegroundColor Yellow
    }
}

Write-Host "  ✓ Found or created $($createdUsers.Count) users ($usersWithExpiry with account expiry)" -ForegroundColor Green

# Create groups
Write-TestStep "Step 3" "Creating $($scale.Groups) groups"

$createdGroups = @()

for ($i = 1; $i -lt $scale.Groups + 1; $i++) {
    $groupName = "TestGroup$i"
    $description = "Test group $i for integration testing"

    # Assign department-based groups
    $dept = $departments[$i % $departments.Length]
    $groupName = "Group-$dept-$i"

    $result = docker exec $container samba-tool group add `
        $groupName `
        --description="$description" 2>&1

    if ($LASTEXITCODE -eq 0) {
        $createdGroups += @{
            Name = $groupName
            Department = $dept
        }

        # Move to TestGroups OU
        $groupDN = "CN=$groupName,CN=Users,$domainDN"
        $targetDN = "OU=TestGroups,$domainDN"
        docker exec $container samba-tool group move $groupDN $targetDN 2>&1 | Out-Null

        if (($i % 20) -eq 0 -or $i -eq $scale.Groups) {
            Write-Host "    Created $i / $($scale.Groups) groups..." -ForegroundColor Gray
        }
    }
    elseif ($result -match "already exists") {
        # Group already exists - add to list anyway for membership processing
        $createdGroups += @{
            Name = $groupName
            Department = $dept
        }
    }
    else {
        Write-Host "    Warning: Failed to create group $groupName : $result" -ForegroundColor Yellow
    }
}

Write-Host "  ✓ Found or created $($createdGroups.Count) groups" -ForegroundColor Green

# Add users to groups
Write-TestStep "Step 4" "Adding users to groups (avg: $($scale.AvgMemberships) memberships/user)"

$totalMemberships = 0

foreach ($user in $createdUsers) {
    # Match users to groups by department
    $deptGroups = @($createdGroups | Where-Object { $_.Department -eq $user.Department })

    if ($deptGroups.Count -gt 0) {
        # Add to some department groups (randomised)
        $numGroups = [Math]::Min($scale.AvgMemberships, $deptGroups.Count)
        $selectedGroups = Get-RandomSubset -Items $deptGroups -Count $numGroups

        foreach ($group in $selectedGroups) {
            $result = docker exec $container samba-tool group addmembers `
                $group.Name `
                $user.SamAccountName 2>&1

            if ($LASTEXITCODE -eq 0) {
                $totalMemberships++
            }
        }
    }

    if (($totalMemberships % 500) -eq 0) {
        Write-Host "    Added $totalMemberships memberships..." -ForegroundColor Gray
    }
}

Write-Host "  ✓ Added $totalMemberships group memberships" -ForegroundColor Green

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
