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

function Write-TestSection {
    <#
    .SYNOPSIS
        Write a test section header
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$Title
    )

    Write-Host "`n===========================================" -ForegroundColor Cyan
    Write-Host " $Title" -ForegroundColor Cyan
    Write-Host "===========================================" -ForegroundColor Cyan
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
        [ValidateSet("Micro", "Small", "Medium", "Large", "XLarge", "XXLarge")]
        [string]$Template
    )

    $scales = @{
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
        Large = @{
            Users = 10000
            Groups = 500
            AvgMemberships = 10
        }
        XLarge = @{
            Users = 100000
            Groups = 2000
            AvgMemberships = 12
        }
        XXLarge = @{
            Users = 1000000
            Groups = 10000
            AvgMemberships = 15
        }
    }

    return $scales[$Template]
}

function New-TestUser {
    <#
    .SYNOPSIS
        Generate a realistic test user object
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$Index,

        [Parameter(Mandatory=$false)]
        [string]$Domain = "testdomain.local"
    )

    $firstNames = @("Alice", "Bob", "Charlie", "Diana", "Edward", "Fiona", "George", "Hannah", "Ian", "Julia",
                    "Kevin", "Laura", "Michael", "Nancy", "Oliver", "Patricia", "Quentin", "Rachel", "Steven", "Tina")

    $lastNames = @("Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez",
                   "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin")

    $departments = @("IT", "HR", "Sales", "Finance", "Operations", "Marketing", "Legal", "Engineering", "Support", "Admin")

    $titles = @("Manager", "Director", "Analyst", "Specialist", "Coordinator", "Administrator", "Engineer", "Developer", "Consultant", "Associate")

    $firstName = $firstNames[$Index % $firstNames.Length]
    $lastName = $lastNames[($Index / $firstNames.Length) % $lastNames.Length]
    $department = $departments[$Index % $departments.Length]
    $title = $titles[$Index % $titles.Length]

    $samAccountName = "$($firstName.ToLower()).$($lastName.ToLower())$Index"
    $email = "$samAccountName@$Domain"
    $employeeId = "EMP{0:D6}" -f $Index

    return @{
        FirstName = $firstName
        LastName = $lastName
        SamAccountName = $samAccountName
        Email = $email
        Department = $department
        Title = $title
        EmployeeId = $employeeId
        DisplayName = "$firstName $lastName"
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

# Export functions
Export-ModuleMember -Function @(
    'Assert-Condition',
    'Assert-Equal',
    'Assert-NotNull',
    'Write-TestSection',
    'Write-TestStep',
    'Wait-ForCondition',
    'Get-TemplateScale',
    'New-TestUser',
    'Get-RandomSubset'
)
