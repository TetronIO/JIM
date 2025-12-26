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
        [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
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
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$Index,

        [Parameter(Mandatory=$false)]
        [string]$Domain = "testdomain.local"
    )

    $nameData = Get-TestNameData
    $firstNames = $nameData.FirstNames
    $lastNames = $nameData.LastNames
    $totalCombinations = $nameData.TotalCombinations

    # Match the departments from JIM.Application/Resources/Departments.en.txt
    $departments = @("Marketing", "Operations", "Finance", "Sales", "Human Resources", "Procurement",
                     "Information Technology", "Research & Development", "Executive", "Legal", "Facilities", "Catering")
    $titles = @("Manager", "Director", "Analyst", "Specialist", "Coordinator", "Administrator", "Engineer", "Developer", "Consultant", "Associate")

    # Calculate unique first/last name combination based on index
    # Use modulo to cycle through all combinations
    $combinationIndex = $Index % $totalCombinations
    $firstNameIndex = $combinationIndex % $firstNames.Count
    $lastNameIndex = [int][Math]::Floor($combinationIndex / $firstNames.Count) % $lastNames.Count

    $firstName = $firstNames[$firstNameIndex]
    $lastName = $lastNames[$lastNameIndex]
    $department = $departments[$Index % $departments.Length]
    $title = $titles[$Index % $titles.Length]

    # Always include index in samAccountName for guaranteed uniqueness
    $samAccountName = "$($firstName.ToLower()).$($lastName.ToLower())$Index"
    $email = "$samAccountName@$Domain"
    $employeeId = "EMP{0:D6}" -f $Index

    # For display name, add suffix only if we've exhausted unique combinations
    $displayName = if ($Index -ge $totalCombinations) {
        $suffix = [int][Math]::Floor($Index / $totalCombinations) + 1
        "$firstName $lastName ($suffix)"
    } else {
        "$firstName $lastName"
    }

    return @{
        FirstName = $firstName
        LastName = $lastName
        SamAccountName = $samAccountName
        Email = $email
        Department = $department
        Title = $title
        EmployeeId = $employeeId
        DisplayName = $displayName
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

# Functions are automatically available when dot-sourced
# No need for Export-ModuleMember
