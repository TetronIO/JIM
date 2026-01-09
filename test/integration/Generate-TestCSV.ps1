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

# Copy to Docker volume (if running in container environment)
Write-TestStep "Step 3" "Copying files to Docker volume"

try {
    # Check if samba-ad-primary container exists and is running
    $containerRunning = docker ps --filter "name=samba-ad-primary" --filter "status=running" --format "{{.Names}}" 2>$null

    if ($containerRunning -eq "samba-ad-primary") {
        Write-Host "  Copying CSV files to container volume..." -ForegroundColor Gray

        # Copy files into the shared volume via container
        docker cp $csvPath samba-ad-primary:/connector-files/hr-users.csv
        docker cp $deptCsvPath samba-ad-primary:/connector-files/departments.csv

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
Write-Host "Template:       $Template" -ForegroundColor Cyan
Write-Host "Users:          $($users.Count)" -ForegroundColor Cyan
Write-Host "Departments:    $($departments.Count)" -ForegroundColor Cyan
Write-Host "Output path:    $OutputPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ CSV generation complete" -ForegroundColor Green
Write-Host ""
Write-Host "Files created:" -ForegroundColor Gray
Write-Host "  - $csvPath" -ForegroundColor Gray
Write-Host "  - $deptCsvPath" -ForegroundColor Gray
