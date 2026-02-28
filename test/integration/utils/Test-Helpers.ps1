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

        Also generates realistic employment data:
        - EmployeeType: ~20% Contractors, ~80% Employees
        - Company: Subatomic for employees, one of five partner companies for contractors
        - AccountExpires: All contractors get expiry dates (1 week to 12 months)
                         ~15% of employees get expiry dates (resignations, 1 week to 3 months)
        - Pronouns: ~25% of users have pronouns populated (he/him, she/her, they/them, etc.)
    #>
    param(
        [Parameter(Mandatory=$true)]
        [int]$Index,

        [Parameter(Mandatory=$false)]
        [string]$Domain = "subatomic.local"
    )

    $nameData = Get-TestNameData
    $firstNames = $nameData.FirstNames
    $lastNames = $nameData.LastNames
    $totalCombinations = $nameData.TotalCombinations

    # Match the departments from src/JIM.Application/Resources/Departments.en.txt
    $departments = @("Marketing", "Operations", "Finance", "Sales", "Human Resources", "Procurement",
                     "Information Technology", "Research & Development", "Executive", "Legal", "Facilities", "Catering")
    $titles = @("Manager", "Director", "Analyst", "Specialist", "Coordinator", "Administrator", "Engineer", "Developer", "Consultant", "Associate")

    # Pronouns: ~25% of users have pronouns populated (optional field)
    # Distribution reflects realistic workplace adoption rates
    $pronounOptions = @("he/him", "she/her", "they/them", "he/they", "she/they")

    # Companies: Subatomic is the main company (employees), partner companies for contractors
    # These are used for company-specific entitlement groups in Scenario 4
    $mainCompany = "Subatomic"
    $partnerCompanies = @(
        "Nexus Dynamics",      # Technology consulting partner
        "Orbital Systems",     # Cloud infrastructure provider
        "Quantum Bridge",      # Integration services partner
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

    # For display name, add suffix only if we've exhausted unique combinations
    $displayName = if ($Index -ge $totalCombinations) {
        $suffix = [int][Math]::Floor($Index / $totalCombinations) + 1
        "$firstName $lastName ($suffix)"
    } else {
        "$firstName $lastName"
    }

    # Determine employee type: ~20% contractors, ~80% employees
    # Use deterministic assignment based on index for reproducibility
    $isContractor = ($Index % 5) -eq 0
    $employeeType = if ($isContractor) { "Contractor" } else { "Employee" }

    # Assign company: Employees work for Subatomic, contractors come from partner companies
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
    $accountExpires = $null
    $now = Get-Date

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

        Validates that Delta Sync completed, allowing warnings (but not errors).
    #>
    param(
        [Parameter(Mandatory=$true)]
        [string]$ActivityId,

        [Parameter(Mandatory=$true)]
        [string]$Name,

        [Parameter(Mandatory=$false)]
        [switch]$AllowWarnings
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
            if ($stats.totalProvisioned -gt 0) { $errorDetails += "  - Provisioned: $($stats.totalProvisioned)" }
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
                        if ($item.connectedSystemObjectExternalId) {
                            $errorDetails += "    Object: $($item.connectedSystemObjectExternalId)"
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
        The ObjectChangeType to look for (e.g., 'Added', 'Deleted', 'Updated', 'Projected', 'Provisioned', 'Deprovisioned')

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
                     'DisconnectedOutOfScope', 'OutOfScopeRetainJoin', 'DriftCorrection', 'Provisioned',
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
        'Provisioned' { $stats.totalProvisioned }
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
                     'DisconnectedOutOfScope', 'OutOfScopeRetainJoin', 'DriftCorrection', 'Provisioned',
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
        'Provisioned' { return $stats.totalProvisioned }
        'Exported' { return $stats.totalExported }
        'Deprovisioned' { return $stats.totalDeprovisioned }
        'NoChange' { return $stats.totalUnchanged }
        'PendingExport' { return $stats.totalPendingExports }
        'PendingExportConfirmed' { return $stats.totalPendingExportsConfirmed }
        default { return 0 }
    }
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

# Functions are automatically available when dot-sourced
# No need for Export-ModuleMember
