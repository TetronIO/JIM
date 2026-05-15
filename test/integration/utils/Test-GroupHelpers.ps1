# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    Group helper functions for Scenario 8 integration testing

.DESCRIPTION
    Provides functions to generate test groups with realistic enterprise distribution
    including various group types, scopes, and mail enablement states
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Group type flag constants (AD groupType attribute is a bitmask)
$script:GroupTypeFlags = @{
    GlobalScope        = 0x00000002   # 2
    DomainLocalScope   = 0x00000004   # 4
    UniversalScope     = 0x00000008   # 8
    SecurityEnabled    = 0x80000000   # -2147483648 (signed int)
}

# Pre-calculated group type values
$script:GroupTypeValues = @{
    GlobalSecurity       = -2147483646  # 0x80000002
    DomainLocalSecurity  = -2147483644  # 0x80000004
    UniversalSecurity    = -2147483640  # 0x80000008
    GlobalDistribution   = 2            # 0x00000002
    DomainLocalDistribution = 4         # 0x00000004
    UniversalDistribution = 8           # 0x00000008
}

function Get-GroupTypeFlags {
    <#
    .SYNOPSIS
        Get the groupType integer value for a given type and scope combination
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Security", "Distribution")]
        [string]$Type,

        [Parameter(Mandatory=$true)]
        [ValidateSet("Global", "DomainLocal", "Universal")]
        [string]$Scope
    )

    $key = "${Scope}${Type}"
    if ($script:GroupTypeValues.ContainsKey($key)) {
        return $script:GroupTypeValues[$key]
    }

    throw "Unknown group type combination: $Type, $Scope"
}

function Get-Scenario8GroupScale {
    <#
    .SYNOPSIS
        Get the group counts for each category based on template scale
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups")]
        [string]$Template
    )

    # Scale definitions from the plan document
    $scales = @{
        Nano = @{
            Companies = 1
            Departments = 2
            Locations = 1
            Projects = 2
            TotalGroups = 6
            Users = 3
        }
        Micro = @{
            Companies = 2
            Departments = 3
            Locations = 2
            Projects = 5
            TotalGroups = 12
            Users = 10
        }
        Small = @{
            Companies = 3
            Departments = 5
            Locations = 3
            Projects = 20
            TotalGroups = 31
            Users = 100
        }
        Medium = @{
            Companies = 5
            Departments = 8
            Locations = 5
            Projects = 100
            TotalGroups = 118
            Users = 1000
        }
        MediumLarge = @{
            Companies = 5
            Departments = 10
            Locations = 8
            Projects = 250
            TotalGroups = 273
            Users = 5000
        }
        Large = @{
            Companies = 8
            Departments = 12
            Locations = 10
            Projects = 500
            TotalGroups = 530
            Users = 10000
        }
        # Scale templates: Capped group counts to keep total memberships manageable.
        # samba-tool group addmembers holds an LDB write lock per call, so millions
        # of membership writes are impractical. Fewer groups with varied sizes gives
        # better test coverage without the combinatorial explosion.
        Scale100k50Groups = @{
            Companies = 5
            Departments = 10
            Locations = 5
            Projects = 30
            TotalGroups = 50
            Users = 100000
        }
        Scale200k55Groups = @{
            Companies = 5
            Departments = 10
            Locations = 5
            Projects = 35
            TotalGroups = 55
            Users = 200000
        }
        Scale500k65Groups = @{
            Companies = 5
            Departments = 10
            Locations = 5
            Projects = 45
            TotalGroups = 65
            Users = 500000
        }
        Scale750k70Groups = @{
            Companies = 5
            Departments = 10
            Locations = 5
            Projects = 50
            TotalGroups = 70
            Users = 750000
        }
        Scale1m80Groups = @{
            Companies = 5
            Departments = 10
            Locations = 5
            Projects = 50
            TotalGroups = 70
            Users = 1000000
        }
        # Scale100k5kGroups: realistic long-tail distribution for a 100k-person org.
        # Mostly small groups (project teams, app access, distribution lists, roles)
        # plus a handful of very large groups (all-staff, divisions). OpenLDAP only;
        # Samba AD cannot populate this shape within the time budget due to its
        # per-call LDB write lock. Enforced by hard-fail guards in the populators.
        Scale100k5kGroups = @{
            Companies         = 0       # retired for this template; superseded by AllStaff + Divisions
            AllStaff          = 2       # ~80k-100k members each
            Divisions         = 10      # ~5k-25k members each
            Locations         = 15      # ~1k-10k members each
            Departments       = 100     # ~200-3,000 members each (long-tail starts here)
            Applications      = 500     # ~10-300 members each
            Projects          = 3000    # ~5-30 members each (bulk of the long-tail)
            DistributionLists = 1000    # ~10-150 members each
            Roles             = 400     # ~20-500 members each
            TotalGroups       = 5027
            Users             = 100000
        }
    }

    return $scales[$Template]
}

