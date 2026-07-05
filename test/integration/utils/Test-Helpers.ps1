# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Common test helper functions for integration testing

.DESCRIPTION
    Provides assertion functions, logging helpers, and common utilities
    used across integration test scenarios
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Condition {
    <#
    .SYNOPSIS
        Assert a condition is true, fail with message if false
    #>
    param(
        [Parameter(Mandatory=$true)]
        [bool]$Condition,

        [Parameter(Mandatory=$true)]
        [string]$Message
    )

    if (-not $Condition) {
        Write-Host "✗ FAILED: $Message" -ForegroundColor Red
        throw "Assertion failed: $Message"
    }

    Write-Host "✓ PASSED: $Message" -ForegroundColor Green
}

function Assert-Equal {
    <#
    .SYNOPSIS
        Assert two values are equal
    #>
    param(
        [Parameter(Mandatory=$true)]
        $Expected,

        [Parameter(Mandatory=$true)]
        $Actual,

        [Parameter(Mandatory=$true)]
        [string]$Message
    )

    if ($Expected -ne $Actual) {
        Write-Host "✗ FAILED: $Message" -ForegroundColor Red
        Write-Host "  Expected: $Expected" -ForegroundColor Yellow
        Write-Host "  Actual:   $Actual" -ForegroundColor Yellow
        throw "Assertion failed: $Message (Expected: $Expected, Actual: $Actual)"
    }

    Write-Host "✓ PASSED: $Message" -ForegroundColor Green
}

function Assert-NotNull {
    <#
    .SYNOPSIS
        Assert a value is not null
    #>
    param(
        [Parameter(Mandatory=$true)]
        [AllowNull()]
        $Value,

        [Parameter(Mandatory=$true)]
        [string]$Message
    )

    if ($null -eq $Value) {
        Write-Host "✗ FAILED: $Message (Value was null)" -ForegroundColor Red
        throw "Assertion failed: $Message (Value was null)"
    }

    Write-Host "✓ PASSED: $Message" -ForegroundColor Green
}

function Write-Failure {
    <#
    .SYNOPSIS
        Write a failure message in red
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Message
    )
    Write-Host "✗ $Message" -ForegroundColor Red
}

function Write-TestSection {
    <#
    .SYNOPSIS
        Write a test section header
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Title
    )

    Write-Host ""
    Write-Host "===========================================" -ForegroundColor Cyan
    Write-Host " $Title" -ForegroundColor Cyan
    Write-Host "===========================================" -ForegroundColor Cyan
    Write-Host ""
}

function Write-TestStep {
    <#
    .SYNOPSIS
        Write a test step header
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Step,

        [Parameter(Mandatory=$true)]
        [string]$Description
    )

    Write-Host "`n[$Step] $Description" -ForegroundColor Yellow
}

function Wait-ForCondition {
    <#
    .SYNOPSIS
        Wait for a condition to become true with timeout
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ScriptBlock]$Condition,

        [Parameter(Mandatory=$false)]
        [int]$TimeoutSeconds = 300,

        [Parameter(Mandatory=$false)]
        [int]$IntervalSeconds = 5,

        [Parameter(Mandatory=$true)]
        [string]$Description
    )

    $elapsed = 0
    Write-Host "Waiting for: $Description (timeout: ${TimeoutSeconds}s)" -ForegroundColor Gray

    while ($elapsed -lt $TimeoutSeconds) {
        try {
            $result = & $Condition
            if ($result) {
                Write-Host "✓ Condition met after ${elapsed}s" -ForegroundColor Green
                return $true
            }
        }
        catch {
            # Condition check failed, continue waiting
            Write-Verbose "Condition check error: $_"
        }

        Start-Sleep -Seconds $IntervalSeconds
        $elapsed += $IntervalSeconds

        if ($elapsed % 30 -eq 0) {
            Write-Host "  Still waiting... (${elapsed}s elapsed)" -ForegroundColor Gray
        }
    }

    Write-Host "✗ Timeout waiting for: $Description" -ForegroundColor Red
    return $false
}

function Get-TemplateScale {
    <#
    .SYNOPSIS
        Get the object counts for a template size
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
        [string]$Template
    )

    $scales = @{
        Nano = @{
            Users = 3
            Groups = 1
            AvgMemberships = 1
        }
        Micro = @{
            Users = 10
            Groups = 3
            AvgMemberships = 3
        }
        Small = @{
            Users = 100
            Groups = 20
            AvgMemberships = 5
        }
        Medium = @{
            Users = 1000
            Groups = 100
            AvgMemberships = 8
        }
        MediumLarge = @{
            Users = 5000
            Groups = 250
            AvgMemberships = 9
        }
        Large = @{
            Users = 10000
            Groups = 500
            AvgMemberships = 10
        }
        # Scale templates: Capped group counts to keep total memberships manageable.
        # samba-tool holds an LDB write lock per call, making millions of membership
        # writes impractical. Fewer groups with higher avg memberships gives better
        # coverage without the population time explosion.
        Scale100k50Groups = @{
            Users = 100000
            Groups = 50
            AvgMemberships = 12
        }
        Scale200k55Groups = @{
            Users = 200000
            Groups = 55
            AvgMemberships = 12
        }
        Scale500k65Groups = @{
            Users = 500000
            Groups = 65
            AvgMemberships = 13
        }
        Scale750k70Groups = @{
            Users = 750000
            Groups = 70
            AvgMemberships = 14
        }
        Scale1m80Groups = @{
            Users = 1000000
            Groups = 80
            AvgMemberships = 15
        }
        # Scale100k5kGroups: realistic long-tail group shape for Scenario 8 only.
        # Group counts and per-user membership average are driven by the per-category
        # logic in Test-GroupHelpers.ps1 (Get-Scenario8GroupScale). Values below are
        # informational and are used by Generate-TestCSV / CSV cache keying only;
        # the Samba populator must reject this template before it gets here.
        Scale100k5kGroups = @{
            Users = 100000
            Groups = 5027
            AvgMemberships = 9
        }
        # Higher-tier long-tail templates. Same Scenario 8 + OpenLDAP-only constraint
        # as Scale100k5kGroups: category counts grow sub-linearly for org-structure
        # categories (Divisions, Locations, Departments) and roughly linearly for the
        # ad-hoc tail (Projects, DistributionLists). The AvgMemberships values below
        # are informational; the populator emits the real distribution via
        # Get-LongTailGroupSize.
        Scale200k10kGroups = @{
            Users = 200000
            Groups = 9984
            AvgMemberships = 8
        }
        Scale500k25kGroups = @{
            Users = 500000
            Groups = 24997
            AvgMemberships = 10
        }
        Scale750k40kGroups = @{
            Users = 750000
            Groups = 40011
            AvgMemberships = 11
        }
        Scale1m60kGroups = @{
            Users = 1000000
            Groups = 60073
            AvgMemberships = 13
        }
    }

    return $scales[$Template]
}

function Get-DirectoryConfig {
    <#
    .SYNOPSIS
        Get directory-specific configuration for integration tests

    .DESCRIPTION
        Returns a hashtable with all directory-specific values needed by setup scripts,
        scenario scripts, and LDAP helper functions. This abstraction allows the same
        test scenarios to run against Samba AD or OpenLDAP by varying only the
        directory-specific details.

    .PARAMETER DirectoryType
        Which directory type to configure for (SambaAD or OpenLDAP)

    .PARAMETER Instance
        Which instance to use. For SambaAD: Primary, Source, Target.
        For OpenLDAP: Primary (the only instance, but with two suffixes).
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("SambaAD", "OpenLDAP")]
        [string]$DirectoryType,

        [Parameter(Mandatory=$false)]
        [string]$Instance = "Primary"
    )

    switch ($DirectoryType) {
        "SambaAD" {
            $instanceConfigs = @{
                Primary = @{
                    ContainerName    = "samba-ad-primary"
                    Host             = "samba-ad-primary"
                    Port             = 636
                    UseSSL           = $true
                    CertValidation   = "Skip Validation (Not Recommended)"
                    BindDN           = "CN=Administrator,CN=Users,DC=panoply,DC=local"
                    BindPassword     = "Test@123!"
                    AuthType         = "Simple"
                    BaseDN           = "DC=panoply,DC=local"
                    UserContainer    = "OU=Users,OU=Corp,DC=panoply,DC=local"
                    GroupContainer   = "OU=Groups,OU=Corp,DC=panoply,DC=local"
                    UserObjectClass  = "user"
                    GroupObjectClass = "group"
                    UserRdnAttr      = "CN"
                    UserNameAttr     = "sAMAccountName"
                    ExternalIdAttr   = "objectGUID"
                    DepartmentAttr   = "department"
                    DeleteBehaviour  = "Disable"
                    DisableAttribute = "userAccountControl"
                    DnTemplate       = 'CN={displayName},OU=Users,OU=Corp,DC=panoply,DC=local'
                    Domain           = "panoply.local"
                    ShortDomain      = "PANOPLY"
                    LdapSearchPort   = 389
                    LdapSearchScheme = "ldap"
                    ComposeProfiles  = @()
                    PopulateScript   = "Populate-SambaAD.ps1"
                    ConnectedSystemName = "Panoply AD"
                }
                Source = @{
                    ContainerName    = "samba-ad-source"
                    Host             = "samba-ad-source"
                    Port             = 636
                    UseSSL           = $true
                    CertValidation   = "Skip Validation (Not Recommended)"
                    BindDN           = "CN=Administrator,CN=Users,DC=resurgam,DC=local"
                    BindPassword     = "Test@123!"
                    AuthType         = "Simple"
                    BaseDN           = "DC=resurgam,DC=local"
                    UserContainer    = "OU=Users,OU=Corp,DC=resurgam,DC=local"
                    GroupContainer   = "OU=Groups,OU=Corp,DC=resurgam,DC=local"
                    UserObjectClass  = "user"
                    GroupObjectClass = "group"
                    UserRdnAttr      = "CN"
                    UserNameAttr     = "sAMAccountName"
                    ExternalIdAttr   = "objectGUID"
                    DepartmentAttr   = "department"
                    DeleteBehaviour  = "Disable"
                    DisableAttribute = "userAccountControl"
                    DnTemplate       = 'CN={displayName},OU=Users,OU=Corp,DC=resurgam,DC=local'
                    Domain           = "resurgam.local"
                    ShortDomain      = "RESURGAM"
                    LdapSearchPort   = 389
                    LdapSearchScheme = "ldap"
                    ComposeProfiles  = @("scenario2")
                    PopulateScript   = "Populate-SambaAD.ps1"
                    ConnectedSystemName = "Resurgam AD"
                }
                Target = @{
                    ContainerName    = "samba-ad-target"
                    Host             = "samba-ad-target"
                    Port             = 636
                    UseSSL           = $true
                    CertValidation   = "Skip Validation (Not Recommended)"
                    BindDN           = "CN=Administrator,CN=Users,DC=gentian,DC=local"
                    BindPassword     = "Test@123!"
                    AuthType         = "Simple"
                    BaseDN           = "DC=gentian,DC=local"
                    UserContainer    = "OU=Users,OU=CorpManaged,DC=gentian,DC=local"
                    GroupContainer   = "OU=Groups,OU=CorpManaged,DC=gentian,DC=local"
                    UserObjectClass  = "user"
                    GroupObjectClass = "group"
                    UserRdnAttr      = "CN"
                    UserNameAttr     = "sAMAccountName"
                    ExternalIdAttr   = "objectGUID"
                    DepartmentAttr   = "department"
                    DeleteBehaviour  = "Disable"
                    DisableAttribute = "userAccountControl"
                    DnTemplate       = 'CN={displayName},OU=Users,OU=CorpManaged,DC=gentian,DC=local'
                    Domain           = "gentian.local"
                    ShortDomain      = "GENTIAN"
                    LdapSearchPort   = 389
                    LdapSearchScheme = "ldap"
                    ComposeProfiles  = @("scenario2")
                    PopulateScript   = "Populate-SambaAD.ps1"
                    ConnectedSystemName = "Gentian AD"
                }
            }

            if (-not $instanceConfigs.ContainsKey($Instance)) {
                throw "Unknown SambaAD instance: $Instance. Valid values: Primary, Source, Target"
            }

            return $instanceConfigs[$Instance]
        }
        "OpenLDAP" {
            $instanceConfigs = @{
                Primary = @{
                    ContainerName    = "openldap-primary"
                    Host             = "openldap-primary"
                    Port             = 1389
                    UseSSL           = $false
                    CertValidation   = $null
                    BindDN           = "cn=admin,dc=yellowstone,dc=local"
                    BindPassword     = "Test@123!"
                    AuthType         = "Simple"
                    BaseDN           = "dc=yellowstone,dc=local"
                    UserContainer    = "ou=People,dc=yellowstone,dc=local"
                    GroupContainer   = "ou=Groups,dc=yellowstone,dc=local"
                    UserObjectClass  = "inetOrgPerson"
                    GroupObjectClass = "groupOfNames"
                    UserRdnAttr      = "uid"
                    UserNameAttr     = "uid"
                    ExternalIdAttr   = "entryUUID"
                    DepartmentAttr   = "departmentNumber"
                    DeleteBehaviour  = "Delete"
                    DisableAttribute = $null
                    DnTemplate       = 'uid={uid},ou=People,dc=yellowstone,dc=local'
                    Domain           = "yellowstone.local"
                    ShortDomain      = $null
                    LdapSearchPort   = 1389
                    LdapSearchScheme = "ldap"
                    ComposeProfiles  = @("openldap")
                    PopulateScript   = "Populate-OpenLDAP.ps1"
                    ConnectedSystemName = "Yellowstone OpenLDAP"
                    # Second suffix for multi-partition testing
                    SecondSuffix     = "dc=glitterband,dc=local"
                    SecondBindDN     = "cn=admin,dc=glitterband,dc=local"
                }
                # Source and Target use the same OpenLDAP container but different suffixes
                # for cross-domain sync testing (Scenario 2)
                Source = @{
                    ContainerName    = "openldap-primary"
                    Host             = "openldap-primary"
                    Port             = 1389
                    UseSSL           = $false
                    CertValidation   = $null
                    BindDN           = "cn=admin,dc=yellowstone,dc=local"
                    BindPassword     = "Test@123!"
                    AuthType         = "Simple"
                    BaseDN           = "dc=yellowstone,dc=local"
                    UserContainer    = "ou=People,dc=yellowstone,dc=local"
                    GroupContainer   = "ou=Groups,dc=yellowstone,dc=local"
                    UserObjectClass  = "inetOrgPerson"
                    GroupObjectClass = "groupOfNames"
                    UserRdnAttr      = "uid"
                    UserNameAttr     = "uid"
                    ExternalIdAttr   = "entryUUID"
                    DepartmentAttr   = "departmentNumber"
                    DeleteBehaviour  = "Delete"
                    DisableAttribute = $null
                    DnTemplate       = 'uid={uid},ou=People,dc=yellowstone,dc=local'
                    Domain           = "yellowstone.local"
                    ShortDomain      = $null
                    LdapSearchPort   = 1389
                    LdapSearchScheme = "ldap"
                    ComposeProfiles  = @("openldap")
                    PopulateScript   = "Populate-OpenLDAP.ps1"
                    ConnectedSystemName = "Yellowstone APAC"
                }
                Target = @{
                    ContainerName    = "openldap-primary"
                    Host             = "openldap-primary"
                    Port             = 1389
                    UseSSL           = $false
                    CertValidation   = $null
                    BindDN           = "cn=admin,dc=glitterband,dc=local"
                    BindPassword     = "Test@123!"
                    AuthType         = "Simple"
                    BaseDN           = "dc=glitterband,dc=local"
                    UserContainer    = "ou=People,dc=glitterband,dc=local"
                    GroupContainer   = "ou=Groups,dc=glitterband,dc=local"
                    UserObjectClass  = "inetOrgPerson"
                    GroupObjectClass = "groupOfNames"
                    UserRdnAttr      = "uid"
                    UserNameAttr     = "uid"
                    ExternalIdAttr   = "entryUUID"
                    DepartmentAttr   = "departmentNumber"
                    DeleteBehaviour  = "Delete"
                    DisableAttribute = $null
                    DnTemplate       = 'uid={uid},ou=People,dc=glitterband,dc=local'
                    Domain           = "glitterband.local"
                    ShortDomain      = $null
                    LdapSearchPort   = 1389
                    LdapSearchScheme = "ldap"
                    ComposeProfiles  = @("openldap")
                    PopulateScript   = "Populate-OpenLDAP.ps1"
                    ConnectedSystemName = "Glitterband EMEA"
                }
            }

            if (-not $instanceConfigs.ContainsKey($Instance)) {
                throw "Unknown OpenLDAP instance: $Instance. Valid values: Primary, Source, Target"
            }

            return $instanceConfigs[$Instance]
        }
    }
}

