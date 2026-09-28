# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Populate Samba AD with test data for Scenario 008: Cross-domain Entitlement Sync

.DESCRIPTION
    Creates users and entitlement groups in Source AD for cross-domain group synchronisation.
    This script is self-contained - it creates its own users and groups in the Source AD,
    which will then be synced to the Target AD by JIM.

    Structure created:
    - OU=Corp,DC=resurgam,DC=local
      - OU=Users (test users)
      - OU=Entitlements (entitlement groups)

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, MediumLarge, Large, Scale100k50Groups, Scale200k55Groups, Scale500k65Groups, Scale750k70Groups, Scale1m80Groups, Scale100k5kGroups, Scale200k10kGroups, Scale500k25kGroups, Scale750k40kGroups, Scale1m60kGroups)

.PARAMETER Instance
    Which Samba AD instance to populate (Source or Target)
    - Source: Populates users and groups
    - Target: Only creates OU structure (groups will be provisioned by JIM)

.PARAMETER DirectoryConfig
    Optional. When it is an ActiveDirectory config (Get-DirectoryConfig -DirectoryType ActiveDirectory),
    the population is delivered to the real domain controller of the Instance (a Hyper-V virtual
    machine, no container) instead of into a Samba container: the same LDIF this script builds is fed to
    ldapadd or ldapmodify through Invoke-LdapTool over LDAPS as the domain administrator, group
    membership is added with "add: member" modify records of 500 members instead of samba-tool, and
    JIM's delegation over a container this script creates is applied through the lab control plane.
    The scenario passes the run's own config (its Primary instance); the domain controller written to is
    the one for -Instance, resolved from the lab environment variables when the config passed is not
    that instance's. When absent, or any other directory type, the behaviour is unchanged: the Samba
    container is chosen by -Instance.

.EXAMPLE
    ./Populate-SambaAD-Scenario-008.ps1 -Template Nano -Instance Source

.EXAMPLE
    ./Populate-SambaAD-Scenario-008.ps1 -Template Small -Instance Source

.EXAMPLE
    ./Populate-SambaAD-Scenario-008.ps1 -Template Nano -Instance Source -DirectoryConfig (Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance Source)
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Source", "Target")]
    [string]$Instance = "Source",

    [Parameter(Mandatory=$false)]
    [string]$Container = "",

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Hard-fail: the long-tail templates are OpenLDAP only.
# Samba AD's per-call LDB write lock makes the long-tail shape (thousands of
# groups, millions of memberships) impractical within the time budget. Surface
# the constraint immediately rather than letting the run start and time out
# hours later.
$longTailTemplates = @("Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")
if ($Template -in $longTailTemplates) {
    throw "Template '$Template' is not supported on Samba AD. Use 'Scale100k50Groups' or another capped-groups template for Samba scale testing, or run this template with -DirectoryType OpenLDAP."
}

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"
. "$PSScriptRoot/utils/Test-GroupHelpers.ps1"

# Active Directory lab: a real domain controller has no container to docker exec into, so the same LDIF
# is delivered with ldapadd or ldapmodify from the LDAP toolbox over LDAPS instead. The extra helpers are
# only loaded for that, so a Samba run is exactly as it was.
$useActiveDirectory = $false
$adConfig = $null
if ($DirectoryConfig -and ([string]$DirectoryConfig['DirectoryType']) -eq 'ActiveDirectory') {
    . "$PSScriptRoot/utils/LDAP-Helpers.ps1"
    . "$PSScriptRoot/utils/Invoke-LabControl.ps1"
    . "$PSScriptRoot/utils/ActiveDirectoryLab-Helpers.ps1"
    $useActiveDirectory = $true
}

Write-TestSection "Scenario 008: Populating $(if ($useActiveDirectory) { 'Active Directory' } else { 'Samba AD' }) ($Instance) with $Template template"

# Get scales
$groupScale = Get-Scenario8GroupScale -Template $Template