# Reference data for group names
$script:CompanyNames = @(
    "Panoply", "NexusDynamics", "OrbitalSystems", "QuantumBridge", "StellarLogistics",
    "VortexTech", "CatalystCorp", "HorizonIndustries", "PulsarEnterprises", "NovaNetworks",
    "FusionCore", "CelestialSystems", "NebulaWorks", "AtomicVentures", "CosmicPlatform"
)

$script:DepartmentNames = @(
    "Engineering", "Finance", "Human-Resources", "Information-Technology", "Legal",
    "Marketing", "Operations", "Procurement", "Research-Development", "Sales",
    "Customer-Support", "Quality-Assurance", "Product-Management", "Data-Science",
    "Security", "Facilities", "Executive", "Compliance", "Communications", "Training"
)

$script:LocationNames = @(
    "Sydney", "Melbourne", "London", "Manchester", "NewYork", "SanFrancisco",
    "Tokyo", "Singapore", "Berlin", "Paris", "Toronto", "Dubai", "Mumbai",
    "Shanghai", "HongKong", "Seoul", "Amsterdam", "Zurich", "Dublin", "Stockholm"
)

$script:ProjectAdjectives = @(
    "Agile", "Digital", "Global", "Smart", "Cloud", "Next", "Ultra", "Hyper",
    "Prime", "Alpha", "Delta", "Omega", "Apex", "Titan", "Rapid", "Swift"
)

$script:ProjectNouns = @(
    "Phoenix", "Titan", "Mercury", "Apollo", "Voyager", "Nebula", "Quantum", "Fusion",
    "Platform", "Gateway", "Engine", "Hub", "Core", "Bridge", "Matrix", "Vector",
    "Horizon", "Catalyst", "Pulse", "Nova", "Vertex", "Zenith", "Prism", "Vortex"
)

# Name pools for the long-tail group categories used by Scale100k5kGroups.
$script:AllStaffNames = @("AllEmployees", "AllStaff")

$script:DivisionNames = @(
    "Engineering", "Sales", "Operations", "Finance", "Technology",
    "CustomerSuccess", "Manufacturing", "Logistics", "Research", "MarketingComms",
    "ProductGroup", "FieldServices", "ProfessionalServices", "CorporateAffairs", "RiskAndCompliance"
)

$script:ApplicationCoreNames = @(
    "CRM", "ERP", "HRPortal", "Salesforce", "Workday", "Jira", "Confluence", "GitHub",
    "Slack", "Teams", "Outlook", "SharePoint", "ServiceNow", "Tableau", "PowerBI",
    "Splunk", "Datadog", "PagerDuty", "Zoom", "Box", "Dropbox", "Office365",
    "AzurePortal", "AWSConsole", "Okta", "Auth0", "OnePassword", "Snowflake",
    "Databricks", "Looker", "Notion", "Asana", "Miro", "Figma", "Adobe",
    "DocuSign", "Zendesk", "Intercom", "HubSpot", "Marketo"
)

$script:ApplicationAccessLevels = @("Users", "ReadOnly", "Admins", "Power", "External")

$script:RoleNouns = @(
    "Admins", "Operators", "Auditors", "Viewers", "Managers", "Contributors",
    "Reviewers", "Approvers", "Owners", "Members", "Editors", "Publishers"
)

$script:RoleQualifiers = @(
    "Server", "Network", "Database", "Storage", "Security", "Backup", "Identity",
    "Workstation", "Mobile", "Print", "VPN", "Firewall", "AD", "DNS", "DHCP",
    "Helpdesk", "Tier1", "Tier2", "Tier3", "Compliance", "GDPR", "SOX", "PCI", "ISO27001",
    "Finance", "HR", "Payroll", "Procurement", "Legal", "Marketing", "Sales"
)