# Script-level cache for name data (loaded once)
$script:TestNameData = $null

function Get-TestNameData {
    <#
    .SYNOPSIS
        Load and cache name data from reference CSV files
    #>
    if ($null -eq $script:TestNameData) {
        $testDataPath = Join-Path $PSScriptRoot "../../test-data"

        # Load first names from CSVs (skip header row)
        $femaleNames = @()
        $maleNames = @()
        $lastNames = @()

        $femaleFile = Join-Path $testDataPath "Firstnames-f.csv"
        $maleFile = Join-Path $testDataPath "Firstnames-m.csv"
        $lastNamesFile = Join-Path $testDataPath "Lastnames.csv"

        if (Test-Path $femaleFile) {
            $femaleNames = @(Import-Csv $femaleFile | ForEach-Object { $_.FirstName })
        }

        if (Test-Path $maleFile) {
            $maleNames = @(Import-Csv $maleFile | ForEach-Object { $_.FirstName })
        }

        if (Test-Path $lastNamesFile) {
            $lastNames = @(Import-Csv $lastNamesFile | ForEach-Object { $_.LastName })
        }

        # Fallback to small arrays if files not found
        if ($femaleNames.Count -eq 0) {
            $femaleNames = @("Alice", "Diana", "Fiona", "Hannah", "Julia", "Laura", "Nancy", "Patricia", "Rachel", "Tina")
        }
        if ($maleNames.Count -eq 0) {
            $maleNames = @("Bob", "Charlie", "Edward", "George", "Ian", "Kevin", "Michael", "Oliver", "Quentin", "Steven")
        }
        if ($lastNames.Count -eq 0) {
            $lastNames = @("Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez")
        }

        # Combine first names (alternating male/female for variety)
        $allFirstNames = @()
        $maxCount = [Math]::Max($femaleNames.Count, $maleNames.Count)
        for ($i = 0; $i -lt $maxCount; $i++) {
            if ($i -lt $maleNames.Count) { $allFirstNames += $maleNames[$i] }
            if ($i -lt $femaleNames.Count) { $allFirstNames += $femaleNames[$i] }
        }

        $script:TestNameData = @{
            FirstNames = $allFirstNames
            LastNames = $lastNames
            TotalCombinations = $allFirstNames.Count * $lastNames.Count
        }

        Write-Verbose "Loaded $($allFirstNames.Count) first names and $($lastNames.Count) last names ($($script:TestNameData.TotalCombinations) combinations)"
    }

    return $script:TestNameData
}

function New-TestUser {
    <#
    .SYNOPSIS
        Generate a realistic test user object with unique display name

    .DESCRIPTION
        Uses reference CSV files with ~2000 first names and ~500 last names
        to generate ~1,000,000 unique name combinations. For datasets larger
        than available combinations, a numeric suffix is appended to ensure
        uniqueness.

        Also generates realistic employment data:
        - EmployeeType: ~20% Contractors, ~80% Employees
        - Company: Panoply for employees, one of five partner companies for contractors
        - AccountExpires: All contractors get expiry dates (1 week to 12 months)
                         ~15% of employees get expiry dates (resignations, 1 week to 3 months)
        - Pronouns: ~25% of users have pronouns populated (he/him, she/her, they/them, etc.)
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$Index,

        [Parameter(Mandatory=$false)]
        [string]$Domain = "panoply.local"
    )

    $nameData = Get-TestNameData
    $firstNames = $nameData.FirstNames
    $lastNames = $nameData.LastNames

    # Match the departments from src/JIM.Application/Resources/Departments.en.txt
    $departments = @("Marketing", "Operations", "Finance", "Sales", "Human Resources", "Procurement",
                     "Information Technology", "Research & Development", "Executive", "Legal", "Facilities", "Catering")
    $titles = @("Manager", "Director", "Analyst", "Specialist", "Coordinator", "Administrator", "Engineer", "Developer", "Consultant", "Associate")

    # Pronouns: ~25% of users have pronouns populated (optional field)
    # Distribution reflects realistic workplace adoption rates
    $pronounOptions = @("he/him", "she/her", "they/them", "he/they", "she/they")

    # Companies: Panoply is the main company (employees), partner companies for contractors
    # These are used for company-specific entitlement groups in Scenario 4
    $mainCompany = "Panoply"
    $partnerCompanies = @(
        "Nexus Dynamics",      # Technology consulting partner
        "Akinya",     # Cloud infrastructure provider
        "Rockhopper",      # Integration services partner
        "Stellar Logistics",   # Supply chain partner
        "Vertex Solutions"     # Professional services firm
    )

    # Calculate unique first/last name combination based on index
    # Use a distribution that spreads names across both first and last name pools
    # to avoid all users having the same surname for small datasets.
    #
    # Strategy: Use prime-based stepping to distribute names more evenly.
    # This ensures that even for small datasets (e.g., 100 users), we get
    # a good mix of different first AND last names.
    $firstNameCount = $firstNames.Count
    $lastNameCount = $lastNames.Count

    # Use index directly for first name (cycling through all first names)
    $firstNameIndex = $Index % $firstNameCount

    # Use a prime multiplier for last name to spread across the last name pool
    # Prime 97 ensures good distribution and avoids patterns
    $lastNameIndex = ($Index * 97) % $lastNameCount

    $firstName = $firstNames[$firstNameIndex]
    $lastName = $lastNames[$lastNameIndex]
    $department = $departments[$Index % $departments.Length]
    $title = $titles[$Index % $titles.Length]

    # Always include index in samAccountName for guaranteed uniqueness
    $samAccountName = "$($firstName.ToLower()).$($lastName.ToLower())$Index"
    $email = "$samAccountName@$Domain"
    # HrId is a GUID - use deterministic generation based on index for reproducibility
    $hrId = [guid]::new("{0:D8}-0000-0000-0000-000000000000" -f $Index).ToString()
    $employeeId = "EMP{0:D6}" -f $Index

    # The effective unique name cycle is min(firstNameCount, lastNameCount * gcd factor),
    # which in practice equals firstNameCount (~2000) since first names cycle at that interval.
    # Beyond that, (firstName, lastName) pairs repeat. Always include the index for datasets
    # larger than the first name pool to guarantee unique DNs in AD.
    $displayName = if ($Index -ge $firstNameCount) {
        "$firstName $lastName ($Index)"
    } else {
        "$firstName $lastName"
    }

    # Determine employee type: ~20% contractors, ~80% employees
    # Use deterministic assignment based on index for reproducibility
    $isContractor = ($Index % 5) -eq 0
    $employeeType = if ($isContractor) { "Contractor" } else { "Employee" }

    # Assign company: Employees work for Panoply, contractors come from partner companies
    # Contractors are distributed across the 5 partner companies deterministically
    $company = if ($isContractor) {
        $partnerIndex = ($Index / 5) % $partnerCompanies.Count
        $partnerCompanies[$partnerIndex]
    } else {
        $mainCompany
    }

    # Calculate account expiry date
    # - All contractors: 1 week to 12 months in the future
    # - ~15% of employees (those with resignations): 1 week to 3 months in the future
    # - Other employees: no expiry (null)
    #
    # Base date is a fixed epoch (not Get-Date) so CSV generation is byte-deterministic across runs,
    # which is what makes the CSV cache (Get-OrGenerate-TestCSV.ps1) safe. Any call site that needs a
    # "from-now" offset should compute it against Get-Date itself.
    # Chosen well into the future so that expiry dates derived from (epoch + 7..365 days) remain
    # future-dated for the foreseeable life of this test suite.
    $accountExpires = $null
    $now = [DateTime]::new(2030, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)

    if ($isContractor) {
        # Contractors: expiry between 1 week and 12 months
        # Use index to distribute expiry dates across the range
        $minDays = 7
        $maxDays = 365
        $daysToAdd = $minDays + (($Index * 17) % ($maxDays - $minDays))
        $accountExpires = $now.AddDays($daysToAdd)
    }
    elseif (($Index % 7) -eq 3) {
        # ~15% of employees have resignations (those where index % 7 == 3)
        # Expiry between 1 week and 3 months
        $minDays = 7
        $maxDays = 90
        $daysToAdd = $minDays + (($Index * 13) % ($maxDays - $minDays))
        $accountExpires = $now.AddDays($daysToAdd)
    }

    # Assign pronouns to ~25% of users (deterministic based on index)
    # Use modulo 4 to get exactly 25% coverage
    $pronouns = if (($Index % 4) -eq 0) {
        $pronounOptions[$Index % $pronounOptions.Count]
    } else {
        $null
    }

    return @{
        FirstName = $firstName
        LastName = $lastName
        SamAccountName = $samAccountName
        Email = $email
        Department = $department
        Title = $title
        HrId = $hrId
        EmployeeId = $employeeId
        DisplayName = $displayName
        EmployeeType = $employeeType
        Company = $company
        AccountExpires = $accountExpires
        Pronouns = $pronouns
    }
}

function Get-RandomSubset {
    <#
    .SYNOPSIS
        Get a random subset of items from an array
    #>
    param(
        [Parameter(Mandatory=$true)]
        [array]$Items,

        [Parameter(Mandatory=$true)]
        [int]$Count
    )

    if ($Count -ge $Items.Length) {
        return $Items
    }

    $shuffled = $Items | Sort-Object { Get-Random }
    return $shuffled[0..($Count - 1)]
}

# Progress bar functions for visual feedback during long operations

$script:ConnectorVolumeHelperImage = 'busybox:1.37.0'

function Clear-ConnectorFilesVolume {
    <#
    .SYNOPSIS
        Empty the contents of jim-connector-files-volume in place.

    .DESCRIPTION
        `docker compose down -v` cannot delete jim-connector-files-volume while
        samba-ad-primary / openldap-primary (started from test/integration/docker/docker-compose.integration-tests.yml
        and kept alive across scenarios in -Scenario All mode) hold it open. Without an
        in-place wipe the volume silently persists between runs and accumulates stale files.

        This helper launches a throwaway container from the jim.worker image (already pulled,
        no extra network fetch) with default (non-restricted) capabilities, mounts the volume,
        and rm -rf's its contents as root. This is safe to call whether jim.worker is running
        or not; falls back to the compose-project image name when the container isn't up.

        Call sites:
          - Run-IntegrationTests.ps1 Step 1 (top-level reset)
          - Run-IntegrationTests.ps1 Reset-JIMForNextScenario (between-scenarios lightweight reset)
        Both paths must start each scenario with an empty volume, so the helper lives here
        rather than being duplicated in both call sites.
    #>
    param()

    $workerImage = (docker inspect jim.worker --format '{{.Config.Image}}' 2>$null)
    if (-not $workerImage) {
        # Fall back to the compose-project image name if the container isn't up.
        $workerImage = 'jim-worker'
    }

    Write-Host "  Emptying jim-connector-files-volume contents (via throwaway $workerImage container)..." -ForegroundColor Gray
    docker run --rm --user 0 --entrypoint sh `
        -v jim-connector-files-volume:/vol $workerImage `
        -c 'rm -rf /vol/* /vol/.[!.]* 2>/dev/null; true' 2>&1 | Out-Null
}

