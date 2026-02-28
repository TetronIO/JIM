<#
.SYNOPSIS
    Generate test CSV files for HR data

.DESCRIPTION
    Creates realistic HR CSV files based on the specified template scale
    Files are created in the shared volume for JIM File connector testing

.PARAMETER Template
    Data scale template (Nano, Micro, Small, Medium, Large, XLarge, XXLarge)

.PARAMETER OutputPath
    Path where CSV files should be created (default: ./test-data)

.EXAMPLE
    ./Generate-TestCSV.ps1 -Template Small
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "XLarge", "XXLarge")]
    [string]$Template = "Small",

    [Parameter(Mandatory=$false)]
    [string]$OutputPath = "./test-data"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Import helpers
. "$PSScriptRoot/utils/Test-Helpers.ps1"

Write-TestSection "Generating Test CSV Files with $Template template"

# Ensure output directory exists
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}

# Get scale for template
$scale = Get-TemplateScale -Template $Template

Write-Host "Users to generate: $($scale.Users)" -ForegroundColor Gray
Write-Host "Output path: $OutputPath" -ForegroundColor Gray

# Generate HR users CSV
Write-TestStep "Step 1" "Generating HR users CSV"

$csvPath = Join-Path $OutputPath "hr-users.csv"
$users = @()

for ($i = 1; $i -lt $scale.Users + 1; $i++) {
    $user = New-TestUser -Index $i -Domain "subatomic.local"

    $upn = "$($user.SamAccountName)@subatomic.local"

    # Format employeeEndDate as ISO 8601 for CSV compatibility
    # This represents the employee's contract/employment end date from HR
    # JIM will convert this to AD's accountExpires (NT time format) via ToFileTime expression
    $employeeEndDateValue = if ($null -ne $user.AccountExpires) {
        $user.AccountExpires.ToString("yyyy-MM-ddTHH:mm:ssZ")
    } else {
        ""
    }

    $users += [PSCustomObject]@{
        employeeId = $user.EmployeeId
        firstName = $user.FirstName
        lastName = $user.LastName
        email = $user.Email
        department = $user.Department
        title = $user.Title
        company = $user.Company
        pronouns = if ($null -ne $user.Pronouns) { $user.Pronouns } else { "" }
        samAccountName = $user.SamAccountName
        displayName = $user.DisplayName
        status = "Active"
        userPrincipalName = $upn
        employeeType = $user.EmployeeType
        employeeEndDate = $employeeEndDateValue
    }

    if (($i % 1000) -eq 0 -or $i -eq $scale.Users) {
        Write-Host "    Generated $i / $($scale.Users) users..." -ForegroundColor Gray
    }
}

# Export to CSV
$users | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

Write-Host "  ✓ Created $csvPath with $($users.Count) users" -ForegroundColor Green

# Generate department lookup CSV
Write-TestStep "Step 2" "Generating departments CSV"

$deptCsvPath = Join-Path $OutputPath "departments.csv"
$departments = @(
    [PSCustomObject]@{ Code = "IT"; Name = "Information Technology"; Manager = "alice.smith1" }
    [PSCustomObject]@{ Code = "HR"; Name = "Human Resources"; Manager = "bob.johnson2" }
    [PSCustomObject]@{ Code = "Sales"; Name = "Sales"; Manager = "charlie.williams3" }
    [PSCustomObject]@{ Code = "Finance"; Name = "Finance"; Manager = "diana.brown4" }
    [PSCustomObject]@{ Code = "Operations"; Name = "Operations"; Manager = "edward.jones5" }
    [PSCustomObject]@{ Code = "Marketing"; Name = "Marketing"; Manager = "fiona.garcia6" }
    [PSCustomObject]@{ Code = "Legal"; Name = "Legal"; Manager = "george.miller7" }
    [PSCustomObject]@{ Code = "Engineering"; Name = "Engineering"; Manager = "hannah.davis8" }
    [PSCustomObject]@{ Code = "Support"; Name = "Support"; Manager = "ian.rodriguez9" }
    [PSCustomObject]@{ Code = "Admin"; Name = "Administration"; Manager = "julia.martinez10" }
)

$departments | Export-Csv -Path $deptCsvPath -NoTypeInformation -Encoding UTF8

Write-Host "  ✓ Created $deptCsvPath with $($departments.Count) departments" -ForegroundColor Green

# Generate Training data CSV (covers 85% of HR users)
Write-TestStep "Step 3" "Generating training data CSV"

$trainingCsvPath = Join-Path $OutputPath "training-records.csv"
$trainingRecords = @()

# Training courses that employees can complete
$courses = @(
    @{ Code = "SEC101"; Name = "Security Awareness Basics"; Category = "Security" }
    @{ Code = "SEC201"; Name = "Advanced Security Protocols"; Category = "Security" }
    @{ Code = "COMP101"; Name = "Compliance Fundamentals"; Category = "Compliance" }
    @{ Code = "COMP201"; Name = "Data Privacy Regulations"; Category = "Compliance" }
    @{ Code = "LEAD101"; Name = "Leadership Essentials"; Category = "Leadership" }
    @{ Code = "LEAD201"; Name = "Team Management"; Category = "Leadership" }
    @{ Code = "TECH101"; Name = "IT Systems Overview"; Category = "Technical" }
    @{ Code = "TECH201"; Name = "Cloud Infrastructure"; Category = "Technical" }
    @{ Code = "SOFT101"; Name = "Communication Skills"; Category = "Soft Skills" }
    @{ Code = "SOFT201"; Name = "Conflict Resolution"; Category = "Soft Skills" }
)