$script:DistributionListThemes = @(
    "Announcements", "Updates", "News", "Bulletin", "Digest", "Newsletter",
    "Community", "Guild", "Club", "Group", "Network", "Forum", "Circle",
    "Alerts", "Notifications"
)

$script:DistributionListTopics = @(
    "Java", "Python", "Go", "DotNet", "TypeScript", "Rust", "Kubernetes", "Docker",
    "Cloud", "Security", "DevOps", "MLAI", "Data", "Platform", "Mobile", "WebDev",
    "QA", "UX", "DesignSystems", "API", "Coffee", "Books", "Gaming", "Cycling",
    "Running", "Photography", "Cooking", "Travel", "Wellness", "Volunteering",
    "Diversity", "Mentoring", "Learning", "Onboarding", "Alumni", "RemoteWork",
    "OfficeLondon", "OfficeNewYork", "OfficeTokyo", "OfficeBerlin"
)

function Get-CombinatorialNames {
    <#
    .SYNOPSIS
        Generate Count unique names by combining prefixes and suffixes deterministically.

    .DESCRIPTION
        Used to produce arbitrarily large name pools (e.g. 500 application access
        groups, 1000 distribution lists) without needing a hand-curated list.
        First emits all prefix-suffix combinations, then appends numeric suffixes
        if still short of Count.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$Count,

        [Parameter(Mandatory=$true)]
        [string[]]$Prefixes,

        [Parameter(Mandatory=$true)]
        [string[]]$Suffixes,

        [Parameter(Mandatory=$false)]
        [string]$Separator = "-"
    )

    $names = [System.Collections.Generic.List[string]]::new()
    $seen = @{}

    foreach ($prefix in $Prefixes) {
        foreach ($suffix in $Suffixes) {
            if ($names.Count -ge $Count) { break }
            $name = "${prefix}${Separator}${suffix}"
            if (-not $seen.ContainsKey($name)) {
                [void]$names.Add($name)
                $seen[$name] = $true
            }
        }
        if ($names.Count -ge $Count) { break }
    }

    # Pad with numeric suffixes if needed
    $counter = 1
    while ($names.Count -lt $Count) {
        foreach ($prefix in $Prefixes) {
            foreach ($suffix in $Suffixes) {
                if ($names.Count -ge $Count) { break }
                $name = "${prefix}${Separator}${suffix}${counter}"
                if (-not $seen.ContainsKey($name)) {
                    [void]$names.Add($name)
                    $seen[$name] = $true
                }
            }
            if ($names.Count -ge $Count) { break }
        }
        $counter++
    }

    return $names[0..($Count - 1)]
}