function Write-FilesToConnectorVolume {
    <#
    .SYNOPSIS
        Copy one or more host files into the jim-connector-files-volume in a single rootless docker run.

    .DESCRIPTION
        Mounts the shared jim-connector-files-volume and the host source directory into a throwaway
        busybox container running as UID 1654, then copies the requested files in one shot. Files
        land owned by UID 1654 (the `app` user in jim.worker) because that is the UID doing the
        writes; no `chown` and no root is required in the pipeline.

        This replaces the per-file `docker exec -i -u app jim.worker tee ...` streaming pattern.
        For bulk seeding (e.g. the four CSVs in Step 5 of the integration harness) this cuts
        Scale100k50Groups Step 5 from ~15-25 s to ~1-3 s by eliminating the per-file docker exec round-
        trips and the PowerShell-pipe-over-stdin transfer.

        Correctness notes:
          - Each destination is removed via `rm -f` before the copy. Busybox `cp` overwrites
            contents in place rather than unlinking, so `rm -f` preserves the fresh-inode
            guarantee of the old implementation (defends against stale files owned by a
            different UID written e.g. via Samba/OpenLDAP).
          - Destination paths are passed through a generated shell script, not as `docker run`
            argv, so paths with spaces or special characters are safe provided they do not
            contain single quotes. CSV filenames never do.
          - The host source directory is bind-mounted read-only.

    .PARAMETER SourceDir
        Host directory containing all source files. Bind-mounted read-only into the helper
        container at /src.

    .PARAMETER Files
        Array of hashtables, each with:
          - SourceFile: filename (not path) inside $SourceDir
          - DestinationPath: absolute path inside the container volume, e.g.
            /connector-files/test-data/hr-users.csv

    .EXAMPLE
        Write-FilesToConnectorVolume -SourceDir $outputPath -Files @(
            @{ SourceFile = 'hr-users.csv';    DestinationPath = '/connector-files/test-data/hr-users.csv' }
            @{ SourceFile = 'departments.csv'; DestinationPath = '/connector-files/test-data/departments.csv' }
        )
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$SourceDir,

        [Parameter(Mandatory=$true)]
        [hashtable[]]$Files
    )

    if (-not (Test-Path -Path $SourceDir -PathType Container)) {
        throw "Source directory not found: $SourceDir"
    }

    if ($Files.Count -eq 0) {
        throw "Write-FilesToConnectorVolume called with no files."
    }

    $resolvedSourceDir = (Resolve-Path -Path $SourceDir).Path
    if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
        $resolvedSourceDir = $resolvedSourceDir.Replace('\','/')
    }

    # Caller provenance: captured once per call and used in three places —
    #   (a) the DarkGray breadcrumb line in the scenario transcript,
    #   (b) the .last-seed file inside the volume (for post-mortem forensics when
    #       a scenario transcript doesn't show the call, e.g. stray out-of-band writer),
    #   (c) failure messages so the throw points at the caller, not just the helper.
    $stack = Get-PSCallStack
    $callerScript = if ($stack.Count -ge 2 -and $stack[1].ScriptName) {
        Split-Path -Leaf $stack[1].ScriptName
    } else { '<unknown>' }
    $callerLine = if ($stack.Count -ge 2) { $stack[1].ScriptLineNumber } else { 0 }
    $callerCmd = if ($stack.Count -ge 2) { $stack[1].Command } else { '<unknown>' }
    $callerInfo = "${callerScript}:${callerLine} (${callerCmd})"

    # Capture expected per-file sizes on host now so we can verify post-copy that
    # what's in the volume matches what we asked busybox to copy. This catches two
    # failure modes: (1) a silent short-read / truncation in the cp path, and (2)
    # any out-of-band overwriter between this call's return and the caller's
    # first read. (1) is diagnosed immediately by the throw below; (2) requires
    # the caller to re-check later via Assert-ConnectorVolumeCsvParity.
    $expectedSizes = @{}
    $totalBytes = 0L

    # Collect destination metadata and build the copy script in one pass.
    $destDirs = [System.Collections.Generic.HashSet[string]]::new()
    $shellScript = [System.Text.StringBuilder]::new()
    $null = $shellScript.AppendLine('set -e')

    foreach ($entry in $Files) {
        $sourceFile = [string]$entry.SourceFile
        $destPath   = [string]$entry.DestinationPath

        if (-not $sourceFile) {
            throw "Write-FilesToConnectorVolume: entry is missing SourceFile. (caller: $callerInfo)"
        }
        if (-not $destPath) {
            throw "Write-FilesToConnectorVolume: entry is missing DestinationPath. (caller: $callerInfo)"
        }
        if (-not $destPath.StartsWith('/')) {
            throw "DestinationPath must be absolute (got '$destPath'). (caller: $callerInfo)"
        }
        if ($sourceFile -match '[\\/]') {
            throw "SourceFile must be a filename, not a path (got '$sourceFile'). Set SourceDir to the containing directory. (caller: $callerInfo)"
        }
        if ($sourceFile -match "'" -or $destPath -match "'") {
            throw "Write-FilesToConnectorVolume does not support paths containing single quotes. (caller: $callerInfo)"
        }

        $hostFile = Join-Path $resolvedSourceDir $sourceFile
        if (-not (Test-Path -Path $hostFile -PathType Leaf)) {
            throw "Source file not found: $hostFile (caller: $callerInfo)"
        }

        $sourceSize = (Get-Item -LiteralPath $hostFile).Length
        $expectedSizes[$destPath] = $sourceSize
        $totalBytes += $sourceSize

        $destDir = [System.IO.Path]::GetDirectoryName($destPath).Replace('\','/')
        if ($destDir -and $destDir -ne '/') {
            $null = $destDirs.Add($destDir)
        }

        # rm -f guarantees a fresh inode even if a stale file owned by a different
        # UID is already at the destination (e.g. from a previous run or a parallel
        # Samba/OpenLDAP write path). cp --remove-destination would be tidier but
        # busybox cp doesn't ship that flag; the explicit rm is portable.
        $null = $shellScript.AppendLine("rm -f '$destPath'")
        $null = $shellScript.AppendLine("cp '/src/$sourceFile' '$destPath'")
    }

    # Breadcrumb: prints to the transcript so a reader can see exactly which
    # script and line issued every seed, plus the payload shape. Intentionally
    # short so it doesn't clutter normal runs.
    Write-Host "  [seed] $callerInfo -> $($Files.Count) file(s), $totalBytes bytes from $resolvedSourceDir" -ForegroundColor DarkGray

    # .last-seed breadcrumb written INSIDE the container at the top of the shell
    # script. If the volume is ever found with unexpected contents, `docker exec
    # jim.worker cat /connector-files/.last-seed` reports the timestamp, the
    # PowerShell PID ($env:JIM_SEED_CALLER_PID), the caller location, and the
    # destination list. This catches out-of-band writers whose transcripts we
    # don't control — the .last-seed reflects whatever wrote the volume most
    # recently, regardless of which session ran it.
    $mkdirScript = [System.Text.StringBuilder]::new()
    foreach ($d in $destDirs) {
        $null = $mkdirScript.AppendLine("mkdir -p '$d'")
    }

    $destList = ($Files | ForEach-Object { $_.DestinationPath }) -join ' '
    $headerScript = [System.Text.StringBuilder]::new()
    # Use printf-with-literal-strings so no variable expansion happens on content
    # coming from PowerShell; caller_pid is injected via env, the rest are literals.
    $null = $headerScript.AppendLine('set -e')
    $null = $headerScript.AppendLine("printf '%s pid=%d ppid=%d caller_pid=%s caller=%s bytes=%d files=%s\n' `"`$(date -Iseconds)`" `$`$ `"`${PPID}`" `"`${JIM_SEED_CALLER_PID:-unset}`" '$callerInfo' '$totalBytes' '$destList' > /connector-files/.last-seed")

    # Prepend header + mkdirs so provenance is captured even if a later cp fails,
    # and directories exist before cp runs.
    $fullScript = $headerScript.ToString() + $mkdirScript.ToString() + $shellScript.ToString()

    $mountSpec = "${resolvedSourceDir}:/src:ro"
    $volumeMount = 'jim-connector-files-volume:/connector-files'

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'docker'
    $psi.ArgumentList.Add('run')
    $psi.ArgumentList.Add('--rm')
    # Expose the PowerShell PID to the shell so .last-seed records it — useful
    # when multiple pwsh sessions could contend for the same volume.
    $psi.ArgumentList.Add('-e')
    $psi.ArgumentList.Add("JIM_SEED_CALLER_PID=$PID")
    $psi.ArgumentList.Add('--user')
    $psi.ArgumentList.Add('1654:1654')
    $psi.ArgumentList.Add('-v')
    $psi.ArgumentList.Add($volumeMount)
    $psi.ArgumentList.Add('-v')
    $psi.ArgumentList.Add($mountSpec)
    $psi.ArgumentList.Add($script:ConnectorVolumeHelperImage)
    $psi.ArgumentList.Add('sh')
    $psi.ArgumentList.Add('-c')
    $psi.ArgumentList.Add($fullScript)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    try {
        $stdout = $proc.StandardOutput.ReadToEnd()
        $stderr = $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0) {
            $fileList = ($Files | ForEach-Object { $_.DestinationPath }) -join ', '
            $hint = ''
            if ($stderr -match 'Permission denied') {
                $hint = " This usually means a destination file or its parent directory is owned by a UID other than 1654 (e.g. from a past 'docker cp' into the volume). Reset the volume with 'docker volume rm jim-connector-files-volume' after stopping jim.worker / jim.web, or chown the affected path from a root-privileged sidecar."
            }
            throw "docker run helper (image $script:ConnectorVolumeHelperImage) failed with exit code $($proc.ExitCode) while seeding [$fileList] from $callerInfo.$hint stderr: $stderr. stdout: $stdout"
        }
    }
    finally {
        $proc.Dispose()
    }

    # Post-seed size verification. Stat every destination inside the volume and
    # compare against the host source size captured above. A mismatch here would
    # mean the cp path did something weird (short read, silent partial write),
    # or — if sizes match now but diverge later — that a subsequent out-of-band
    # writer overwrote the file. Either way, fail fast and loudly so the hour-
    # long sync downstream doesn't proceed on corrupted input.
    $statPaths = ($Files | ForEach-Object { "'$($_.DestinationPath)'" }) -join ' '
    $statScript = "for f in $statPaths; do printf '%s %s\n' `"`$f`" `"`$(stat -c '%s' `"`$f`")`"; done"
    $statPsi = New-Object System.Diagnostics.ProcessStartInfo
    $statPsi.FileName = 'docker'
    $statPsi.ArgumentList.Add('run')
    $statPsi.ArgumentList.Add('--rm')
    $statPsi.ArgumentList.Add('--user')
    $statPsi.ArgumentList.Add('1654:1654')
    $statPsi.ArgumentList.Add('-v')
    $statPsi.ArgumentList.Add($volumeMount)
    $statPsi.ArgumentList.Add($script:ConnectorVolumeHelperImage)
    $statPsi.ArgumentList.Add('sh')
    $statPsi.ArgumentList.Add('-c')
    $statPsi.ArgumentList.Add($statScript)
    $statPsi.UseShellExecute = $false
    $statPsi.RedirectStandardOutput = $true
    $statPsi.RedirectStandardError = $true

    $statProc = [System.Diagnostics.Process]::Start($statPsi)
    try {
        $statStdout = $statProc.StandardOutput.ReadToEnd()
        $statStderr = $statProc.StandardError.ReadToEnd()
        $statProc.WaitForExit()
        if ($statProc.ExitCode -ne 0) {
            throw "Post-seed verification failed: stat helper exited $($statProc.ExitCode). stderr: $statStderr. stdout: $statStdout. Caller: $callerInfo"
        }

        $mismatches = @()
        foreach ($line in ($statStdout -split "`n")) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $parts = $line.Trim() -split '\s+', 2
            if ($parts.Count -ne 2) { continue }
            $path = $parts[0]
            $actual = [int64]$parts[1]
            $expected = $expectedSizes[$path]
            if ($null -eq $expected) { continue }
            if ($actual -ne $expected) {
                $mismatches += "  ${path}: host=$expected bytes, volume=$actual bytes"
            }
        }

        if ($mismatches.Count -gt 0) {
            $details = $mismatches -join "`n"
            throw @"
Post-seed size verification detected corruption. Caller: $callerInfo
Source dir: $resolvedSourceDir
Mismatches:
$details
The File connector will read truncated data. Investigate the seed path (busybox cp
failure, bind-mount issue) or any out-of-band writer to jim-connector-files-volume
BEFORE re-running (root cause will recur on retry).
"@
        }
    }
    finally {
        $statProc.Dispose()
    }
}

function Write-FileToConnectorVolume {
    <#
    .SYNOPSIS
        Copy a single host file into /connector-files inside the shared volume with correct ownership.

    .DESCRIPTION
        Thin wrapper around Write-FilesToConnectorVolume for call sites that only need to write
        one file. See Write-FilesToConnectorVolume for the full design rationale.

    .PARAMETER SourcePath
        Host path to the file to copy in.

    .PARAMETER DestinationPath
        Absolute path inside the container, e.g. /connector-files/test-data/hr-users.csv.
        The parent directory is created if missing.

    .EXAMPLE
        Write-FileToConnectorVolume -SourcePath $csvPath -DestinationPath /connector-files/test-data/hr-users.csv
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$SourcePath,

        [Parameter(Mandatory=$true)]
        [string]$DestinationPath
    )

    if (-not (Test-Path -Path $SourcePath -PathType Leaf)) {
        throw "Source file not found: $SourcePath"
    }

    $sourceDir  = [System.IO.Path]::GetDirectoryName($SourcePath)
    $sourceFile = [System.IO.Path]::GetFileName($SourcePath)

    Write-FilesToConnectorVolume -SourceDir $sourceDir -Files @(
        @{ SourceFile = $sourceFile; DestinationPath = $DestinationPath }
    )
}

function Copy-CsvToConnectorFiles {
    <#
    .SYNOPSIS
        Seed a CSV file into /connector-files/test-data/ with correct ownership.

    .DESCRIPTION
        Thin wrapper around Write-FileToConnectorVolume that defaults the destination
        to /connector-files/test-data/<SourceBasename>. Preserved for backwards
        compatibility with existing scenario scripts; new callers can use
        Write-FileToConnectorVolume directly when they need a non-standard destination.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$SourcePath,

        [Parameter(Mandatory=$false)]
        [string]$DestinationName
    )

    if (-not $DestinationName) {
        $DestinationName = [System.IO.Path]::GetFileName($SourcePath)
    }

    Write-FileToConnectorVolume -SourcePath $SourcePath -DestinationPath "/connector-files/test-data/$DestinationName"
}

function Assert-ConnectorVolumeCsvParity {
    <#
    .SYNOPSIS
        Verify volume CSV file sizes match host expectations; throw on divergence.

    .DESCRIPTION
        Defence-in-depth check meant to run immediately before a CSV-consuming run
        profile fires. Write-FilesToConnectorVolume already verifies sizes at seed
        time; this helper catches the narrower failure mode where the volume was
        seeded correctly but something truncated the file between seed and read
        (the 08:47:22 Scale100k50Groups incident we investigated).

        On mismatch, throws with the container path, expected (host) size, and
        actual (volume) size so the transcript captures exactly which file
        diverged. Also reads /connector-files/.last-seed from the volume and
        surfaces its contents — that file records the most recent seeder's PID,
        caller, and payload, so the failure message points at the out-of-band
        writer if one exists.

    .PARAMETER Pairs
        Array of hashtables each with HostPath (absolute host path to the file
        we SHOULD be reading) and ContainerPath (absolute /connector-files path
        inside the volume). All files listed are checked in a single docker exec.

    .EXAMPLE
        Assert-ConnectorVolumeCsvParity -Pairs @(
            @{ HostPath = "$testDataPath/hr-users.csv";         ContainerPath = '/connector-files/test-data/hr-users.csv' }
            @{ HostPath = "$testDataPath/training-records.csv"; ContainerPath = '/connector-files/test-data/training-records.csv' }
        )
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable[]]$Pairs
    )

    if ($Pairs.Count -eq 0) { return }

    foreach ($p in $Pairs) {
        if (-not $p.HostPath -or -not $p.ContainerPath) {
            throw "Assert-ConnectorVolumeCsvParity: each pair must have HostPath and ContainerPath."
        }
        if (-not (Test-Path -Path $p.HostPath -PathType Leaf)) {
            throw "Assert-ConnectorVolumeCsvParity: host file not found: $($p.HostPath)"
        }
    }

    # Build a single shell command that prints "<path> <size>" per container path.
    # Running one docker exec is meaningfully cheaper than one per file when this
    # helper is called before every CSV-consuming run profile.
    $containerPaths = ($Pairs | ForEach-Object { "'$($_.ContainerPath)'" }) -join ' '
    $statCmd = "for f in $containerPaths; do printf '%s %s\n' `"`$f`" `"`$(stat -c '%s' `"`$f`" 2>/dev/null || echo MISSING)`"; done"

    $statOutput = & docker exec jim.worker sh -c $statCmd 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Assert-ConnectorVolumeCsvParity: docker exec jim.worker failed: $statOutput"
    }

    $actualByPath = @{}
    foreach ($line in ($statOutput -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line.Trim() -split '\s+', 2
        if ($parts.Count -ne 2) { continue }
        $actualByPath[$parts[0]] = $parts[1]
    }

    $mismatches = @()
    foreach ($p in $Pairs) {
        $expected = (Get-Item -LiteralPath $p.HostPath).Length
        $actualRaw = $actualByPath[$p.ContainerPath]
        if ($null -eq $actualRaw -or $actualRaw -eq 'MISSING') {
            $mismatches += "  $($p.ContainerPath): host=$expected bytes, volume=MISSING"
            continue
        }
        $actual = [int64]$actualRaw
        if ($actual -ne $expected) {
            $mismatches += "  $($p.ContainerPath): host=$expected bytes, volume=$actual bytes"
        }
    }

    if ($mismatches.Count -eq 0) { return }

    # Divergence detected — grab the .last-seed breadcrumb and fail loudly.
    $lastSeed = & docker exec jim.worker sh -c 'cat /connector-files/.last-seed 2>/dev/null || echo "(.last-seed not present)"' 2>&1
    $details = $mismatches -join "`n"
    throw @"
Connector volume CSV parity check FAILED. One or more files have diverged from the
host source between the most recent seed and this read. Do not proceed with the
pending run profile — the File connector will read truncated or stale data.

Mismatches:
$details

Most recent seed provenance (from /connector-files/.last-seed):
  $lastSeed

Next steps:
  - Check for stray pwsh sessions / background jobs that might have issued another seed.
  - Grep the scenario transcript for '[seed]' lines between the last good read and now.
  - docker exec jim.worker ls -la /connector-files/test-data to see current file state.