# Define consistent company and department lists for Scenario 008.
# Keys are technical names (must match $script:CompanyNames / $script:DepartmentNames
# in utils/Test-GroupHelpers.ps1 exactly); values are display names (with spaces).
# Every entry in the source arrays must have a mapping here, otherwise the populate
# step will throw rather than silently emit a "Company-" / "Dept-" displayName.
$scenario8CompanyNames = @{
    "Panoply" = "Panoply"
    "NexusDynamics" = "Nexus Dynamics"
    "OrbitalSystems" = "Akinya"
    "QuantumBridge" = "Rockhopper"
    "StellarLogistics" = "Stellar Logistics"
    "VortexTech" = "Vortex Tech"
    "CatalystCorp" = "Catalyst Corp"
    "HorizonIndustries" = "Horizon Industries"
    "PulsarEnterprises" = "Pulsar Enterprises"
    "NovaNetworks" = "Nova Networks"
    "FusionCore" = "Fusion Core"
    "CelestialSystems" = "Celestial Systems"
    "NebulaWorks" = "Nebula Works"
    "AtomicVentures" = "Atomic Ventures"
    "CosmicPlatform" = "Cosmic Platform"
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
    "Customer-Support" = "Customer Support"
    "Quality-Assurance" = "Quality Assurance"
    "Product-Management" = "Product Management"
    "Data-Science" = "Data Science"
    "Security" = "Security"
    "Facilities" = "Facilities"
    "Executive" = "Executive"
    "Compliance" = "Compliance"
    "Communications" = "Communications"
    "Training" = "Training"
}

# Container and domain mapping
$containerMap = @{
    Source = @{
        Container = "samba-ad-source"
        Domain = "RESURGAM"
        DomainDN = "DC=resurgam,DC=local"
        DomainSuffix = "resurgam.local"
    }
    Target = @{
        Container = "samba-ad-target"
        Domain = "GENTIAN"
        DomainDN = "DC=gentian,DC=local"
        DomainSuffix = "gentian.local"
    }
}

$config = $containerMap[$Instance]
$container = if ($Container) { $Container } else { $config.Container }
$domain = $config.Domain
$domainDN = $config.DomainDN
$domainSuffix = $config.DomainSuffix

if ($useActiveDirectory) {
    # The scenario passes the run's own config (its Primary instance), so the domain controller written
    # to is the config whose base DN is this Instance's domain, and otherwise this Instance's own.
    $adConfig = if ([string]$DirectoryConfig['BaseDN'] -ieq $domainDN) { $DirectoryConfig } else { Get-DirectoryConfig -DirectoryType ActiveDirectory -Instance $Instance }
}

Write-Host "Container:       $(if ($useActiveDirectory) { "none (domain controller $($adConfig.VmName), $($adConfig.Host); LDAP tools run in the toolbox)" } else { $container })" -ForegroundColor Gray
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

