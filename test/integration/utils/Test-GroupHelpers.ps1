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
        [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
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
        XLarge = @{
            Companies = 10
            Departments = 15
            Locations = 15
            Projects = 2000
            TotalGroups = 2040
            Users = 100000
        }
        XXLarge = @{
            Companies = 15
            Departments = 20
            Locations = 20
            Projects = 10000
            TotalGroups = 10055
            Users = 1000000
        }
    }

    return $scales[$Template]
}

# Reference data for group names
$script:CompanyNames = @(
    "Subatomic", "NexusDynamics", "OrbitalSystems", "QuantumBridge", "StellarLogistics",
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
        - Department: 100%
        - Location: 80%
        - Project: 60%
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Company", "Department", "Location", "Project")]
        [string]$Category,

        [Parameter(Mandatory=$true)]
        [int]$Index
    )

    switch ($Category) {
        "Company" { return $true }
        "Department" { return $true }
        "Location" { return ($Index % 5) -ne 0 }  # 80%
        "Project" { return ($Index % 5) -lt 3 }   # 60%
    }
}

function New-TestGroup {
    <#
    .SYNOPSIS
        Generate a test group object with all required attributes

    .PARAMETER Category
        Group category: Company, Department, Location, or Project

    .PARAMETER Name
        The name portion (e.g., "Engineering" for "Dept-Engineering")

    .PARAMETER Index
        Index for distribution calculations

    .PARAMETER Domain
        Domain suffix for email addresses (e.g., "sourcedomain.local")
    #>
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Company", "Department", "Location", "Project")]
        [string]$Category,

        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$true)]
        [int]$Index,

        [Parameter(Mandatory=$false)]
        [string]$Domain = "sourcedomain.local"
    )

    # Build group name based on category
    $prefix = switch ($Category) {
        "Company" { "Company" }
        "Department" { "Dept" }
        "Location" { "Location" }
        "Project" { "Project" }
    }
    $groupName = "$prefix-$Name"
    $sAMAccountName = $groupName

    # Get type distribution
    $typeInfo = Get-GroupTypeDistribution -Index $Index

    # Build description
    $description = switch ($Category) {
        "Company" { "Company-wide group for $Name" }
        "Department" { "Department group for $Name" }
        "Location" { "Location group for $Name office" }
        "Project" { "Project team for $Name" }
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
        [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
        [string]$Template,

        [Parameter(Mandatory=$false)]
        [string]$Domain = "sourcedomain.local"
    )

    $scale = Get-Scenario8GroupScale -Template $Template
    $groups = @()
    $globalIndex = 0

    # Company groups
    for ($i = 0; $i -lt $scale.Companies; $i++) {
        $name = $script:CompanyNames[$i % $script:CompanyNames.Count]
        $group = New-TestGroup -Category "Company" -Name $name -Index $globalIndex -Domain $Domain
        $groups += $group
        $globalIndex++
    }

    # Department groups
    for ($i = 0; $i -lt $scale.Departments; $i++) {
        $name = $script:DepartmentNames[$i % $script:DepartmentNames.Count]
        $group = New-TestGroup -Category "Department" -Name $name -Index $globalIndex -Domain $Domain
        $groups += $group
        $globalIndex++
    }

    # Location groups
    for ($i = 0; $i -lt $scale.Locations; $i++) {
        $name = $script:LocationNames[$i % $script:LocationNames.Count]
        $group = New-TestGroup -Category "Location" -Name $name -Index $globalIndex -Domain $Domain
        $groups += $group
        $globalIndex++
    }

    # Project groups
    $projectNames = Get-ProjectNames -Count $scale.Projects
    for ($i = 0; $i -lt $scale.Projects; $i++) {
        $name = $projectNames[$i]
        $group = New-TestGroup -Category "Project" -Name $name -Index $globalIndex -Domain $Domain
        $groups += $group
        $globalIndex++
    }

    return $groups
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