"@
}

function Write-ProgressBar {
    <#
    .SYNOPSIS
        Write a progress bar to the console

    .DESCRIPTION
        Displays a visual progress bar with percentage, elapsed time, and optional ETA.
        Uses carriage return to update in place.

    .PARAMETER Activity
        The activity being performed (shown above the bar)

    .PARAMETER Status
        Current status message (shown on the bar line)

    .PARAMETER PercentComplete
        Percentage complete (0-100)

    .PARAMETER SecondsElapsed
        Seconds elapsed since operation started

    .PARAMETER ShowETA
        Whether to show estimated time remaining
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Activity,

        [Parameter(Mandatory=$false)]
        [string]$Status = "",

        [Parameter(Mandatory=$true)]
        [int]$PercentComplete,

        [Parameter(Mandatory=$false)]
        [int]$SecondsElapsed = 0,

        [Parameter(Mandatory=$false)]
        [switch]$ShowETA
    )

    # Clamp percentage to 0-100
    $PercentComplete = [Math]::Max(0, [Math]::Min(100, $PercentComplete))

    # Calculate bar width (leave room for percentage and time)
    $barWidth = 30
    $filledWidth = [Math]::Floor($barWidth * $PercentComplete / 100)
    $emptyWidth = $barWidth - $filledWidth

    # Build the bar
    $filled = "█" * $filledWidth
    $empty = "░" * $emptyWidth
    $bar = "[$filled$empty]"

    # Format elapsed time
    $elapsed = ""
    if ($SecondsElapsed -gt 0) {
        $ts = [TimeSpan]::FromSeconds($SecondsElapsed)
        $elapsed = " {0:mm\:ss}" -f $ts
    }

    # Calculate ETA
    $eta = ""
    if ($ShowETA -and $PercentComplete -gt 0 -and $PercentComplete -lt 100 -and $SecondsElapsed -gt 0) {
        $estimatedTotal = $SecondsElapsed / ($PercentComplete / 100)
        $remaining = $estimatedTotal - $SecondsElapsed
        if ($remaining -gt 0) {
            $ts = [TimeSpan]::FromSeconds($remaining)
            $eta = " ETA: {0:mm\:ss}" -f $ts
        }
    }

    # Build status line
    $statusText = if ($Status) { " $Status" } else { "" }
    $line = "`r  $bar $PercentComplete%$elapsed$eta$statusText"

    # Pad to clear any previous longer text
    $line = $line.PadRight(100)

    Write-Host $line -NoNewline -ForegroundColor Cyan
}

function Complete-ProgressBar {
    <#
    .SYNOPSIS
        Complete a progress bar and move to next line

    .PARAMETER Success
        Whether the operation succeeded

    .PARAMETER Message
        Completion message to display
    #>
    param(
        [Parameter(Mandatory=$false)]
        [bool]$Success = $true,

        [Parameter(Mandatory=$false)]
        [string]$Message = ""
    )

    # Clear the progress bar line
    Write-Host "`r$(' ' * 100)`r" -NoNewline

    # Write completion message
    if ($Success) {
        $icon = "✓"
        $color = "Green"
    } else {
        $icon = "✗"
        $color = "Red"
    }

    if ($Message) {
        Write-Host "  $icon $Message" -ForegroundColor $color
    }
}

function Start-TimedOperation {
    <#
    .SYNOPSIS
        Start tracking a timed operation

    .DESCRIPTION
        Returns a hashtable with start time that can be passed to Write-OperationProgress

    .PARAMETER Name
        Name of the operation

    .PARAMETER TotalSteps
        Total number of steps (for percentage calculation)
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$false)]
        [int]$TotalSteps = 100
    )

    return @{
        Name = $Name
        StartTime = Get-Date
        TotalSteps = $TotalSteps
        CurrentStep = 0
    }
}

function Update-OperationProgress {
    <#
    .SYNOPSIS
        Update progress for a timed operation

    .PARAMETER Operation
        The operation hashtable from Start-TimedOperation

    .PARAMETER CurrentStep
        Current step number

    .PARAMETER Status
        Current status message
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Operation,

        [Parameter(Mandatory=$false)]
        [int]$CurrentStep = -1,

        [Parameter(Mandatory=$false)]
        [string]$Status = ""
    )

    if ($CurrentStep -ge 0) {
        $Operation.CurrentStep = $CurrentStep
    }

    $elapsed = ((Get-Date) - $Operation.StartTime).TotalSeconds
    $percent = if ($Operation.TotalSteps -gt 0) {
        [Math]::Floor(($Operation.CurrentStep / $Operation.TotalSteps) * 100)
    } else {
        0
    }

    Write-ProgressBar -Activity $Operation.Name -Status $Status -PercentComplete $percent -SecondsElapsed $elapsed -ShowETA
}

function Complete-TimedOperation {
    <#
    .SYNOPSIS
        Complete a timed operation and show final time

    .PARAMETER Operation
        The operation hashtable from Start-TimedOperation

    .PARAMETER Success
        Whether the operation succeeded

    .PARAMETER Message
        Optional completion message (defaults to operation name + duration)
    #>
    param(
        [Parameter(Mandatory=$true)]
        [hashtable]$Operation,

        [Parameter(Mandatory=$false)]
        [bool]$Success = $true,

        [Parameter(Mandatory=$false)]
        [string]$Message = ""
    )

    $elapsed = ((Get-Date) - $Operation.StartTime).TotalSeconds
    $ts = [TimeSpan]::FromSeconds($elapsed)
    $duration = "{0:mm\:ss}" -f $ts

    if (-not $Message) {
        $Message = "$($Operation.Name) completed in $duration"
    } else {
        $Message = "$Message ($duration)"
    }

    Complete-ProgressBar -Success $Success -Message $Message
}

function Write-Spinner {
    <#
    .SYNOPSIS
        Write a spinner animation for indeterminate progress

    .PARAMETER Message
        Message to display next to spinner

    .PARAMETER Frame
        Current frame number (cycles through spinner characters)
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Message,

        [Parameter(Mandatory=$true)]
        [int]$Frame
    )

    $spinChars = @("⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏")
    $spinner = $spinChars[$Frame % $spinChars.Length]

    Write-Host "`r  $spinner $Message".PadRight(80) -NoNewline -ForegroundColor Yellow
}