function Get-LongTailGroupSize {
    <#
    .SYNOPSIS
        Return a target member count for a group following a category-specific long-tail distribution.

    .DESCRIPTION
        Used by the Scale100k5kGroups populator. Each category has its own size
        profile chosen to mirror real-enterprise topology:
        - AllStaff: 80-100% of users (very large)
        - Division: 5-25% of users
        - Location: 1-10% of users
        - Department: 200-3000 members
        - Application: 10-300 members
        - Project: 5-30 members (the bulk of the long-tail)
        - DistributionList: 10-150 members
        - Role: 20-500 members

        The Index parameter rotates through a small set of size tiers within the
        category, producing deterministic but varied sizes across the population
        of groups in that category.
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("AllStaff", "Division", "Location", "Department", "Application", "Project", "DistributionList", "Role")]
        [string]$Category,

        [Parameter(Mandatory=$true)]
        [int]$Index,

        [Parameter(Mandatory=$true)]
        [int]$UserCount
    )

    # Size tier tables per category. Each tier is a fraction-of-users or
    # absolute member count; the populator picks tier = Index modulo Count.
    switch ($Category) {
        "AllStaff" {
            # 2 groups: one truly everyone, one ~80%
            $tiers = @(1.0, 0.8)
            return [Math]::Max(1, [Math]::Floor($UserCount * $tiers[$Index % $tiers.Count]))
        }
        "Division" {
            # 10 groups, varying from 5% to 25% of users
            $tiers = @(0.25, 0.20, 0.18, 0.15, 0.12, 0.10, 0.08, 0.07, 0.06, 0.05)
            return [Math]::Max(1, [Math]::Floor($UserCount * $tiers[$Index % $tiers.Count]))
        }
        "Location" {
            # 15 groups, varying from 1% to 10% of users
            $tiers = @(0.10, 0.08, 0.06, 0.05, 0.04, 0.03, 0.025, 0.02, 0.015, 0.012, 0.01, 0.008, 0.006, 0.004, 0.002)
            return [Math]::Max(1, [Math]::Floor($UserCount * $tiers[$Index % $tiers.Count]))
        }
        "Department" {
            # 100 groups, absolute sizes 200-3000 with weighted distribution
            $tiers = @(3000, 2500, 2000, 1500, 1200, 1000, 800, 600, 500, 400, 300, 250, 200)
            return [Math]::Min($UserCount, $tiers[$Index % $tiers.Count])
        }
        "Application" {
            # 500 groups, absolute sizes 10-300 weighted toward smaller
            $tiers = @(300, 200, 150, 100, 80, 60, 50, 40, 30, 25, 20, 15, 10)
            return [Math]::Min($UserCount, $tiers[$Index % $tiers.Count])
        }
        "Project" {
            # 3000 groups, absolute sizes 5-30 with a few larger outliers
            $tiers = @(30, 25, 20, 18, 15, 12, 10, 8, 7, 6, 5, 8, 10, 50, 5)
            return [Math]::Min($UserCount, $tiers[$Index % $tiers.Count])
        }
        "DistributionList" {
            # 1000 groups, absolute sizes 10-150 with broad spread
            $tiers = @(150, 120, 100, 80, 60, 50, 40, 30, 25, 20, 15, 12, 10)
            return [Math]::Min($UserCount, $tiers[$Index % $tiers.Count])
        }
        "Role" {
            # 400 groups, absolute sizes 20-500 (RBAC-style, often 10s to low 100s)
            $tiers = @(500, 300, 200, 150, 100, 80, 60, 50, 40, 30, 25, 20)
            return [Math]::Min($UserCount, $tiers[$Index % $tiers.Count])
        }
    }
}

function Get-ProjectNames {
    <#
    .SYNOPSIS
        Generate unique project names using adjective + noun combinations
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$Count
    )

    $names = @()
    $usedCombinations = @{}

    # First use single nouns for simpler names
    foreach ($noun in $script:ProjectNouns) {
        if ($names.Count -ge $Count) { break }
        $names += $noun
        $usedCombinations[$noun] = $true
    }

    # Then use adjective + noun combinations
    foreach ($adj in $script:ProjectAdjectives) {
        foreach ($noun in $script:ProjectNouns) {
            if ($names.Count -ge $Count) { break }
            $name = "$adj$noun"
            if (-not $usedCombinations.ContainsKey($name)) {
                $names += $name
                $usedCombinations[$name] = $true
            }
        }
        if ($names.Count -ge $Count) { break }
    }

    # If we still need more, add numbers
    $counter = 1
    while ($names.Count -lt $Count) {
        foreach ($noun in $script:ProjectNouns) {
            if ($names.Count -ge $Count) { break }
            $name = "${noun}${counter}"
            $names += $name
        }
        $counter++
    }

    return $names[0..($Count - 1)]
}