if ($useActiveDirectory) {
    # Active Directory: the units are added with ldapadd. The baseline checkpoint already holds OU=Corp,
    # OU=Users and OU=Groups under it (delegated to JIM), so "already exists" is the ordinary answer for
    # Corp and Users, exactly as it is with samba-tool. A container this script creates directly under the
    # domain root (CorpManaged) carries no delegation, so JIM's access is granted over it through the lab
    # control plane the moment it exists; everything below Corp or CorpManaged inherits it.
    $corpOU = "OU=Corp,$domainDN"
    $usersOU = "OU=Users,$corpOU"
    $entitlementsOU = "OU=Entitlements,$corpOU"
    $adOrganisationalUnits = @(
        @{ Dn = $corpOU; Delegate = $true },
        @{ Dn = $usersOU; Delegate = $false },
        @{ Dn = $entitlementsOU; Delegate = $false }
    )
    if ($Instance -eq "Target") {
        $corpManagedOU = "OU=CorpManaged,$domainDN"
        $adOrganisationalUnits += @{ Dn = $corpManagedOU; Delegate = $true }
        $adOrganisationalUnits += @{ Dn = "OU=Users,$corpManagedOU"; Delegate = $false }
        $adOrganisationalUnits += @{ Dn = "OU=Entitlements,$corpManagedOU"; Delegate = $false }
    }

    foreach ($adUnit in $adOrganisationalUnits) {
        $unitOutcome = Invoke-ActiveDirectoryOrganisationalUnitAdd -DirectoryConfig $adConfig -Dn $adUnit.Dn
        Write-ActiveDirectoryLabLine "    ✓ OU $($unitOutcome.ToLower()): $($adUnit.Dn)" -ForegroundColor Green
        if ($adUnit.Delegate) {
            Invoke-ActiveDirectoryLabDelegation -VmName $adConfig.VmName -ContainerDn $adUnit.Dn
            Write-ActiveDirectoryLabLine "    ✓ JIM delegation granted: $($adUnit.Dn) (inherited by everything below it)" -ForegroundColor Green
        }
    }

    if ($Instance -eq "Target") {
        Write-TestSection "Target Population Complete"
        Write-ActiveDirectoryLabLine "Template:       $Template" -ForegroundColor Cyan
        Write-ActiveDirectoryLabLine "OU structure created - JIM will provision users and groups" -ForegroundColor Gray
        Write-ActiveDirectoryLabLine ""
        Write-ActiveDirectoryLabLine "✓ Target AD population complete (OU structure only)" -ForegroundColor Green
        exit 0
    }
}

# The Samba AD branch below is unchanged and deliberately not re-indented, so the diff for the Active
# Directory support stays reviewable. It runs for every directory config that is not ActiveDirectory.
if (-not $useActiveDirectory) {
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

# JIM manages objects under Corp, so its service account needs the delegation over it. The Users
# and Entitlements OUs created below it inherit the delegation and need no call of their own.
Grant-JimAdDelegation -ContainerName $container -ContainerDn $corpOU
Write-Host "    ✓ JIM delegation granted: Corp (inherited by everything below it)" -ForegroundColor Green

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

    # JIM provisions into CorpManaged, so its service account needs the delegation over it. The
    # Users and Entitlements OUs created below it inherit the delegation.
    Grant-JimAdDelegation -ContainerName $container -ContainerDn $corpManagedOU
    Write-Host "    ✓ JIM delegation granted: CorpManaged (inherited by everything below it)" -ForegroundColor Green

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
} # end: Samba AD organisational units

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

    # userAccountControl distribution: 90% enabled (512 → Active), 10% disabled (514 → Archived)
    # Slot i=0 reserved for disabled so small datasets always have at least one disabled user
    $uac = if ($i -eq 0) { 514 } elseif (($i % 10) -eq 9) { 514 } else { 512 }

    [PSCustomObject]@{
        Index                = $i
        SamAccountName       = $user.SamAccountName
        DisplayName          = $user.DisplayName
        FirstName            = $user.FirstName
        LastName             = $user.LastName
        Email                = $user.Email
        Title                = $user.Title
        Pronouns             = $user.Pronouns
        Department           = $departmentTechnicalName
        Company              = $companyTechnicalName
        DN                   = "CN=$($user.DisplayName),$using:usersOU"
        UserAccountControl   = $uac
    }
} -ThrottleLimit ([Math]::Min(8, [Environment]::ProcessorCount))

# Sort by index to ensure deterministic order for LDIF generation
$createdUsers = @($createdUsers | Sort-Object -Property Index)

$userGenDuration = ((Get-Date) - $userGenStart).TotalSeconds
Write-Host "  ✓ Generated $($createdUsers.Count) user records in $([Math]::Round($userGenDuration, 1))s" -ForegroundColor Green