function Assert-ActivitySuccess {
    <#
    .SYNOPSIS
        Assert that an Activity completed successfully (status = 'Complete')

    .DESCRIPTION
        Validates that a JIM Activity completed without warnings or errors.
        This prevents integration tests from silently passing when Activities
        have warnings/errors that should be investigated.

    .PARAMETER ActivityId
        The Activity ID (GUID) to validate

    .PARAMETER Name
        A friendly name for the operation (used in error messages)

    .PARAMETER AllowWarnings
        If specified, allows 'CompleteWithWarning' status to pass.
        Use sparingly - warnings often indicate real issues.

    .EXAMPLE
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "CSV Full Import"

        Validates that the CSV Full Import completed successfully.

    .EXAMPLE
        Assert-ActivitySuccess -ActivityId $syncResult.activityId -Name "Delta Sync" -AllowWarnings

        Validates that Delta Sync completed, allowing any warnings (but not errors).

    .EXAMPLE
        Assert-ActivitySuccess -ActivityId $importResult.activityId -Name "Delta Import" `
            -AllowWarnings -AllowedWarningTypes @('DeltaImportFallbackToFullImport')

        Validates that the Delta Import completed, allowing CompleteWithWarning ONLY if
        all warning RPEIs have the DeltaImportFallbackToFullImport error type.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ActivityId,

        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$false)]
        [switch]$AllowWarnings,

        [Parameter(Mandatory=$false)]
        [string[]]$AllowedWarningTypes
    )

    # Fetch the Activity details
    $activity = Get-JIMActivity -Id $ActivityId

    if (-not $activity) {
        throw "Activity not found: $ActivityId for '$Name'"
    }

    $status = $activity.status

    # Define acceptable statuses
    $acceptableStatuses = @('Complete')
    if ($AllowWarnings) {
        $acceptableStatuses += 'CompleteWithWarning'
    }

    # Check if status is acceptable
    if ($status -in $acceptableStatuses) {
        # If CompleteWithWarning and AllowedWarningTypes specified, verify all warnings are of allowed types
        if ($status -eq 'CompleteWithWarning' -and $AllowedWarningTypes) {
            $errorItems = Get-JIMActivity -Id $ActivityId -ExecutionItems |
                Where-Object { $_.errorType -and $_.errorType -ne 'NotSet' }

            $unexpectedWarnings = $errorItems | Where-Object { $_.errorType -notin $AllowedWarningTypes }
            if ($unexpectedWarnings) {
                $unexpectedTypes = ($unexpectedWarnings | ForEach-Object { $_.errorType } | Select-Object -Unique) -join ', '
                throw "Activity '$Name' completed with unexpected warning types: $unexpectedTypes. " +
                    "Only these warning types are allowed: $($AllowedWarningTypes -join ', ') (ActivityId: $ActivityId)"
            }
            Write-Host "  ✓ $Name completed with expected warning (Status: $status, Warning: $($AllowedWarningTypes -join ', '))" -ForegroundColor Green
            return
        }

        Write-Host "  ✓ $Name completed successfully (Status: $status)" -ForegroundColor Green
        return  # Success - no output (callers don't use return value)
    }

    # Activity did not complete successfully - gather diagnostic information
    $errorDetails = @()
    $errorDetails += "Activity '$Name' ended with status: $status"
    $errorDetails += "Activity ID: $ActivityId"

    if ($activity.errorMessage) {
        $errorDetails += "Error Message: $($activity.errorMessage)"
    }

    if ($activity.message) {
        $errorDetails += "Status Message: $($activity.message)"
    }

    # Get execution statistics if available
    try {
        $stats = Get-JIMActivityStats -ActivityId $ActivityId -ErrorAction SilentlyContinue
        if ($stats) {
            $errorDetails += "Statistics:"
            $errorDetails += "  - Objects Processed: $($stats.totalObjectsProcessed)"
            $errorDetails += "  - Object Changes: $($stats.totalObjectChangeCount)"
            $errorDetails += "  - Unchanged: $($stats.totalUnchanged)"
            $errorDetails += "  - Errors: $($stats.totalObjectErrors)"
            # Import stats
            if ($stats.totalCsoAdds -gt 0) { $errorDetails += "  - CSO Adds: $($stats.totalCsoAdds)" }
            if ($stats.totalCsoUpdates -gt 0) { $errorDetails += "  - CSO Updates: $($stats.totalCsoUpdates)" }
            if ($stats.totalCsoDeletes -gt 0) { $errorDetails += "  - CSO Deletes: $($stats.totalCsoDeletes)" }
            # Sync stats
            if ($stats.totalProjections -gt 0) { $errorDetails += "  - Projections: $($stats.totalProjections)" }
            if ($stats.totalJoins -gt 0) { $errorDetails += "  - Joins: $($stats.totalJoins)" }
            if ($stats.totalAttributeFlows -gt 0) { $errorDetails += "  - Attribute Flows: $($stats.totalAttributeFlows)" }
            if ($stats.totalDisconnections -gt 0) { $errorDetails += "  - Disconnections: $($stats.totalDisconnections)" }
            if ($stats.totalDisconnectedOutOfScope -gt 0) { $errorDetails += "  - Disconnected (Out of Scope): $($stats.totalDisconnectedOutOfScope)" }
            if ($stats.totalOutOfScopeRetainJoin -gt 0) { $errorDetails += "  - Out of Scope (Retain Join): $($stats.totalOutOfScopeRetainJoin)" }
            if ($stats.totalDriftCorrections -gt 0) { $errorDetails += "  - Drift Corrections: $($stats.totalDriftCorrections)" }
            # Export stats
            if ($stats.totalExported -gt 0) { $errorDetails += "  - Exported: $($stats.totalExported)" }
            if ($stats.totalDeprovisioned -gt 0) { $errorDetails += "  - Deprovisioned: $($stats.totalDeprovisioned)" }
            # Direct creation stats
            if ($stats.totalCreated -gt 0) { $errorDetails += "  - Created: $($stats.totalCreated)" }

            # If there are errors, try to get the first few error items
            if ($stats.totalObjectErrors -gt 0) {
                $errorItems = Get-JIMActivity -Id $ActivityId -ExecutionItems |
                    Where-Object { $_.errorType -and $_.errorType -ne 'NotSet' } |
                    Select-Object -First 5

                if ($errorItems) {
                    $errorDetails += "First error items:"
                    foreach ($item in $errorItems) {
                        $errorDetails += "  - Error: $($item.errorType)"
                        if ($item.errorMessage) {
                            $errorDetails += "    Message: $($item.errorMessage)"
                        }
                        if ($item.snapshotDisplayName) {
                            $errorDetails += "    Object: $($item.snapshotDisplayName)"
                        }
                        elseif ($item.connectedSystemObjectExternalId) {
                            $errorDetails += "    ExtId: $($item.connectedSystemObjectExternalId)"
                        }
                    }
                }
            }
        }
    }
    catch {
        # Statistics may not be available for all activity types
        Write-Verbose "Could not retrieve activity statistics: $_"
    }

    # Output error details
    Write-Host "  ✗ $Name FAILED" -ForegroundColor Red
    foreach ($detail in $errorDetails) {
        Write-Host "    $detail" -ForegroundColor Red
    }

    throw "Activity '$Name' did not complete successfully. Status: $status (ActivityId: $ActivityId)"
}

function Assert-ExportSuccess {
    <#
    .SYNOPSIS
        Assert that an export Activity completed successfully with no export failures.

    .DESCRIPTION
        Validates both the activity status (must be 'Complete') AND the export outcome
        (no failures reported in the activity message). This catches cases where the
        activity status is 'Complete' but exports actually failed silently.

    .PARAMETER ActivityId
        The Activity ID (GUID) to validate

    .PARAMETER Name
        A friendly name for the operation (used in error messages)

    .EXAMPLE
        Assert-ExportSuccess -ActivityId $exportResult.activityId -Name "LDAP Export (Joiner)"
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ActivityId,

        [Parameter(Mandatory=$true)]
        [string]$Name
    )

    # First validate activity status
    Assert-ActivitySuccess -ActivityId $ActivityId -Name $Name

    # Then validate no export failures in the activity message
    $activity = Get-JIMActivity -Id $ActivityId
    if ($activity.message -match '(\d+) failed' -and [int]$Matches[1] -gt 0) {
        Write-Host "  ✗ $Name had $($Matches[1]) export failure(s)" -ForegroundColor Red
        Write-Host "    Activity message: $($activity.message)" -ForegroundColor Red
        Write-Host "    Activity ID: $ActivityId" -ForegroundColor Red
        throw "Export '$Name' had $($Matches[1]) failure(s). Activity message: $($activity.message) (ActivityId: $ActivityId)"
    }
}

function Assert-ExportRpeisHaveCsoLink {
    <#
    .SYNOPSIS
        Assert that every Exported / Deprovisioned RPEI for a just-completed export Activity
        has its ConnectedSystemObjectId FK populated, and that the linked
        ConnectedSystemObjectChange row (when present) also has its ConnectedSystemObjectId populated.

    .DESCRIPTION
        Guards against a regression where the export pipeline writes RPEIs through the raw-SQL /
        COPY bulk-insert path with only the navigation property set. EF's automatic FK fix-up
        does not run on raw SQL, so the scalar FK ends up NULL and the audit trail loses its
        link from the Activity into the CSO detail page (issue #683).

        This check MUST be performed against a real Postgres instance; in-memory EF auto-tracks
        navigations and would silently mask the bug. The helper queries the database directly
        via psql in the jim.database container.

        Scope: only checks Exported (11) and Deprovisioned (12) RPEIs. Other ObjectChangeType
        values legitimately have NULL ConnectedSystemObjectId in some flows (e.g. CSO already
        deleted via ON DELETE SET NULL on a historical activity).

    .PARAMETER ActivityId
        The Activity ID (GUID) of a just-completed export Activity.

    .PARAMETER Name
        A friendly name for the export Activity (used in output messages).

    .EXAMPLE
        Assert-ExportRpeisHaveCsoLink -ActivityId $exportResult.activityId -Name "Target Export"
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ActivityId,

        [Parameter(Mandatory=$true)]
        [string]$Name
    )

    # Validate ActivityId is a well-formed GUID before substituting into SQL.
    # The query interpolates ActivityId directly because psql -c does not support bind parameters
    # over docker compose exec; restricting to a GUID closes the only realistic injection vector.
    $parsedId = [Guid]::Empty
    if (-not [Guid]::TryParse($ActivityId, [ref]$parsedId)) {
        throw "Assert-ExportRpeisHaveCsoLink: ActivityId '$ActivityId' is not a valid GUID."
    }
    $safeActivityId = $parsedId.ToString()

    # ObjectChangeType values: Exported = 11, Deprovisioned = 12
    $rpeiQuery = @"
SELECT COUNT(*) FROM "ActivityRunProfileExecutionItems"
WHERE "ActivityId" = '$safeActivityId'
  AND "ObjectChangeType" IN (11, 12)
  AND "ConnectedSystemObjectId" IS NULL
  AND "ErrorType" IS NULL;
"@

    $rpeiNullCount = docker compose exec -T jim.database psql -t -A -U jim -d jim -c $rpeiQuery 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Assert-ExportRpeisHaveCsoLink: psql query failed for '$Name' (ActivityId: $ActivityId). Output: $rpeiNullCount"
    }

    $rpeiNullCount = [int]($rpeiNullCount | Out-String).Trim()
    if ($rpeiNullCount -gt 0) {
        Write-Host "  ✗ $Name - $rpeiNullCount export RPEI(s) persisted with NULL ConnectedSystemObjectId" -ForegroundColor Red
        Write-Host "    This breaks audit-trail navigation from Operations into CSO detail (#683)" -ForegroundColor Red
        Write-Host "    ActivityId: $ActivityId" -ForegroundColor Red
        throw "Export '$Name' persisted $rpeiNullCount RPEI row(s) with NULL ConnectedSystemObjectId (ActivityId: $ActivityId)"
    }

    # Also check the linked ConnectedSystemObjectChange rows. These are written through the same
    # raw-SQL/COPY path and exhibit the same defect (the change row's FK was set on the navigation
    # only, not the scalar). Restrict to changes linked to the export Activity's RPEIs.
    $changeQuery = @"
SELECT COUNT(*) FROM "ConnectedSystemObjectChanges" c
JOIN "ActivityRunProfileExecutionItems" r ON c."ActivityRunProfileExecutionItemId" = r."Id"
WHERE r."ActivityId" = '$safeActivityId'
  AND r."ObjectChangeType" IN (11, 12)
  AND r."ErrorType" IS NULL
  AND c."ConnectedSystemObjectId" IS NULL;
"@

    $changeNullCount = docker compose exec -T jim.database psql -t -A -U jim -d jim -c $changeQuery 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Assert-ExportRpeisHaveCsoLink: psql query failed (change rows) for '$Name' (ActivityId: $ActivityId). Output: $changeNullCount"
    }

    $changeNullCount = [int]($changeNullCount | Out-String).Trim()
    if ($changeNullCount -gt 0) {
        Write-Host "  ✗ $Name - $changeNullCount ConnectedSystemObjectChange row(s) persisted with NULL ConnectedSystemObjectId" -ForegroundColor Red
        Write-Host "    Causality Tree will fail to render attribute-level export detail (#683)" -ForegroundColor Red
        Write-Host "    ActivityId: $ActivityId" -ForegroundColor Red
        throw "Export '$Name' persisted $changeNullCount ConnectedSystemObjectChange row(s) with NULL ConnectedSystemObjectId (ActivityId: $ActivityId)"
    }

    Write-Host "  ✓ $Name - all export RPEIs and change rows have populated ConnectedSystemObjectId FKs" -ForegroundColor Green
}

function Assert-ActivityHasChanges {
    <#
    .SYNOPSIS
        Assert that an Activity has the expected number and type of changes

    .DESCRIPTION
        Validates that a JIM Activity recorded specific change types with expected counts.
        This ensures that run profile executions actually processed the expected data,
        not just that they completed successfully.

    .PARAMETER ActivityId
        The Activity ID (GUID) to validate

    .PARAMETER Name
        A friendly name for the operation (used in messages)

    .PARAMETER ExpectedChangeType
        The ObjectChangeType to look for (e.g., 'Added', 'Deleted', 'Updated', 'Projected', 'Exported', 'Deprovisioned')

    .PARAMETER MinCount
        Minimum number of changes expected (default: 1)

    .PARAMETER MaxCount
        Maximum number of changes expected (optional, no limit if not specified)

    .PARAMETER ExactCount
        Exact number of changes expected (overrides MinCount/MaxCount)

    .EXAMPLE
        Assert-ActivityHasChanges -ActivityId $importResult.activityId -Name "CSV Import" -ExpectedChangeType "Added" -MinCount 5

        Validates that the import added at least 5 objects.

    .EXAMPLE
        Assert-ActivityHasChanges -ActivityId $syncResult.activityId -Name "Delta Sync" -ExpectedChangeType "Deleted" -ExactCount 1

        Validates that exactly 1 deletion was processed.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ActivityId,

        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$true)]
        [ValidateSet('Added', 'Updated', 'Deleted', 'Projected', 'Joined', 'AttributeFlow', 'Disconnected',
                     'DisconnectedOutOfScope', 'OutOfScopeRetainJoin', 'DriftCorrection',
                     'Exported', 'Deprovisioned', 'NoChange', 'PendingExport', 'PendingExportConfirmed')]
        [string]$ExpectedChangeType,

        [Parameter(Mandatory=$false)]
        [int]$MinCount = 1,

        [Parameter(Mandatory=$false)]
        [int]$MaxCount = -1,

        [Parameter(Mandatory=$false)]
        [int]$ExactCount = -1
    )

    # Get activity stats to check change counts
    $stats = Get-JIMActivityStats -ActivityId $ActivityId

    if (-not $stats) {
        throw "Could not retrieve statistics for Activity '$Name' (ID: $ActivityId)"
    }

    # Map change type to stats property
    $actualCount = switch ($ExpectedChangeType) {
        'Added' { $stats.totalCsoAdds }
        'Updated' { $stats.totalCsoUpdates }
        'Deleted' { $stats.totalCsoDeletes }
        'Projected' { $stats.totalProjections }
        'Joined' { $stats.totalJoins }
        'AttributeFlow' { $stats.totalAttributeFlows }
        'Disconnected' { $stats.totalDisconnections }
        'DisconnectedOutOfScope' { $stats.totalDisconnectedOutOfScope }
        'OutOfScopeRetainJoin' { $stats.totalOutOfScopeRetainJoin }
        'DriftCorrection' { $stats.totalDriftCorrections }
        'Exported' { $stats.totalExported }
        'Deprovisioned' { $stats.totalDeprovisioned }
        'NoChange' { $stats.totalUnchanged }
        'PendingExport' { $stats.totalPendingExports }
        'PendingExportConfirmed' { $stats.totalPendingExportsConfirmed }
        default { throw "Unknown change type: $ExpectedChangeType" }
    }

    # Validate count
    if ($ExactCount -ge 0) {
        # Exact count validation
        if ($actualCount -ne $ExactCount) {
            Write-Host "  ✗ $Name - Expected exactly $ExactCount $ExpectedChangeType changes, but got $actualCount" -ForegroundColor Red
            throw "Activity '$Name' expected exactly $ExactCount $ExpectedChangeType changes, but got $actualCount (ActivityId: $ActivityId)"
        }
        Write-Host "  ✓ $Name - $actualCount $ExpectedChangeType changes (expected exactly $ExactCount)" -ForegroundColor Green
    }
    else {
        # Min/Max validation
        $failed = $false
        $message = ""

        if ($actualCount -lt $MinCount) {
            $failed = $true
            $message = "Expected at least $MinCount $ExpectedChangeType changes, but got $actualCount"
        }
        elseif ($MaxCount -ge 0 -and $actualCount -gt $MaxCount) {
            $failed = $true
            $message = "Expected at most $MaxCount $ExpectedChangeType changes, but got $actualCount"
        }

        if ($failed) {
            Write-Host "  ✗ $Name - $message" -ForegroundColor Red
            throw "Activity '$Name' $message (ActivityId: $ActivityId)"
        }

        $rangeMsg = if ($MaxCount -ge 0) { "$MinCount-$MaxCount" } else { "≥$MinCount" }
        Write-Host "  ✓ $Name - $actualCount $ExpectedChangeType changes (expected $rangeMsg)" -ForegroundColor Green
    }
}

function Get-ActivityChangeCount {
    <#
    .SYNOPSIS
        Get the count of a specific change type from an activity

    .DESCRIPTION
        Returns the count of changes for a specific ObjectChangeType.
        Useful when you need to check counts without asserting.

    .PARAMETER ActivityId
        The Activity ID (GUID) to query

    .PARAMETER ChangeType
        The ObjectChangeType to count

    .EXAMPLE
        $deletedCount = Get-ActivityChangeCount -ActivityId $importResult.activityId -ChangeType "Deleted"

    .OUTPUTS
        [int] The count of changes of the specified type
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ActivityId,

        [Parameter(Mandatory=$true)]
        [ValidateSet('Added', 'Updated', 'Deleted', 'Projected', 'Joined', 'AttributeFlow', 'Disconnected',
                     'DisconnectedOutOfScope', 'OutOfScopeRetainJoin', 'DriftCorrection',
                     'Exported', 'Deprovisioned', 'NoChange', 'PendingExport', 'PendingExportConfirmed')]
        [string]$ChangeType
    )

    $stats = Get-JIMActivityStats -ActivityId $ActivityId

    if (-not $stats) {
        return 0
    }

    switch ($ChangeType) {
        'Added' { return $stats.totalCsoAdds }
        'Updated' { return $stats.totalCsoUpdates }
        'Deleted' { return $stats.totalCsoDeletes }
        'Projected' { return $stats.totalProjections }
        'Joined' { return $stats.totalJoins }
        'AttributeFlow' { return $stats.totalAttributeFlows }
        'Disconnected' { return $stats.totalDisconnections }
        'DisconnectedOutOfScope' { return $stats.totalDisconnectedOutOfScope }
        'OutOfScopeRetainJoin' { return $stats.totalOutOfScopeRetainJoin }
        'DriftCorrection' { return $stats.totalDriftCorrections }
        'Exported' { return $stats.totalExported }
        'Deprovisioned' { return $stats.totalDeprovisioned }
        'NoChange' { return $stats.totalUnchanged }
        'PendingExport' { return $stats.totalPendingExports }
        'PendingExportConfirmed' { return $stats.totalPendingExportsConfirmed }
        default { return 0 }
    }
}

function Assert-NoUnresolvedReferences {
    <#
    .SYNOPSIS
        Assert that a Connected System has no unresolved reference attribute values.

    .DESCRIPTION
        Fails fast if any reference attribute values in the Connected System are unresolved.
        This catches container scope issues and cross-run reference resolution failures early.

    .PARAMETER ConnectedSystemId
        The Connected System ID to check.

    .PARAMETER Name
        A friendly name for the check (used in error messages).

    .PARAMETER Context
        Optional context string (e.g. "after Source Full Import").

    .EXAMPLE
        Assert-NoUnresolvedReferences -ConnectedSystemId $sourceSystem.id -Name "Source AD" -Context "after Full Import"
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory=$true)]
        [string]$Name,

        [string]$Context = ""
    )

    $contextMsg = if ($Context) { " $Context" } else { "" }

    # Retry on transient failures — the database may still be processing a large import
    $maxRetries = 3
    $unresolvedCount = $null
    for ($attempt = 1; $attempt -le $maxRetries; $attempt++) {
        try {
            $unresolvedCount = Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId $ConnectedSystemId
            break
        }
        catch {
            if ($attempt -eq $maxRetries) {
                throw "Failed to check unresolved references for $Name$contextMsg after $maxRetries attempts: $_"
            }
            Write-Host "    Transient failure checking unresolved references (attempt $attempt/$maxRetries), retrying..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }

    if ($unresolvedCount -gt 0) {
        throw "$Name has $unresolvedCount unresolved reference attribute value(s)$contextMsg. " +
              "Reference values (e.g. group member DNs) could not be matched to CSOs. " +
              "Check container scope configuration - all referenced objects must be in scope."
    }
    Write-Host "  ✓ $Name - no unresolved references$contextMsg" -ForegroundColor Green
}

function Assert-ScheduleExecutionSuccess {
    <#
    .SYNOPSIS
        Assert that a Schedule Execution completed successfully and all step activities succeeded.

    .DESCRIPTION
        Validates that a JIM Schedule Execution completed without errors by checking:
        1. The overall execution status is 'Completed'
        2. Every step's activity status is acceptable (Complete, or CompleteWithWarning if allowed)

        Uses the execution detail endpoint which returns step-level activity status information.
        This prevents integration tests from silently passing when a schedule execution
        reports 'Completed' but individual step activities had warnings or errors.

    .PARAMETER ExecutionId
        The Schedule Execution ID (GUID) to validate.

    .PARAMETER Name
        A friendly name for the execution (used in error messages).

    .PARAMETER AllowWarnings
        If specified, allows 'CompleteWithWarning' activity status to pass.
        Use sparingly - warnings often indicate real issues.

    .EXAMPLE
        Assert-ScheduleExecutionSuccess -ExecutionId $execution.id -Name "Multi-Step Schedule"

    .EXAMPLE
        Assert-ScheduleExecutionSuccess -ExecutionId $execution.id -Name "Parallel Schedule" -AllowWarnings
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ExecutionId,

        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$false)]
        [switch]$AllowWarnings
    )

    # Fetch the execution details (includes step info with activityId and activityStatus)
    $execution = Get-JIMScheduleExecution -Id $ExecutionId

    if (-not $execution) {
        throw "Schedule execution not found: $ExecutionId for '$Name'"
    }

    # Check overall execution status
    $status = $execution.status
    $executionFailed = ($status -ne "Completed" -and $status -ne 2)

    if ($executionFailed) {
        Write-Host "  ✗ $Name FAILED (execution status: $status)" -ForegroundColor Red
        if ($execution.errorMessage) {
            Write-Host "    Error: $($execution.errorMessage)" -ForegroundColor Red
        }
    }

    # Validate individual step activity statuses from the execution detail
    $steps = $execution.steps

    if (-not $steps -or $steps.Count -eq 0) {
        if ($executionFailed) {
            $errorMsg = "Schedule execution '$Name' ended with status: $status"
            if ($execution.errorMessage) {
                $errorMsg += " - Error: $($execution.errorMessage)"
            }
            throw $errorMsg
        }
        # No step detail available - fall back to execution status check only
        Write-Host "  ✓ $Name completed successfully (Status: Completed)" -ForegroundColor Green
        return
    }

    # Define acceptable activity statuses
    $acceptableStatuses = @('Complete')
    if ($AllowWarnings) {
        $acceptableStatuses += 'CompleteWithWarning'
    }

    $failedSteps = @()
    $validatedCount = 0

    foreach ($step in $steps) {
        $activityStatus = $step.activityStatus

        # Skip steps without activity status (not yet run, or no activity linked)
        if (-not $activityStatus) {
            continue
        }

        $validatedCount++

        if ($activityStatus -notin $acceptableStatuses) {
            $failedSteps += $step
        }
    }

    if ($failedSteps.Count -gt 0) {
        Write-Host "  ✗ $Name has $($failedSteps.Count) failed step activity/activities:" -ForegroundColor Red
        foreach ($failed in $failedSteps) {
            $stepName = $failed.name ?? "Step $($failed.stepIndex)"
            Write-Host "    - $stepName : ActivityStatus=$($failed.activityStatus)" -ForegroundColor Red
            if ($failed.errorMessage) {
                Write-Host "      Error: $($failed.errorMessage)" -ForegroundColor Red
            }

            # If the step has an activityId, use Assert-ActivitySuccess for detailed diagnostics
            if ($failed.activityId) {
                try {
                    Assert-ActivitySuccess -ActivityId $failed.activityId -Name $stepName
                }
                catch {
                    # Assert-ActivitySuccess already printed diagnostics, continue to next step
                }
            }
        }
        throw "Schedule execution '$Name' failed: $($failedSteps.Count) step activity/activities had non-success status (ExecutionId: $ExecutionId)"
    }

    if ($executionFailed) {
        # Execution failed but no step-level failures found (e.g. infrastructure error)
        $errorMsg = "Schedule execution '$Name' ended with status: $status"
        if ($execution.errorMessage) {
            $errorMsg += " - Error: $($execution.errorMessage)"
        }
        throw $errorMsg
    }

    Write-Host "  ✓ $Name completed successfully (Status: Completed, $validatedCount step activities OK)" -ForegroundColor Green
}

function Assert-ParallelExecutionTiming {
    <#
    .SYNOPSIS
        Assert that parallel step groups in a schedule execution actually ran concurrently.

    .DESCRIPTION
        Validates that steps sharing the same StepIndex (parallel groups) have overlapping
        execution time ranges, proving they ran concurrently rather than sequentially.

        For each parallel group (2+ steps at the same StepIndex):
        1. All steps must have StartedAt and CompletedAt timestamps
        2. At least one pair of steps must have overlapping time ranges
           (step B started before step A completed)

        This catches the case where parallel steps are incorrectly dispatched sequentially,
        which would otherwise go undetected since the schedule still completes successfully.

    .PARAMETER ExecutionId
        The Schedule Execution ID (GUID) to validate.

    .PARAMETER Name
        A friendly name for the execution (used in output messages).

    .EXAMPLE
        Assert-ParallelExecutionTiming -ExecutionId $execution.id -Name "Complex Parallel Execution"
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ExecutionId,

        [Parameter(Mandatory=$true)]
        [string]$Name
    )

    $execution = Get-JIMScheduleExecution -Id $ExecutionId

    if (-not $execution) {
        throw "Schedule execution not found: $ExecutionId for '$Name'"
    }

    $steps = $execution.steps
    if (-not $steps -or $steps.Count -eq 0) {
        throw "No steps found in execution '$Name' (ExecutionId: $ExecutionId)"
    }

    # Group steps by stepIndex to find parallel groups
    $stepGroups = @{}
    foreach ($step in $steps) {
        $idx = $step.stepIndex
        if (-not $stepGroups.ContainsKey($idx)) {
            $stepGroups[$idx] = @()
        }
        $stepGroups[$idx] += $step
    }

    $parallelGroupCount = 0
    $validatedGroupCount = 0

    foreach ($idx in ($stepGroups.Keys | Sort-Object)) {
        $group = $stepGroups[$idx]

        # Only validate groups with 2+ steps (parallel groups)
        if ($group.Count -lt 2) {
            continue
        }

        $parallelGroupCount++
        Write-Host "  Validating parallel timing for step index $idx ($($group.Count) steps)..." -ForegroundColor DarkGray

        # Check all steps have timing data
        $stepsWithTiming = @()
        foreach ($s in $group) {
            $stepName = $s.name ?? "Step $idx"
            if (-not $s.startedAt -or -not $s.completedAt) {
                Write-Host "    WARNING: Step '$stepName' (CS=$($s.connectedSystemId)) missing timing data (startedAt=$($s.startedAt), completedAt=$($s.completedAt))" -ForegroundColor Yellow
                continue
            }
            $stepsWithTiming += @{
                Name = $stepName
                ConnectedSystemId = $s.connectedSystemId
                StartedAt = [DateTime]::Parse($s.startedAt)
                CompletedAt = [DateTime]::Parse($s.completedAt)
            }
        }

        if ($stepsWithTiming.Count -lt 2) {
            Write-Host "    WARNING: Fewer than 2 steps with timing data at index $idx, skipping overlap check" -ForegroundColor Yellow
            continue
        }

        # Log timing details
        foreach ($s in $stepsWithTiming) {
            $duration = ($s.CompletedAt - $s.StartedAt).TotalSeconds
            Write-Host "    $($s.Name) (CS=$($s.ConnectedSystemId)): $($s.StartedAt.ToString('HH:mm:ss.fff')) -> $($s.CompletedAt.ToString('HH:mm:ss.fff')) ($([math]::Round($duration, 1))s)" -ForegroundColor DarkGray
        }

        # Check for any overlapping pair — step B started before step A completed
        $hasOverlap = $false
        for ($i = 0; $i -lt $stepsWithTiming.Count; $i++) {
            for ($j = $i + 1; $j -lt $stepsWithTiming.Count; $j++) {
                $a = $stepsWithTiming[$i]
                $b = $stepsWithTiming[$j]

                # Two ranges overlap if A starts before or when B ends AND B starts before or when A ends.
                # Using -le (<=) handles zero-duration steps that start at the same instant.
                if ($a.StartedAt -le $b.CompletedAt -and $b.StartedAt -le $a.CompletedAt) {
                    $hasOverlap = $true
                    Write-Host "    Overlap confirmed: '$($a.Name)' and '$($b.Name)' ran concurrently" -ForegroundColor DarkGray
                    break
                }
            }
            if ($hasOverlap) { break }
        }

        if (-not $hasOverlap) {
            throw "Parallel step group at index $idx did NOT execute concurrently. Steps ran sequentially despite being in a parallel group. This indicates a bug in the task dispatch pipeline. (ExecutionId: $ExecutionId)"
        }

        $validatedGroupCount++
    }

    if ($parallelGroupCount -eq 0) {
        Write-Host "  No parallel step groups found in execution '$Name' - nothing to validate" -ForegroundColor Yellow
        return
    }

    Write-Host "  ✓ $Name parallel execution validated ($validatedGroupCount/$parallelGroupCount parallel groups confirmed concurrent)" -ForegroundColor Green
}

function Assert-ActivityOutcomeStats {
    <#
    .SYNOPSIS
        Assert that an activity's stats contain expected outcome-based counts.

    .DESCRIPTION
        Retrieves execution statistics for a Run Profile activity and validates
        that specific stat properties match expected values. Used to verify that
        the RPEI outcome graph is correctly recording and surfacing outcomes
        through the stats API endpoint.

    .PARAMETER ActivityId
        The Activity ID (GUID) to validate.

    .PARAMETER Name
        A friendly name for the activity (used in output messages).

    .PARAMETER ExpectedStats
        A hashtable of stat property names and their expected values.
        Only the specified properties are validated; others are ignored.
        Property names match the JSON response (e.g., totalProjections, totalCsoAdds).

    .EXAMPLE
        Assert-ActivityOutcomeStats -ActivityId $syncResult.activityId -Name "Full Sync (Joiner)" -ExpectedStats @{
            totalProjections = 10
            totalAttributeFlows = 10
            totalPendingExports = 10
        }
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ActivityId,

        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$true)]
        [hashtable]$ExpectedStats
    )

    $stats = Get-JIMActivityStats -ActivityId $ActivityId

    if (-not $stats) {
        throw "Could not retrieve statistics for Activity '$Name' (ID: $ActivityId)"
    }

    $failures = @()
    foreach ($key in $ExpectedStats.Keys) {
        $expectedValue = $ExpectedStats[$key]
        $actualValue = $stats.$key

        if ($null -eq $actualValue) {
            $failures += "  Property '$key' not found in stats response"
            continue
        }

        if ($actualValue -ne $expectedValue) {
            $failures += "  $key`: expected $expectedValue, got $actualValue"
        }
    }

    if ($failures.Count -gt 0) {
        $failureDetails = $failures -join "`n"
        Write-Host "  ✗ $Name outcome stats validation failed:" -ForegroundColor Red
        foreach ($f in $failures) {
            Write-Host "    $f" -ForegroundColor Red
        }
        throw "Activity '$Name' outcome stats do not match expected values (ActivityId: $ActivityId):`n$failureDetails"
    }

    $checkedProps = ($ExpectedStats.Keys | Sort-Object) -join ", "
    Write-Host "  ✓ $Name outcome stats validated ($checkedProps)" -ForegroundColor Green
}