function Get-GroupTypeDistribution {
    <#
    .SYNOPSIS
        Get the group type configuration based on index for realistic distribution

    .DESCRIPTION
        Returns group type, scope, and mail enablement based on the detailed distribution:
        - 30% Security Universal Mail-Enabled
        - 25% Security Universal Not Mail-Enabled
        - 15% Security Global Not Mail-Enabled
        - 5% Security Domain Local Not Mail-Enabled
        - 20% Distribution Universal Mail-Enabled
        - 5% Distribution Global Mail-Enabled
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$Index
    )

    # Use modulo 100 for distribution percentage
    $bucket = $Index % 100

    if ($bucket -lt 30) {
        # 30% - Security Universal Mail-Enabled
        return @{
            Type = "Security"
            Scope = "Universal"
            MailEnabled = $true
            GroupTypeValue = $script:GroupTypeValues.UniversalSecurity
        }
    }
    elseif ($bucket -lt 55) {
        # 25% - Security Universal Not Mail-Enabled
        return @{
            Type = "Security"
            Scope = "Universal"
            MailEnabled = $false
            GroupTypeValue = $script:GroupTypeValues.UniversalSecurity
        }
    }
    elseif ($bucket -lt 70) {
        # 15% - Security Global Not Mail-Enabled
        return @{
            Type = "Security"
            Scope = "Global"
            MailEnabled = $false
            GroupTypeValue = $script:GroupTypeValues.GlobalSecurity
        }
    }
    elseif ($bucket -lt 75) {
        # 5% - Security Domain Local Not Mail-Enabled
        return @{
            Type = "Security"
            Scope = "DomainLocal"
            MailEnabled = $false
            GroupTypeValue = $script:GroupTypeValues.DomainLocalSecurity
        }
    }
    elseif ($bucket -lt 95) {
        # 20% - Distribution Universal Mail-Enabled
        return @{
            Type = "Distribution"
            Scope = "Universal"
            MailEnabled = $true
            GroupTypeValue = $script:GroupTypeValues.UniversalDistribution
        }
    }
    else {
        # 5% - Distribution Global Mail-Enabled
        return @{
            Type = "Distribution"
            Scope = "Global"
            MailEnabled = $true
            GroupTypeValue = $script:GroupTypeValues.GlobalDistribution
        }
    }
}

function Get-ManagedByDistribution {
    <#
    .SYNOPSIS
        Determine if a group should have managedBy based on category

    .DESCRIPTION
        Returns $true or $false based on category distribution:
        - Company: 100%
        - AllStaff: 100%
        - Division: 100%
        - Department: 100%
        - Location: 80%
        - Project: 60%
        - Application: 70%
        - DistributionList: 50%
        - Role: 80%
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Company", "AllStaff", "Division", "Department", "Location", "Project", "Application", "DistributionList", "Role")]
        [string]$Category,

        [Parameter(Mandatory=$true)]
        [int]$Index
    )

    switch ($Category) {
        "Company"          { return $true }
        "AllStaff"         { return $true }
        "Division"         { return $true }
        "Department"       { return $true }
        "Location"         { return ($Index % 5) -ne 0 }                                  # 80%
        "Project"          { return ($Index % 5) -lt 3 }                                  # 60%
        "Application"      { return ($Index % 10) -lt 7 }                                 # 70%
        "DistributionList" { return ($Index % 2) -eq 0 }                                  # 50%
        "Role"             { return ($Index % 5) -ne 0 }                                  # 80%
    }
}