# 85% of users have training records
$usersWithTraining = [int]($scale.Users * 0.85)

for ($i = 1; $i -le $usersWithTraining; $i++) {
    $user = New-TestUser -Index $i -Domain "subatomic.local"

    # Each user has 1-5 completed courses (deterministic based on index)
    $numCourses = 1 + ($i % 5)

    # Build multi-value courses completed list (pipe-separated for CSV)
    $completedCourses = @()
    for ($c = 0; $c -lt $numCourses; $c++) {
        $courseIndex = ($i + $c) % $courses.Count
        $completedCourses += $courses[$courseIndex].Code
    }

    # Training completion date (deterministic based on index)
    $daysAgo = 7 + ($i * 3) % 365  # Between 7 days and 1 year ago
    $completionDate = (Get-Date).AddDays(-$daysAgo).ToString("yyyy-MM-ddTHH:mm:ssZ")

    # Training status: Pass (90%), Fail (5%), InProgress (5%)
    $statusIndex = $i % 20
    $trainingStatus = if ($statusIndex -lt 18) { "Pass" } elseif ($statusIndex -lt 19) { "Fail" } else { "InProgress" }

    $trainingRecords += [PSCustomObject]@{
        employeeId = $user.EmployeeId          # Join key to match HR data
        samAccountName = $user.SamAccountName  # Alternative join key
        coursesCompleted = $completedCourses -join "|"  # MVA: pipe-separated list
        trainingStatus = $trainingStatus       # SVA: Pass/Fail/InProgress
        completionDate = $completionDate       # SVA: Date of last completion
        totalCoursesCompleted = $numCourses    # SVA: Count of completed courses
    }

    if (($i % 1000) -eq 0 -or $i -eq $usersWithTraining) {
        Write-Host "    Generated $i / $usersWithTraining training records..." -ForegroundColor Gray
    }
}

$trainingRecords | Export-Csv -Path $trainingCsvPath -NoTypeInformation -Encoding UTF8

Write-Host "  ✓ Created $trainingCsvPath with $($trainingRecords.Count) records (85% of users)" -ForegroundColor Green

# Generate Cross-Domain export target CSV (empty - will be populated by JIM exports)
Write-TestStep "Step 4" "Creating Cross-Domain export target CSV"

$crossDomainCsvPath = Join-Path $OutputPath "cross-domain-users.csv"

# Create empty CSV with headers for export target
$crossDomainHeaders = @(
    "samAccountName",
    "displayName",
    "email",
    "department",
    "employeeId",
    "company",
    "pronouns"
)

# Write header row only (file connector will append exports)
$crossDomainHeaders -join "," | Set-Content -Path $crossDomainCsvPath -Encoding UTF8

Write-Host "  ✓ Created $crossDomainCsvPath (empty export target)" -ForegroundColor Green

# Copy to Docker volume (if running in container environment)
Write-TestStep "Step 5" "Copying files to Docker volume"

try {
    # Check if samba-ad-primary container exists and is running
    $containerRunning = docker ps --filter "name=samba-ad-primary" --filter "status=running" --format "{{.Names}}" 2>$null

    if ($containerRunning -eq "samba-ad-primary") {
        Write-Host "  Copying CSV files to container volume..." -ForegroundColor Gray

        # Copy files into the shared volume via container
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv
        docker cp $deptCsvPath samba-ad-primary:/connector-files/departments.csv
        docker cp $trainingCsvPath samba-ad-primary:/connector-files/training-records.csv
        docker cp $crossDomainCsvPath samba-ad-primary:/connector-files/cross-domain-users.csv

        Write-Host "  ✓ Files copied to /connector-files in container" -ForegroundColor Green
    }
    else {
        Write-Host "  ⚠ Container not running, files only in local directory" -ForegroundColor Yellow
        Write-Host "    Start containers and re-run to copy to volume" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "  ⚠ Could not copy to container: $_" -ForegroundColor Yellow
}

# Summary
Write-TestSection "CSV Generation Summary"
Write-Host "Template:            $Template" -ForegroundColor Cyan
Write-Host "Users:               $($users.Count)" -ForegroundColor Cyan
Write-Host "Training Records:    $($trainingRecords.Count) (85%)" -ForegroundColor Cyan
Write-Host "Departments:         $($departments.Count)" -ForegroundColor Cyan
Write-Host "Output path:         $OutputPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ CSV generation complete" -ForegroundColor Green
Write-Host ""
Write-Host "Files created:" -ForegroundColor Gray
Write-Host "  - $csvPath" -ForegroundColor Gray
Write-Host "  - $trainingCsvPath" -ForegroundColor Gray
Write-Host "  - $deptCsvPath" -ForegroundColor Gray
Write-Host "  - $crossDomainCsvPath (empty export target)" -ForegroundColor Gray