function Assert-ActivityItemsHaveOutcomeSummary {
    <#
    .SYNOPSIS
        Assert that execution items for an activity have OutcomeSummary values.

    .DESCRIPTION
        Retrieves execution items for a Run Profile activity and validates that
        at least some items have non-null OutcomeSummary fields. Optionally checks
        that items contain a specific outcome type in their summary string.

        This verifies that the RPEI outcome graph is populating the denormalised
        OutcomeSummary field used for stat chip rendering in the UI.

    .PARAMETER ActivityId
        The Activity ID (GUID) to validate.

    .PARAMETER Name
        A friendly name for the activity (used in output messages).

    .PARAMETER ExpectedOutcomeType
        Optional. If specified, validates that at least one item's OutcomeSummary
        contains this outcome type (e.g., "Projected", "CsoAdded", "Exported").

    .PARAMETER MinItemsWithSummary
        Minimum number of items that must have a non-null OutcomeSummary.
        Defaults to 1.

    .EXAMPLE
        Assert-ActivityItemsHaveOutcomeSummary -ActivityId $syncResult.activityId -Name "Full Sync" -ExpectedOutcomeType "Projected"

    .EXAMPLE
        Assert-ActivityItemsHaveOutcomeSummary -ActivityId $importResult.activityId -Name "CSV Import" -ExpectedOutcomeType "CsoAdded" -MinItemsWithSummary 5
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ActivityId,

        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$false)]
        [string]$ExpectedOutcomeType,

        [Parameter(Mandatory=$false)]
        [int]$MinItemsWithSummary = 1
    )

    # Get first page of execution items
    $items = @(Get-JIMActivity -Id $ActivityId -ExecutionItems | Select-Object -First 100)

    if ($items.Count -eq 0) {
        throw "No execution items found for Activity '$Name' (ID: $ActivityId)"
    }

    # Check how many items have OutcomeSummary
    $itemsWithSummary = @($items | Where-Object { $_.outcomeSummary })
    $summaryCount = $itemsWithSummary.Count

    if ($summaryCount -lt $MinItemsWithSummary) {
        Write-Host "  ✗ $Name - Expected at least $MinItemsWithSummary items with OutcomeSummary, but found $summaryCount" -ForegroundColor Red
        throw "Activity '$Name' has only $summaryCount items with OutcomeSummary (expected at least $MinItemsWithSummary) (ActivityId: $ActivityId)"
    }

    # Check for specific outcome type if requested
    if ($ExpectedOutcomeType) {
        $matchingItems = @($itemsWithSummary | Where-Object { $_.outcomeSummary -match "$ExpectedOutcomeType`:" })
        if ($matchingItems.Count -eq 0) {
            Write-Host "  ✗ $Name - No items have OutcomeSummary containing '$ExpectedOutcomeType'" -ForegroundColor Red
            $sampleSummaries = ($itemsWithSummary | Select-Object -First 3 | ForEach-Object { $_.outcomeSummary }) -join "; "
            throw "Activity '$Name' has no items with OutcomeSummary containing '$ExpectedOutcomeType'. Sample summaries: $sampleSummaries (ActivityId: $ActivityId)"
        }
        Write-Host "  ✓ $Name has $summaryCount items with OutcomeSummary ($($matchingItems.Count) contain '$ExpectedOutcomeType')" -ForegroundColor Green
    }
    else {
        Write-Host "  ✓ $Name has $summaryCount items with OutcomeSummary" -ForegroundColor Green
    }
}