function New-TestGroup {
    <#
    .SYNOPSIS
        Generate a test group object with all required attributes

    .PARAMETER Category
        Group category: Company, AllStaff, Division, Department, Location, Project,
        Application, DistributionList, or Role.

    .PARAMETER Name
        The name portion (e.g., "Engineering" for "Dept-Engineering")

    .PARAMETER Index
        Index for distribution calculations

    .PARAMETER Domain
        Domain suffix for email addresses (e.g., "resurgam.local")
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Company", "AllStaff", "Division", "Department", "Location", "Project", "Application", "DistributionList", "Role")]
        [string]$Category,

        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$true)]
        [int]$Index,

        [Parameter(Mandatory=$false)]
        [string]$Domain = "resurgam.local"
    )

    # Build group name based on category
    $prefix = switch ($Category) {
        "Company"          { "Company" }
        "AllStaff"         { "AllStaff" }
        "Division"         { "Division" }
        "Department"       { "Dept" }
        "Location"         { "Location" }
        "Project"          { "Project" }
        "Application"      { "App" }
        "DistributionList" { "DL" }
        "Role"             { "Role" }
    }
    $groupName = "$prefix-$Name"
    $sAMAccountName = $groupName

    # Get type distribution. Some categories override the default mix to reflect
    # real-world conventions (distribution lists are always mail-enabled
    # distribution groups; roles are always non-mail security groups).
    $typeInfo = switch ($Category) {
        "DistributionList" {
            @{
                Type           = "Distribution"
                Scope          = "Universal"
                MailEnabled    = $true
                GroupTypeValue = $script:GroupTypeValues.UniversalDistribution
            }
        }
        "Role" {
            @{
                Type           = "Security"
                Scope          = "Universal"
                MailEnabled    = $false
                GroupTypeValue = $script:GroupTypeValues.UniversalSecurity
            }
        }
        default { Get-GroupTypeDistribution -Index $Index }
    }

    # Build description
    $description = switch ($Category) {
        "Company"          { "Company-wide group for $Name" }
        "AllStaff"         { "All-staff group: $Name" }
        "Division"         { "Division group for $Name" }
        "Department"       { "Department group for $Name" }
        "Location"         { "Location group for $Name office" }
        "Project"          { "Project team for $Name" }
        "Application"      { "Application access group: $Name" }
        "DistributionList" { "Distribution list: $Name" }
        "Role"             { "RBAC role: $Name" }
    }

    # Mail attributes (only if mail-enabled)
    $mail = $null
    $mailNickname = $null
    if ($typeInfo.MailEnabled) {
        $mail = "$($groupName.ToLower())@$Domain"
        $mailNickname = $groupName
    }

    # managedBy distribution
    $hasManagedBy = Get-ManagedByDistribution -Category $Category -Index $Index

    return @{
        # Identity
        Name = $groupName
        SAMAccountName = $sAMAccountName
        DisplayName = $groupName
        CN = $groupName
        Description = $description

        # Type and scope
        Category = $Category
        Type = $typeInfo.Type
        Scope = $typeInfo.Scope
        GroupType = $typeInfo.GroupTypeValue
        MailEnabled = $typeInfo.MailEnabled

        # Mail attributes
        Mail = $mail
        MailNickname = $mailNickname

        # Reference attributes
        HasManagedBy = $hasManagedBy
        ManagedBy = $null  # Set later after users are created
        Members = @()      # Set later after users are created
    }
}