# Build LDIF and import in chunks (ldbadd is single-writer, must be sequential)
Write-Host "  Importing users via $(if ($useActiveDirectory) { 'ldapadd' } else { 'ldbadd' })..." -ForegroundColor Gray

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
    [void]$ldifBuilder.AppendLine("userAccountControl: $($u.UserAccountControl)")

    if ($null -ne $u.Pronouns) {
        [void]$ldifBuilder.AppendLine("extensionAttribute1: $($u.Pronouns)")
    }

    [void]$ldifBuilder.AppendLine("")

    # Import in chunks to avoid ldbadd OOM on very large LDB databases
    if ((($i + 1) % $ldifChunkSize -eq 0) -or ($i -eq $createdUsers.Count - 1)) {
        $chunkIndex++
        $chunkCount = if (($i + 1) % $ldifChunkSize -eq 0) { $ldifChunkSize } else { ($i + 1) % $ldifChunkSize }

        if ($useActiveDirectory) {
            # Active Directory: the LDIF built above is fed to ldapadd in the toolbox instead of ldbadd in
            # a container. "Already exists" (68) is tolerated, as it is for ldbadd; anything else throws.
            # Active Directory: two things Samba accepts in this LDIF that a real domain controller may not.
            # (1) userAccountControl 512 (enabled) is added with no password, and the domain's policy is
            # left at the Windows defaults (complexity on); an enabled account with no password may be
            # refused with result 19 (0000052D, WILL_NOT_PERFORM), in which case the users have to be added
            # disabled (514) or with a unicodePwd. (2) sAMAccountName is limited to 20 characters and
            # New-TestUser's names may exceed that. Neither is worked around here; the first run against
            # the lab answers both, and the failure message names the entry and the directory's reason.
            Write-ActiveDirectoryLabLine "  Importing chunk $chunkIndex ($chunkCount users, total $($i + 1)/$($createdUsers.Count))..." -ForegroundColor Gray
            $chunkOutcome = Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $adConfig -Tool ldapadd -Ldif $ldifBuilder.ToString() `
                -AcceptedResultCode 68 -Description "users chunk $chunkIndex (users $($i + 2 - $chunkCount) to $($i + 1))"
            $totalAdded += $chunkOutcome.Applied
            if ($chunkOutcome.AcceptedCount -gt 0) {
                Write-ActiveDirectoryLabLine "    ⚠ $($chunkOutcome.AcceptedCount) users in chunk already exist (idempotent)" -ForegroundColor Yellow
            }
            $ldifBuilder.Clear() | Out-Null
            continue
        }

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
        if (-not $scenario8CompanyNames.ContainsKey($technicalName)) {
            throw "Scenario 008 populate: no pretty-name mapping for company technical name '$technicalName'. Add an entry to `$scenario8CompanyNames in Populate-SambaAD-Scenario-008.ps1 to keep it in sync with `$script:CompanyNames in utils/Test-GroupHelpers.ps1."
        }
        $displayName = "Company-" + ($scenario8CompanyNames[$technicalName] -replace " ", " ")
        $description = "Company-wide group for $($scenario8CompanyNames[$technicalName])"
    }
    elseif ($group.Category -eq "Department") {
        $technicalName = $group.Name -replace "^Dept-", ""
        if (-not $scenario8DepartmentNames.ContainsKey($technicalName)) {
            throw "Scenario 008 populate: no pretty-name mapping for department technical name '$technicalName'. Add an entry to `$scenario8DepartmentNames in Populate-SambaAD-Scenario-008.ps1 to keep it in sync with `$script:DepartmentNames in utils/Test-GroupHelpers.ps1."
        }
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

if ($useActiveDirectory) {
    # Active Directory: the group LDIF built above goes to ldapadd instead of ldbadd. Result 68 (already
    # exists) is tolerated, as it is for ldbadd. $result is set to the shape ldbadd reports, so the shared
    # reporting below is unchanged.
    # Active Directory: the groups are added with sAMAccountName, groupType (negative 32-bit values for
    # security groups), description, displayName and mail exactly as Samba takes them. sAMAccountName is
    # limited to 20 characters and a group name from the generator may exceed that; the first run against
    # the lab answers it.
    Write-ActiveDirectoryLabLine "  Importing $($groups.Count) groups via ldapadd..." -ForegroundColor Gray
    $groupOutcome = Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $adConfig -Tool ldapadd -Ldif $groupLdifBuilder.ToString() `
        -AcceptedResultCode 68 -Description "groups"
    $result = "Added $($groupOutcome.Applied) records"
}
else {
# Write LDIF and bulk import groups
$groupLdifPath = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllText($groupLdifPath, $groupLdifBuilder.ToString())

Write-Host "  Importing $($groups.Count) groups via ldbadd..." -ForegroundColor Gray
docker cp $groupLdifPath "${container}:/tmp/groups.ldif" 2>&1 | Out-Null
$result = docker exec $container ldbadd -H /usr/local/samba/private/sam.ldb /tmp/groups.ldif 2>&1
docker exec $container rm -f /tmp/groups.ldif 2>&1 | Out-Null
Remove-Item $groupLdifPath -Force -ErrorAction SilentlyContinue
} # end: Samba AD group import

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

if ($useActiveDirectory) {
    # Active Directory: "samba-tool group addmembers" becomes "add: member" modify records (still in chunks
    # of 500 members, well inside what one modify may add) sent with ldapmodify. Members and groups are
    # addressed by the DNs the LDIF above already used. A member that is already in the group is
    # tolerated, as "already a member" is above; anything else throws, because a group with a partial
    # membership fails a scenario far from the cause. Active Directory: the result code for a value that
    # is already present is not certain (20, "Type or value exists", or 68, "Already exists"), so both are
    # tolerated; the first run against the lab names the real one.
    $adUserDnBySam = @{}
    foreach ($adUser in $createdUsers) { $adUserDnBySam[$adUser.SamAccountName] = $adUser.DN }
    $adGroupDnBySam = @{}
    foreach ($adGroup in $createdGroups) { $adGroupDnBySam[$adGroup.SAMAccountName] = $adGroup.DN }

    for ($w = 0; $w -lt $membershipWorkItems.Count; $w++) {
        $item = $membershipWorkItems[$w]
        $memberDns = @($item.Members | ForEach-Object { $adUserDnBySam[$_] })
        $null = Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $adConfig -Tool ldapmodify `
            -Ldif (Get-LdapMemberAddLdif -GroupDn $adGroupDnBySam[$item.GroupName] -MemberDn $memberDns -ChunkSize $chunkSize) `
            -AcceptedResultCode 20, 68 -Description "the members of group $($item.GroupName)"
        $totalMemberships += $memberDns.Count

        if (($w + 1) % 100 -eq 0) {
            Write-ActiveDirectoryLabLine "    Progress: $($w + 1)/$($membershipWorkItems.Count) groups..." -ForegroundColor Gray
        }
    }
}
else {
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
} # end: Samba AD membership assignment

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
    if ($useActiveDirectory) {
        # Active Directory: the modify LDIF built above goes to ldapmodify instead of ldbmodify. A failure
        # throws (the Samba branch only warns), so a group is never left without its owner unseen.
        Write-ActiveDirectoryLabLine "  Applying $managedByCount managedBy assignments via ldapmodify..." -ForegroundColor Gray
        $null = Invoke-ActiveDirectoryLdifDelivery -DirectoryConfig $adConfig -Tool ldapmodify -Ldif $modifyBuilder.ToString() `
            -Description "the managedBy assignments"
        Write-ActiveDirectoryLabLine "  ✓ Set managedBy on $managedByCount groups via ldapmodify" -ForegroundColor Green
    }
    else {
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
    } # end: Samba AD managedBy
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
Write-Host "✓ $(if ($useActiveDirectory) { 'Active Directory' } else { 'Samba AD' }) population complete" -ForegroundColor Green

exit 0