function Assert-MvoAttributeValue {
    <#
    .SYNOPSIS
        Assert a Metaverse Object's attribute value(s), and optionally the Synchronisation Rule
        that won Attribute Priority resolution (#91).

    .DESCRIPTION
        Reads directly from the "MetaverseObjectAttributeValues" table via psql rather than the
        REST API. MetaverseObjectAttributeValueDto (JIM.Web/Models/Api/MetaverseObjectDto.cs)
        exposes ContributedBySystemId/ContributedBySystemName but NOT ContributedBySyncRuleId or
        NullValue, both of which Attribute Priority assertions need: NullValue distinguishes an
        asserted-null contribution from no contributor at all, and ContributedBySyncRuleId is the
        only way to name the exact winning Synchronisation Rule when a Connected System could
        (in later scenarios) run more than one import rule into the same attribute. This mirrors
        the direct-psql pattern established by Assert-ExportRpeisHaveCsoLink for the same reason
        (issue #683): the data needed to assert against is not on the wire yet.

        Exactly one of -ExpectedValue, -ExpectedValues, -ExpectedReferenceMvoId or -ExpectNoValue
        must be supplied; each targets a different attribute shape (scalar, multi-valued set,
        reference, or absence/asserted-null).

    .PARAMETER MvoId
        The Metaverse Object ID (GUID) to inspect.

    .PARAMETER AttributeName
        The Metaverse Attribute name (e.g. "Job Title", "Manager").

    .PARAMETER ExpectedValue
        Expected scalar value (string-compared) for a single-valued, non-reference attribute.

    .PARAMETER ExpectedValues
        Expected full value set (order-independent) for a multi-valued attribute. Asserts both
        that every expected value is present and that no unexpected extra value is present, so a
        losing contributor's values being still-present (winner-takes-all-values violated) fails
        the assertion just as surely as a missing expected value.

    .PARAMETER ExpectedReferenceMvoId
        Expected target Metaverse Object ID for a Reference-typed attribute.

    .PARAMETER ExpectNoValue
        Asserts the attribute carries no usable value for this Metaverse Object: neither a real
        value row nor an asserted-null marker row (NullValue = true) is required to be absent by
        this switch alone, both collapse to "nothing to show the caller". A future helper can
        split AssertedNull from NoContributor if a test needs that distinction specifically.

    .PARAMETER ExpectedContributingSyncRuleName
        When supplied, asserts the (non-null-marker) row's ContributedBySyncRuleId resolves to a
        Synchronisation Rule with this exact name.

    .PARAMETER Name
        A friendly name for the assertion, used in diagnostics. Defaults to "<AttributeName> on <MvoId>".

    .EXAMPLE
        Assert-MvoAttributeValue -MvoId $aliceMvoId -AttributeName "Job Title" `
            -ExpectedValue "Engineer (Primary)" -ExpectedContributingSyncRuleName "Scenario 14 Primary Import Users"

    .EXAMPLE
        Assert-MvoAttributeValue -MvoId $aliceMvoId -AttributeName "Manager" -ExpectedReferenceMvoId $bobMvoId

    .EXAMPLE
        Assert-MvoAttributeValue -MvoId $aliceMvoId -AttributeName "Other Telephones" `
            -ExpectedValues @("+44 20 7946 1000", "+44 20 7946 1001")
    #>
    param(
        [Parameter(Mandatory=$true)]
        [Guid]$MvoId,

        [Parameter(Mandatory=$true)]
        [string]$AttributeName,

        [Parameter(Mandatory=$false)]
        [string]$ExpectedValue,

        [Parameter(Mandatory=$false)]
        [string[]]$ExpectedValues,

        [Parameter(Mandatory=$false)]
        [Guid]$ExpectedReferenceMvoId,

        [Parameter(Mandatory=$false)]
        [switch]$ExpectNoValue,

        [Parameter(Mandatory=$false)]
        [string]$ExpectedContributingSyncRuleName,

        [Parameter(Mandatory=$false)]
        [string]$Name
    )

    $displayName = if ($Name) { $Name } else { "$AttributeName on $MvoId" }

    $modeCount = 0
    if ($PSBoundParameters.ContainsKey('ExpectedValue')) { $modeCount++ }
    if ($PSBoundParameters.ContainsKey('ExpectedValues')) { $modeCount++ }
    if ($PSBoundParameters.ContainsKey('ExpectedReferenceMvoId')) { $modeCount++ }
    if ($ExpectNoValue) { $modeCount++ }
    if ($modeCount -ne 1) {
        throw "Assert-MvoAttributeValue ($displayName): exactly one of -ExpectedValue, -ExpectedValues, -ExpectedReferenceMvoId, -ExpectNoValue must be supplied."
    }

    $safeMvoId = $MvoId.ToString()
    $safeAttributeName = $AttributeName.Replace("'", "''")

    # Boolean columns are rendered via CASE rather than a bare ::text cast: PostgreSQL's cast
    # semantics for boolean->text are easy to get subtly wrong across versions, so this pins the
    # exact 'true'/'false'/'' text this function parses back out below.
    $query = @"
SELECT
    COALESCE(v."StringValue", '') AS string_value,
    COALESCE(v."IntValue"::text, '') AS int_value,
    COALESCE(v."LongValue"::text, '') AS long_value,
    COALESCE(v."DateTimeValue"::text, '') AS datetime_value,
    CASE WHEN v."BoolValue" IS NULL THEN '' WHEN v."BoolValue" THEN 'true' ELSE 'false' END AS bool_value,
    COALESCE(v."GuidValue"::text, '') AS guid_value,
    COALESCE(v."ReferenceValueId"::text, '') AS reference_value_id,
    CASE WHEN v."NullValue" THEN 't' ELSE 'f' END AS null_value,
    COALESCE(v."ContributedBySyncRuleId"::text, '') AS sync_rule_id,
    COALESCE(sr."Name", '') AS sync_rule_name,
    COALESCE(v."ContributedBySystemId"::text, '') AS system_id,
    COALESCE(cs."Name", '') AS system_name
FROM "MetaverseObjectAttributeValues" v
JOIN "MetaverseAttributes" a ON a."Id" = v."AttributeId"
LEFT JOIN "SyncRules" sr ON sr."Id" = v."ContributedBySyncRuleId"
LEFT JOIN "ConnectedSystems" cs ON cs."Id" = v."ContributedBySystemId"
WHERE v."MetaverseObjectId" = '$safeMvoId'
  AND a."Name" = '$safeAttributeName'
ORDER BY string_value, reference_value_id;
"@

    $rawRows = docker compose exec -T jim.database psql -t -A -F '|' -U jim -d jim -c $query 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Assert-MvoAttributeValue ($displayName): psql query failed. Output: $rawRows"
    }

    $rows = @($rawRows | Where-Object { $_ -and $_.Trim() -ne '' } | ForEach-Object {
        $fields = $_.Split('|')
        [PSCustomObject]@{
            StringValue      = $fields[0]
            IntValue         = $fields[1]
            LongValue        = $fields[2]
            DateTimeValue    = $fields[3]
            BoolValue        = $fields[4]
            GuidValue        = $fields[5]
            ReferenceValueId = $fields[6]
            NullValue        = ($fields[7] -eq 't')
            SyncRuleId       = $fields[8]
            SyncRuleName     = $fields[9]
            SystemId         = $fields[10]
            SystemName       = $fields[11]
        }
    })

    # Nested so the two call sites below (ExpectNoValue and every value-shape branch) share one
    # implementation without exporting a second public function for what is an internal step.
    function Test-MvoContributingSyncRuleName {
        param($Rows, [string]$ExpectedRuleName)

        $contributingNames = @($Rows | Where-Object { -not $_.NullValue -and $_.SyncRuleName } | Select-Object -ExpandProperty SyncRuleName -Unique)
        if ($contributingNames.Count -eq 0) {
            Write-Host "  ✗ $displayName - expected contributing Synchronisation Rule '$ExpectedRuleName' but no row carries ContributedBySyncRuleId" -ForegroundColor Red
            throw "Assertion failed: $displayName expected contributing Synchronisation Rule '$ExpectedRuleName' but no row carries ContributedBySyncRuleId."
        }
        if ($contributingNames.Count -gt 1 -or $contributingNames[0] -ne $ExpectedRuleName) {
            Write-Host "  ✗ $displayName - expected contributing Synchronisation Rule '$ExpectedRuleName', found: $($contributingNames -join ', ')" -ForegroundColor Red
            throw "Assertion failed: $displayName expected contributing Synchronisation Rule '$ExpectedRuleName', found '$($contributingNames -join ', ')'"
        }
        Write-Host "  ✓ $displayName - contributed by Synchronisation Rule '$ExpectedRuleName'" -ForegroundColor Green
    }

    if ($ExpectNoValue) {
        $realRows = @($rows | Where-Object { -not $_.NullValue })
        if ($realRows.Count -gt 0) {
            Write-Host "  ✗ $displayName - expected no value, found $($realRows.Count) row(s)" -ForegroundColor Red
            foreach ($r in $realRows) {
                Write-Host "    StringValue='$($r.StringValue)' ReferenceValueId='$($r.ReferenceValueId)' ContributedBy='$($r.SyncRuleName)'" -ForegroundColor Red
            }
            throw "Assertion failed: $displayName expected no value but found $($realRows.Count) row(s)."
        }
        Write-Host "  ✓ $displayName - no value present (as expected)" -ForegroundColor Green

        if ($ExpectedContributingSyncRuleName) {
            Test-MvoContributingSyncRuleName -Rows $rows -ExpectedRuleName $ExpectedContributingSyncRuleName
        }
        return
    }

    if ($rows.Count -eq 0) {
        throw "Assertion failed: $displayName has no attribute value row(s) in the Metaverse (expected a contributor)."
    }

    if ($PSBoundParameters.ContainsKey('ExpectedReferenceMvoId')) {
        $expected = $ExpectedReferenceMvoId.ToString()
        $actual = @($rows | Where-Object { -not $_.NullValue } | Select-Object -ExpandProperty ReferenceValueId)
        if ($actual.Count -ne 1 -or $actual[0] -ne $expected) {
            Write-Host "  ✗ $displayName - expected reference to $expected, found: $($actual -join ', ')" -ForegroundColor Red
            throw "Assertion failed: $displayName expected reference MVO '$expected', found '$($actual -join ', ')'"
        }
        Write-Host "  ✓ $displayName - reference resolves to expected MVO ($expected)" -ForegroundColor Green
    }
    elseif ($PSBoundParameters.ContainsKey('ExpectedValue')) {
        $realRows = @($rows | Where-Object { -not $_.NullValue })
        if ($realRows.Count -ne 1) {
            throw "Assertion failed: $displayName expected exactly one value row for a single-valued attribute, found $($realRows.Count)."
        }
        $r = $realRows[0]
        $actual =
            if ($r.StringValue) { $r.StringValue }
            elseif ($r.IntValue) { $r.IntValue }
            elseif ($r.LongValue) { $r.LongValue }
            elseif ($r.DateTimeValue) { $r.DateTimeValue }
            elseif ($r.BoolValue) { $r.BoolValue }
            elseif ($r.GuidValue) { $r.GuidValue }
            else { '' }
        if ($actual -ne $ExpectedValue) {
            Write-Host "  ✗ $displayName - expected '$ExpectedValue', got '$actual'" -ForegroundColor Red
            throw "Assertion failed: $displayName expected '$ExpectedValue', got '$actual'"
        }
        Write-Host "  ✓ $displayName - value is '$actual' (as expected)" -ForegroundColor Green
    }
    elseif ($PSBoundParameters.ContainsKey('ExpectedValues')) {
        $realRows = @($rows | Where-Object { -not $_.NullValue })
        $actualValues = @($realRows | ForEach-Object {
            if ($_.StringValue) { $_.StringValue }
            elseif ($_.ReferenceValueId) { $_.ReferenceValueId }
            elseif ($_.IntValue) { $_.IntValue }
            elseif ($_.LongValue) { $_.LongValue }
            elseif ($_.DateTimeValue) { $_.DateTimeValue }
            elseif ($_.GuidValue) { $_.GuidValue }
            else { $null }
        }) | Where-Object { $null -ne $_ }

        $expectedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$ExpectedValues, [System.StringComparer]::Ordinal)
        $actualSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$actualValues, [System.StringComparer]::Ordinal)

        $missing = @($ExpectedValues | Where-Object { -not $actualSet.Contains($_) })
        $unexpected = @($actualValues | Where-Object { -not $expectedSet.Contains($_) })

        if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
            Write-Host "  ✗ $displayName - value set mismatch" -ForegroundColor Red
            if ($missing.Count -gt 0) { Write-Host "    Missing: $($missing -join ', ')" -ForegroundColor Red }
            if ($unexpected.Count -gt 0) { Write-Host "    Unexpected (should have lost priority resolution): $($unexpected -join ', ')" -ForegroundColor Red }
            throw "Assertion failed: $displayName value set mismatch. Missing: $($missing -join ', '); Unexpected: $($unexpected -join ', ')"
        }
        Write-Host "  ✓ $displayName - value set matches expected ($($actualValues.Count) value(s))" -ForegroundColor Green
    }

    if ($ExpectedContributingSyncRuleName) {
        Test-MvoContributingSyncRuleName -Rows $rows -ExpectedRuleName $ExpectedContributingSyncRuleName
    }
}

function Get-JimErrorLinePattern {
    <#
    .SYNOPSIS
        Returns the regex used to detect Error and Fatal level log lines.

    .DESCRIPTION
        Shared by Start-JimErrorWatcher and Assert-NoWorkerErrors so both layers
        of error detection agree on what constitutes an error line. Covers every
        format the JIM services emit:

          - Serilog console text template, which renders the level inside the
            same bracket pair as the timestamp: '[16:33:02 ERR]', '[16:33:02 FTL]'.
            Also matches a bare '[ERR]'/'[FTL]' should the template ever change.
          - CLEF/compact JSON (RenderedCompactJsonFormatter): '"@l":"Error"',
            '"@l":"Fatal"'. CLEF omits @l entirely for Information-level events,
            so anchoring on the @l property cannot false-positive on messages
            that merely contain the word 'Error'.

    .OUTPUTS
        [string] Regex pattern.
    #>
    return '\[(?:[^\]]*\s)?(?:ERR|FTL)\]|"@l"\s*:\s*"(?:Error|Fatal)"'
}

function Start-JimErrorWatcher {
    <#
    .SYNOPSIS
        Starts background jobs that tail JIM container logs for Error/Fatal lines.

    .DESCRIPTION
        Spawns one background job per target container (jim.web, jim.worker,
        jim.scheduler) that runs `docker logs --since <time> -f <container>` and
        appends any Error or Fatal level line (see Get-JimErrorLinePattern) to a
        shared sentinel file.

        Returns a handle object containing the jobs, the sentinel file path, the
        start time, and an optional regex of allowed patterns to ignore.

        The sentinel file is plain text; each line is prefixed with the container
        name and a UTC timestamp so callers can tell where the error came from.

        This is the live half of the two-layer error detection strategy; the
        post-scenario scan via Assert-NoWorkerErrors is the belt-and-braces layer.

    .PARAMETER SentinelPath
        Path to the sentinel file where detected error lines are written.
        The file is created empty at start time.

    .PARAMETER Since
        DateTime marking the start of the log window. Passed to `docker logs --since`.
        Use the scenario start time so we don't pick up lines from earlier phases.

    .PARAMETER AllowPattern
        Optional regex. Lines matching this pattern are NOT written to the sentinel
        file, even if they match the error pattern. Use sparingly and only for
        genuinely benign patterns confirmed to not indicate a real failure.

    .PARAMETER Containers
        Container names to tail. Defaults to jim.web, jim.worker, jim.scheduler.

    .OUTPUTS
        PSCustomObject with Jobs, SentinelPath, StartTime, AllowPattern.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$SentinelPath,

        [Parameter(Mandatory=$true)]
        [datetime]$Since,

        [Parameter(Mandatory=$false)]
        [string]$AllowPattern = '',

        [Parameter(Mandatory=$false)]
        [string[]]$Containers = @('jim.web', 'jim.worker', 'jim.scheduler')
    )

    # Ensure sentinel file exists and is empty
    $sentinelDir = Split-Path -Parent $SentinelPath
    if ($sentinelDir -and -not (Test-Path $sentinelDir)) {
        New-Item -ItemType Directory -Path $sentinelDir -Force | Out-Null
    }
    Set-Content -Path $SentinelPath -Value '' -NoNewline -Encoding UTF8

    # `docker logs --since` expects RFC3339 or a Go duration. Use RFC3339 with
    # a 1-second cushion so we don't miss any line racing the watcher start.
    $sinceString = $Since.AddSeconds(-1).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

    $jobs = @()
    foreach ($container in $Containers) {
        $job = Start-Job -Name "jim-err-watcher-$container" -ScriptBlock {
            param($containerName, $since, $sentinel, $allowPattern, $errorPattern)

            # `docker logs -f ... 2>&1` merges stderr into stdout so the pipeline
            # sees every log line regardless of which stream the sink uses. The
            # pipeline is line-buffered, so each match is written to the sentinel
            # the moment it arrives.
            & docker logs --since $since -f $containerName 2>&1 | ForEach-Object {
                $line = $_
                if ($null -eq $line) { return }
                if ($line -match $errorPattern) {
                    if ($allowPattern -and ($line -match $allowPattern)) { return }
                    $stamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                    # Open/append/close per line so the sentinel is durable even
                    # if the watcher is killed mid-stream.
                    [System.IO.File]::AppendAllText($sentinel, "[$stamp] [$containerName] $line`n")
                }
            }
        } -ArgumentList $container, $sinceString, $SentinelPath, $AllowPattern, (Get-JimErrorLinePattern)

        $jobs += $job
    }

    return [PSCustomObject]@{
        Jobs         = $jobs
        SentinelPath = $SentinelPath
        StartTime    = $Since
        AllowPattern = $AllowPattern
        Containers   = $Containers
    }
}

function Test-JimErrorWatcher {
    <#
    .SYNOPSIS
        Returns $true if the watcher sentinel file contains any entries.

    .DESCRIPTION
        Lightweight check callable from polling loops. Does not stop the watcher
        or drain jobs; safe to call as often as needed.

    .PARAMETER Handle
        The handle returned by Start-JimErrorWatcher.

    .OUTPUTS
        [bool] True if one or more error lines have been captured.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [PSCustomObject]$Handle
    )

    if (-not (Test-Path $Handle.SentinelPath)) {
        return $false
    }

    $info = Get-Item $Handle.SentinelPath
    return ($info.Length -gt 0)
}