function New-Scenario8GroupSet {
    <#
    .SYNOPSIS
        Generate a complete set of groups for Scenario 8 based on template scale

    .PARAMETER Template
        Data scale template (Nano, Micro, Small, etc.)

    .PARAMETER Domain
        Domain suffix for email addresses

    .RETURNS
        Array of group objects ready for population
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups")]
        [string]$Template,

        [Parameter(Mandatory=$false)]
        [string]$Domain = "resurgam.local"
    )

    $scale = Get-Scenario8GroupScale -Template $Template
    $groups = [System.Collections.Generic.List[object]]::new()
    $globalIndex = 0

    # Helper: read a possibly-missing hashtable key as an integer (default 0).
    # Some scales only define a subset of categories (the legacy four), so the
    # long-tail categories return null on older templates which we coerce to 0
    # for loop bounds.
    function Read-CategoryCount {
        param($Hashtable, [string]$Key)
        if ($Hashtable.ContainsKey($Key)) { return [int]$Hashtable[$Key] }
        return 0
    }

    $companyCount       = Read-CategoryCount $scale 'Companies'
    $allStaffCount      = Read-CategoryCount $scale 'AllStaff'
    $divisionCount      = Read-CategoryCount $scale 'Divisions'
    $departmentCount    = Read-CategoryCount $scale 'Departments'
    $locationCount      = Read-CategoryCount $scale 'Locations'
    $projectCount       = Read-CategoryCount $scale 'Projects'
    $applicationCount   = Read-CategoryCount $scale 'Applications'
    $distributionCount  = Read-CategoryCount $scale 'DistributionLists'
    $roleCount          = Read-CategoryCount $scale 'Roles'

    # Company groups (legacy templates)
    for ($i = 0; $i -lt $companyCount; $i++) {
        $name = $script:CompanyNames[$i % $script:CompanyNames.Count]
        [void]$groups.Add((New-TestGroup -Category "Company" -Name $name -Index $globalIndex -Domain $Domain))
        $globalIndex++
    }

    # AllStaff groups (Scale100k5kGroups)
    for ($i = 0; $i -lt $allStaffCount; $i++) {
        $name = $script:AllStaffNames[$i % $script:AllStaffNames.Count]
        [void]$groups.Add((New-TestGroup -Category "AllStaff" -Name $name -Index $globalIndex -Domain $Domain))
        $globalIndex++
    }

    # Division groups (Scale100k5kGroups)
    for ($i = 0; $i -lt $divisionCount; $i++) {
        $name = $script:DivisionNames[$i % $script:DivisionNames.Count]
        [void]$groups.Add((New-TestGroup -Category "Division" -Name $name -Index $globalIndex -Domain $Domain))
        $globalIndex++
    }

    # Department groups
    # When the count exceeds the curated DepartmentNames pool, suffix with an
    # index to keep names unique (e.g. Dept-Engineering, Dept-Engineering2, ...).
    for ($i = 0; $i -lt $departmentCount; $i++) {
        $baseName = $script:DepartmentNames[$i % $script:DepartmentNames.Count]
        $rotation = [Math]::Floor($i / $script:DepartmentNames.Count)
        $name = if ($rotation -eq 0) { $baseName } else { "${baseName}${rotation}" }
        [void]$groups.Add((New-TestGroup -Category "Department" -Name $name -Index $globalIndex -Domain $Domain))
        $globalIndex++
    }

    # Location groups (same suffix-on-overflow pattern as departments)
    for ($i = 0; $i -lt $locationCount; $i++) {
        $baseName = $script:LocationNames[$i % $script:LocationNames.Count]
        $rotation = [Math]::Floor($i / $script:LocationNames.Count)
        $name = if ($rotation -eq 0) { $baseName } else { "${baseName}${rotation}" }
        [void]$groups.Add((New-TestGroup -Category "Location" -Name $name -Index $globalIndex -Domain $Domain))
        $globalIndex++
    }

    # Project groups
    if ($projectCount -gt 0) {
        $projectNames = Get-ProjectNames -Count $projectCount
        for ($i = 0; $i -lt $projectCount; $i++) {
            [void]$groups.Add((New-TestGroup -Category "Project" -Name $projectNames[$i] -Index $globalIndex -Domain $Domain))
            $globalIndex++
        }
    }

    # Application access groups (Scale100k5kGroups)
    if ($applicationCount -gt 0) {
        $appNames = Get-CombinatorialNames -Count $applicationCount `
            -Prefixes $script:ApplicationCoreNames `
            -Suffixes $script:ApplicationAccessLevels
        for ($i = 0; $i -lt $applicationCount; $i++) {
            [void]$groups.Add((New-TestGroup -Category "Application" -Name $appNames[$i] -Index $globalIndex -Domain $Domain))
            $globalIndex++
        }
    }

    # Distribution lists (Scale100k5kGroups)
    if ($distributionCount -gt 0) {
        $dlNames = Get-CombinatorialNames -Count $distributionCount `
            -Prefixes $script:DistributionListTopics `
            -Suffixes $script:DistributionListThemes
        for ($i = 0; $i -lt $distributionCount; $i++) {
            [void]$groups.Add((New-TestGroup -Category "DistributionList" -Name $dlNames[$i] -Index $globalIndex -Domain $Domain))
            $globalIndex++
        }
    }

    # Roles (Scale100k5kGroups)
    if ($roleCount -gt 0) {
        $roleNames = Get-CombinatorialNames -Count $roleCount `
            -Prefixes $script:RoleQualifiers `
            -Suffixes $script:RoleNouns
        for ($i = 0; $i -lt $roleCount; $i++) {
            [void]$groups.Add((New-TestGroup -Category "Role" -Name $roleNames[$i] -Index $globalIndex -Domain $Domain))
            $globalIndex++
        }
    }

    return $groups.ToArray()
}

function Get-ADGroupScopeString {
    <#
    .SYNOPSIS
        Convert scope name to samba-tool compatible string
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Global", "DomainLocal", "Universal")]
        [string]$Scope
    )

    switch ($Scope) {
        "Global" { return "Global" }
        "DomainLocal" { return "Domain" }  # samba-tool uses "Domain" for Domain Local
        "Universal" { return "Universal" }
    }
}

function Get-ADGroupTypeString {
    <#
    .SYNOPSIS
        Convert type name to samba-tool compatible string
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Security", "Distribution")]
        [string]$Type
    )

    switch ($Type) {
        "Security" { return "Security" }
        "Distribution" { return "Distribution" }
    }
}

# Export functions (when dot-sourced, all functions are available)
