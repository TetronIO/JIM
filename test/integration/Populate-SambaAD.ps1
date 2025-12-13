<#
.SYNOPSIS
    Populate Samba AD with test data

.DESCRIPTION
    Creates organisational structure, users, and groups in Samba AD
    based on the specified template scale

.PARAMETER Template
    Data scale template (Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER Instance
    Which Samba AD instance to populate (Primary, Source, Target)

.EXAMPLE
    ./Populate-SambaAD.ps1 -Template Small -Instance Primary
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
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

$ous = @("TestUsers", "TestGroups")
foreach ($ou in $ous) {
    Write-Host "  Creating OU: $ou" -ForegroundColor Gray

    $result = docker exec $container samba-tool ou create "OU=$ou,$domainDN" 2>&1
    if ($LASTEXITCODE -ne 0 -and $result -notmatch "already exists") {
        Write-Host "    Warning: Failed to create OU $ou : $result" -ForegroundColor Yellow
    }
    else {
        Write-Host "    ✓ OU created: $ou" -ForegroundColor Green
    }
}

# Create users
Write-TestStep "Step 2" "Creating $($scale.Users) users"

$departments = @("IT", "HR", "Sales", "Finance", "Operations", "Marketing", "Legal", "Engineering", "Support", "Admin")
$titles = @("Manager", "Director", "Analyst", "Specialist", "Coordinator", "Administrator", "Engineer", "Developer", "Consultant", "Associate")

$createdUsers = @()

for ($i = 1; $i -le $scale.Users; $i++) {
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

Write-Host "  ✓ Found or created $($createdUsers.Count) users" -ForegroundColor Green

# Create groups
Write-TestStep "Step 3" "Creating $($scale.Groups) groups"

$createdGroups = @()

for ($i = 1; $i -le $scale.Groups; $i++) {
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
Write-Host "Users:          $($createdUsers.Count)" -ForegroundColor Cyan
Write-Host "Groups:         $($createdGroups.Count)" -ForegroundColor Cyan
Write-Host "Memberships:    $totalMemberships" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ Samba AD population complete" -ForegroundColor Green

# Exit with success - idempotent operation
exit 0