function Stop-JimErrorWatcher {
    <#
    .SYNOPSIS
        Stops the watcher jobs and returns captured error lines.

    .DESCRIPTION
        Stops and removes the background docker-logs jobs, then reads the
        sentinel file and returns its lines. Call this from a `finally` block
        so the jobs are always cleaned up, even when the scenario throws.

    .PARAMETER Handle
        The handle returned by Start-JimErrorWatcher.

    .OUTPUTS
        [string[]] Array of captured error lines (empty if no errors were seen).
    #>
    param(
        [Parameter(Mandatory=$true)]
        [PSCustomObject]$Handle
    )

    foreach ($job in $Handle.Jobs) {
        try {
            Stop-Job -Job $job -ErrorAction SilentlyContinue
            Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        }
        catch {
            # Best-effort cleanup; swallow so we always attempt the next job.
        }
    }

    if (-not (Test-Path $Handle.SentinelPath)) {
        return @()
    }

    $lines = @(Get-Content -Path $Handle.SentinelPath -ErrorAction SilentlyContinue | Where-Object { $_ -ne '' })
    return $lines
}

function Clear-StaleIntegrationMonitors {
    <#
    .SYNOPSIS
        Reap monitor processes and sidecar containers leaked by a previous
        crashed or hard-killed runner (#918).

    .DESCRIPTION
        A healthy run stops its own monitors in the scenario finally block, so
        anything matched here is stale by definition. Concurrent runner
        invocations are impossible on one host (shared container names, ports
        and volumes), which makes a host-wide sweep safe.

        Reaps three monitor types:
        - docker-stats samplers (Capture-DockerStats.ps1): matched by command
          line, covering both the .NET global-tool shim and the dotnet child
          that survives it.
        - volume-audit sidecar containers: matched by the
          jim-integration-monitor label (with a name-prefix fallback for
          containers created before the label existed).
        - docker events capturers: matched by the runner's distinctive
          --format string.

        Also removes leftover *.stop signal files in the results directory.

    .PARAMETER ResultsPath
        The runner's results directory, used to clear leftover stop files.

    .OUTPUTS
        None. Logs one summary line, plus a line per reaped stray.
    #>
    param(
        [string]$ResultsPath
    )

    $reapedProcesses = 0
    $reapedContainers = 0

    $strayProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Id -ne $PID -and $_.CommandLine -and (
            $_.CommandLine -match 'Capture-DockerStats\.ps1' -or
            $_.CommandLine -match 'docker events --format.*Actor\.Attributes\.image'
        )
    })
    foreach ($stray in $strayProcesses) {
        Write-Host "  Reaping stray monitor process (PID $($stray.Id)): $($stray.ProcessName)" -ForegroundColor DarkYellow
        Stop-Process -Id $stray.Id -Force -ErrorAction SilentlyContinue
        $reapedProcesses++
    }

    $strayContainers = @(
        (& docker ps -q --filter 'label=jim-integration-monitor' 2>$null)
        (& docker ps -q --filter 'name=jim-volume-audit-' 2>$null)
    ) | Where-Object { $_ } | Select-Object -Unique
    foreach ($containerId in $strayContainers) {
        Write-Host "  Reaping stray monitor container $containerId" -ForegroundColor DarkYellow
        & docker rm -f $containerId 2>&1 | Out-Null
        $reapedContainers++
    }

    if ($ResultsPath -and (Test-Path $ResultsPath)) {
        Get-ChildItem -Path $ResultsPath -Filter '*.stop' -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    if ($reapedProcesses -gt 0 -or $reapedContainers -gt 0) {
        Write-Host "  Reaped $reapedProcesses stray monitor process(es) and $reapedContainers stray container(s) from a previous run" -ForegroundColor Yellow
    }
}

function Start-ConnectorVolumeAuditor {
    <#
    .SYNOPSIS
        Start an inotifywait sidecar that logs every write/create/delete/rename
        to jim-connector-files-volume for the duration of a scenario.

    .DESCRIPTION
        Launches a detached alpine container that installs inotify-tools and
        monitors /connector-files recursively. Every filesystem event is written
        to a log on the host (via a bind-mounted results directory), giving us
        an authoritative timeline of who wrote what to the volume and when.

        This is the strongest probe for diagnosing out-of-band writers: unlike
        transcript-based breadcrumbs, it captures events regardless of which
        process (even non-JIM ones) performed the write. Paired with the
        .last-seed breadcrumb in Write-FilesToConnectorVolume, the two together
        can identify the caller for any unexpected mutation.

        Overhead is negligible on typical scenario runs: inotify is kernel-side
        and the sidecar only emits events when they occur. A Scale100k50Groups run with
        ~4 CSV writes produces a handful of log lines.

        Returns a handle for the companion Stop-ConnectorVolumeAuditor helper.
        Safe to call when docker isn't available — returns $null in that case.

    .PARAMETER LogPath
        Absolute host path to the audit log file to write. Parent directory is
        created if missing.

    .OUTPUTS
        PSCustomObject with ContainerName and LogPath, or $null if the sidecar
        could not be started (no docker, or docker run failed).
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$LogPath
    )

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return $null
    }

    $logDir = Split-Path -Parent $LogPath
    if ($logDir -and -not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }
    # Pre-create the log file so the bind mount is a file (not a directory)
    # inside the sidecar and so the first `tail -f` from a debugging operator
    # doesn't trip on a missing path.
    if (-not (Test-Path $LogPath)) {
        New-Item -ItemType File -Path $LogPath -Force | Out-Null
    }

    $containerName = "jim-volume-audit-$([Guid]::NewGuid().ToString('N').Substring(0,8))"

    # alpine + inotify-tools is ~10 MB pull on first use, cached thereafter.
    # --init gives us a proper PID 1 so SIGTERM from `docker stop` reaches
    # inotifywait cleanly. We chain `apk add` and `inotifywait` in one shell
    # string and redirect output directly to the bind-mounted audit log —
    # inotifywait line-buffers its output by default, so `tail -f` on the host
    # updates live without needing stdbuf.
    $monitorCmd = "apk add -q inotify-tools >/dev/null 2>&1 && " +
                  "inotifywait -m -r -q " +
                  "--timefmt '%FT%T%z' " +
                  "--format '%T %e %w%f' " +
                  "/watch >> /audit.log"

    $runArgs = @(
        'run', '-d', '--rm', '--init',
        '--name', $containerName,
        # Label lets the runner's startup sweep reap strays from a crashed run (#918):
        # inotifywait -m never exits on its own, and --rm only removes the container
        # after exit, so a hard-killed runner otherwise leaks the sidecar forever.
        '--label', 'jim-integration-monitor=volume-audit',
        '-v', 'jim-connector-files-volume:/watch:ro',
        '-v', "${LogPath}:/audit.log",
        'alpine:3.20',
        'sh', '-c', $monitorCmd
    )

    $startOutput = & docker @runArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  (connector-volume audit sidecar failed to start: $startOutput)" -ForegroundColor DarkYellow
        return $null
    }

    return [PSCustomObject]@{
        ContainerName = $containerName
        LogPath       = $LogPath
    }
}

function Stop-ConnectorVolumeAuditor {
    <#
    .SYNOPSIS
        Stop an inotifywait sidecar started by Start-ConnectorVolumeAuditor.

    .DESCRIPTION
        Best-effort cleanup: tries `docker stop` followed by `docker rm -f`.
        Safe to call with $null (no-op); safe to call multiple times. Always
        call from a `finally` block so the sidecar doesn't leak past the run.

    .PARAMETER Handle
        The handle returned by Start-ConnectorVolumeAuditor, or $null.

    .OUTPUTS
        Returns the number of audit log lines captured (0 if the log file is
        empty or missing).
    #>
    param(
        [Parameter(Mandatory=$false)]
        $Handle
    )

    if ($null -eq $Handle) { return 0 }

    # --time 2 caps the wait for graceful SIGTERM; force-kills if the process
    # doesn't exit. This limits worst-case cleanup cost to ~2s per scenario.
    docker stop --time 2 $Handle.ContainerName 2>&1 | Out-Null
    docker rm -f $Handle.ContainerName 2>&1 | Out-Null

    if (Test-Path $Handle.LogPath) {
        $lineCount = @(Get-Content -Path $Handle.LogPath -ErrorAction SilentlyContinue).Count
        return $lineCount
    }
    return 0
}

function Start-DockerEventsCapture {
    <#
    .SYNOPSIS
        Stream `docker events` to a log file for the duration of a scenario.

    .DESCRIPTION
        Spawns `docker events` as a background process that writes every
        container/image/volume lifecycle event to a host log. Essential for
        retroactive diagnosis of throwaway containers (e.g. our busybox seed
        helper, rogue `docker run` calls from other sessions) — `docker events`
        is a live stream with no retention by default, so capturing it to disk
        is the only way to see the history.

        The format includes Time, Type, Action, image, and container name, which
        is enough to identify the command that produced each event when
        correlated with the .last-seed breadcrumb and the inotify audit log.

    .PARAMETER LogPath
        Absolute host path to write the events log to.

    .OUTPUTS
        System.Diagnostics.Process for the docker events stream, or $null on
        failure.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$LogPath
    )

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return $null
    }

    $logDir = Split-Path -Parent $LogPath
    if ($logDir -and -not (Test-Path $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    try {
        # ProcessStartInfo + ArgumentList preserves argv boundaries on Linux
        # pwsh; `Start-Process -ArgumentList` joins the array with spaces, which
        # then retokenises the format string (its internal spaces cause docker
        # to see the trailing words as positional args, not part of --format).
        # We also want stdout flushed line-by-line so a live `tail -f` sees
        # events as they happen — stdbuf -oL in front of docker forces that.
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = '/usr/bin/stdbuf'
        $psi.ArgumentList.Add('-oL')
        $psi.ArgumentList.Add('docker')
        $psi.ArgumentList.Add('events')
        $psi.ArgumentList.Add('--format')
        $psi.ArgumentList.Add('{{.Time}} {{.Type}} {{.Action}} image={{.Actor.Attributes.image}} name={{.Actor.Attributes.name}}')
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true

        $process = [System.Diagnostics.Process]::Start($psi)

        # Pump stdout/stderr to the log files on background threads so we don't
        # block the parent and the pipe doesn't fill up.
        $outStream = [System.IO.StreamWriter]::new($LogPath, $true)
        $errStream = [System.IO.StreamWriter]::new("$LogPath.stderr", $true)
        $outStream.AutoFlush = $true
        $errStream.AutoFlush = $true

        # Register handlers BEFORE BeginOutputReadLine so we don't race early lines.
        $outStream | Add-Member -Name 'Proc' -MemberType NoteProperty -Value $process -Force
        Register-ObjectEvent -InputObject $process -EventName OutputDataReceived -MessageData $outStream -Action {
            if ($EventArgs.Data) { $Event.MessageData.WriteLine($EventArgs.Data) }
        } | Out-Null
        Register-ObjectEvent -InputObject $process -EventName ErrorDataReceived -MessageData $errStream -Action {
            if ($EventArgs.Data) { $Event.MessageData.WriteLine($EventArgs.Data) }
        } | Out-Null
        $process.BeginOutputReadLine()
        $process.BeginErrorReadLine()

        # Tag the process with the streams so Stop-DockerEventsCapture can close them.
        $process | Add-Member -Name 'OutStream' -MemberType NoteProperty -Value $outStream -Force
        $process | Add-Member -Name 'ErrStream' -MemberType NoteProperty -Value $errStream -Force
        return $process
    }
    catch {
        Write-Host "  (docker events capture failed to start: $_)" -ForegroundColor DarkYellow
        return $null
    }
}

function Stop-DockerEventsCapture {
    <#
    .SYNOPSIS
        Stop the docker events stream started by Start-DockerEventsCapture.

    .DESCRIPTION
        Kills the docker events process. Safe to call with $null or an already-
        exited process; safe to call multiple times. Always call from a
        `finally` block.

    .PARAMETER Process
        The process handle returned by Start-DockerEventsCapture, or $null.

    .OUTPUTS
        Returns the number of event lines captured (0 if missing).
    #>
    param(
        [Parameter(Mandatory=$false)]
        $Process,

        [Parameter(Mandatory=$false)]
        [string]$LogPath
    )

    if ($null -ne $Process -and -not $Process.HasExited) {
        try {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        }
        catch {
            # Best effort; docker events sometimes exits on its own when the
            # daemon restarts, in which case HasExited lies briefly.
        }
    }

    # Close the stream writers so final buffered events flush to disk before
    # we count lines. Guarded because a $null Process skipped them entirely.
    if ($null -ne $Process) {
        try {
            $Process.WaitForExit(2000) | Out-Null
        }
        catch { }
        foreach ($propName in 'OutStream', 'ErrStream') {
            $s = $Process.PSObject.Properties[$propName]
            if ($s -and $s.Value) {
                try { $s.Value.Flush(); $s.Value.Dispose() } catch { }
            }
        }
    }

    if ($LogPath -and (Test-Path $LogPath)) {
        return @(Get-Content -Path $LogPath -ErrorAction SilentlyContinue).Count
    }
    return 0
}

function Assert-NoWorkerErrors {
    <#
    .SYNOPSIS
        Post-scenario scan of JIM container logs for Error/Fatal lines.

    .DESCRIPTION
        Belt-and-braces companion to the live watcher. Runs a one-shot
        `docker logs --since <time>` against each target container and fails the
        scenario if any Error or Fatal level line (see Get-JimErrorLinePattern)
        is found. Use this even if the live watcher reported nothing, to catch
        lines that raced the watcher's start-up or shutdown.

    .PARAMETER Since
        DateTime marking the start of the log window.

    .PARAMETER AllowPattern
        Optional regex; matching lines are ignored.

    .PARAMETER Containers
        Containers to scan. Defaults to jim.web, jim.worker, jim.scheduler.

    .OUTPUTS
        Throws if any Error/Fatal line is found (outside the allowlist). Returns
        quietly on success.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [datetime]$Since,

        [Parameter(Mandatory=$false)]
        [string]$AllowPattern = '',

        [Parameter(Mandatory=$false)]
        [string[]]$Containers = @('jim.web', 'jim.worker', 'jim.scheduler')
    )

    $sinceString = $Since.AddSeconds(-1).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $allErrors = @()
    $errorPattern = Get-JimErrorLinePattern

    foreach ($container in $Containers) {
        # 2>&1 merges stderr (where some log sinks write) into stdout so the
        # Select-String below sees every log line, not just stdout.
        $output = docker logs --since $sinceString $container 2>&1
        $errorLines = $output | Where-Object { $_ -match $errorPattern }
        if ($AllowPattern) {
            $errorLines = $errorLines | Where-Object { $_ -notmatch $AllowPattern }
        }
        foreach ($line in $errorLines) {
            $allErrors += "[$container] $line"
        }
    }

    if ($allErrors.Count -gt 0) {
        Write-Host "✗ FAILED: Detected $($allErrors.Count) Error/Fatal line(s) in JIM container logs:" -ForegroundColor Red
        foreach ($line in $allErrors | Select-Object -First 20) {
            Write-Host "    $line" -ForegroundColor Red
        }
        if ($allErrors.Count -gt 20) {
            Write-Host "    ... ($($allErrors.Count - 20) more not shown)" -ForegroundColor Red
        }
        throw "JIM logged $($allErrors.Count) Error/Fatal line(s) during the scenario. See output above."
    }

    Write-Host "✓ PASSED: No Error/Fatal lines in jim.web, jim.worker, or jim.scheduler logs" -ForegroundColor Green
}

# Functions are automatically available when dot-sourced
# No need for Export-ModuleMember
