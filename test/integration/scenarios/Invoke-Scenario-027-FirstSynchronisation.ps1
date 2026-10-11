# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The tested tutorial: runs "Your First Synchronisation" from the docs page itself

.DESCRIPTION
    Executes docs/getting-started/first-synchronisation.md, the public tutorial, and asserts every
    checkpoint the page tells the reader to expect (#1454; TUT-1 in engineering/DOCUMENTATION_STANDARD.md).
    The scenario keeps no copy of the tutorial's steps. Tutorial-Helpers.ps1 reads them from the page:
    each PowerShell tab's code, in page order, then each <!-- checkpoint: ... --> marker, a PowerShell
    expression that must return $true, and each <!-- checkpoint-file: ... --> marker, a file in the
    connector files volume that must hold the stated number of rows. A change to the page is therefore
    a change to this test, and a product change that breaks the tutorial fails the Pre-Release suite.

    What the scenario does itself, because the page leaves it to the reader:
      - connects to JIM (the page's "Before you begin");
      - copies the tutorial's two sample files, published with the docs under docs/assets/tutorial, into
        the connector files volume (the page's Step 1, which is shell commands rather than PowerShell);
      - removes any Connected Systems left by an earlier run of this scenario, and stops with a clear message
        if that run's Metaverse Objects are still there: the tutorial is a first synchronisation, so it
        needs an environment that has not run it before (the runner's default reset gives one).

    The tutorial's code runs with strict mode off, as in a reader's console, and with
    $ErrorActionPreference = 'Stop', so an error a reader would see in red fails the step that raised it.

    Directory-agnostic: the tutorial uses the File Connector only, so the runner runs it once per pass.

.PARAMETER Step
    "Tutorial" or "All": the whole tutorial, which is one test.

.PARAMETER Template
    Accepted for runner compatibility. The tutorial's sample data is fixed at 25 people.

.PARAMETER JIMUrl
    The URL of the JIM instance (default: http://localhost:5200)

.PARAMETER ApiKey
    API key for authentication.

.PARAMETER DirectoryConfig
    Accepted for runner compatibility; the tutorial has no directory.

.EXAMPLE
    ./Invoke-Scenario-027-FirstSynchronisation.ps1 -ApiKey "jim_..."
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Tutorial", "All")]
    [string]$Step = "All",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Nano", "Micro", "Small", "Medium", "MediumLarge", "Large", "Scale100k50Groups", "Scale200k55Groups", "Scale500k65Groups", "Scale750k70Groups", "Scale1m80Groups", "Scale100k5kGroups", "Scale200k10kGroups", "Scale500k25kGroups", "Scale750k40kGroups", "Scale1m60kGroups")]
    [string]$Template = "Nano",

    [Parameter(Mandatory=$false)]
    [string]$JIMUrl = ($env:JIM_INTEGRATION_URL ?? "http://localhost:5200"),

    [Parameter(Mandatory=$false)]
    [string]$ApiKey,

    [Parameter(Mandatory=$false)]
    [int]$WaitSeconds = 0,

    [Parameter(Mandatory=$false)]
    [switch]$SkipPopulate,

    [Parameter(Mandatory=$false)]
    [hashtable]$DirectoryConfig
)

# Accepted for runner compatibility; the tutorial has fixed data and no directory.
$null = $Template
$null = $WaitSeconds
$null = $SkipPopulate
$null = $DirectoryConfig

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot/../utils/Test-Helpers.ps1"
. "$PSScriptRoot/../utils/IntegrationLane.ps1"
. "$PSScriptRoot/../utils/Tutorial-Helpers.ps1"

$repositoryRoot = (Resolve-Path "$PSScriptRoot/../../..").Path
$tutorialPage = Join-Path $repositoryRoot 'docs/getting-started/first-synchronisation.md'
$sampleFolder = Join-Path $repositoryRoot 'docs/assets/tutorial'
# The Connected Systems the tutorial creates, removed first so a re-run starts from nothing.
$tutorialSystemNames = @('HR System', 'Staff Directory')

Write-TestSection "Scenario 027: Your First Synchronisation (the tested tutorial)"
Write-Host "Page:     docs/getting-started/first-synchronisation.md" -ForegroundColor Gray
Write-Host ""

$testResults = @{
    Scenario = "Your First Synchronisation"
    Steps = @()
    Success = $false
}

function Add-StepResult {
    param([string]$Name, [bool]$Success = $true, [string]$Note = "", [string]$ErrorMsg = "")
    $entry = @{ Name = $Name; Success = $Success; Skipped = $false }
    if ($Note) { $entry.Note = $Note }
    if ($ErrorMsg) { $entry.Error = $ErrorMsg }
    $testResults.Steps += $entry
}

# Counts the data rows of a file in the connector files volume, read through a throwaway container
# that mounts the volume, as the seeding helpers write it; this works whether JIM runs in containers
# or natively against the same volume.
function Get-ConnectorVolumeDataRowCount {
    param([string]$Path)
    $volume = (Get-IntegrationLane).ConnectorFilesVolume
    $content = docker run --rm -v "${volume}:/connector-files:ro" $script:ConnectorVolumeHelperImage cat $Path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not read $Path from the connector files volume: $content" }
    $rows = @($content | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return [Math]::Max(0, $rows.Count - 1)
}

if ($Step -in @("Tutorial", "All")) {
    try {
        Write-TestSection "Setup: what the page leaves to the reader"
        if (-not $ApiKey) { throw "API key required for authentication" }

        Import-Module "$repositoryRoot/src/JIM.PowerShell/JIM.psd1" -Force -ErrorAction Stop
        Connect-JIM -Url $JIMUrl -ApiKey $ApiKey | Out-Null

        foreach ($systemName in $tutorialSystemNames) {
            foreach ($existing in @(Get-JIMConnectedSystem | Where-Object { $_.name -eq $systemName })) {
                Remove-JIMConnectedSystem -Id $existing.id -DeleteImmediately -Force | Out-Null
                Write-Host "  Removed a Connected System left by an earlier run: $systemName" -ForegroundColor Gray
            }
        }

        # The tutorial is a first synchronisation, and its checkpoints say what a first run shows (25 people
        # projected). Deleting the Connected Systems above leaves the Metaverse Objects an earlier run
        # projected, so a second run in the same environment would join them instead; say so rather than
        # fail at a checkpoint that reads as a product fault. The runner resets the environment by default.
        if (@(Get-JIMMetaverseObject -ObjectTypeName 'User' -AttributeName 'Employee ID' -AttributeValue 'HR-1001' -ErrorAction SilentlyContinue).Count -gt 0) {
            throw "This JIM already holds the tutorial's people (Employee ID HR-1001) from an earlier run. The tutorial is a first synchronisation: run it against a reset environment, which Run-IntegrationTests.ps1 gives by default."
        }

        Write-FilesToConnectorVolume -SourceDir $sampleFolder -Files @(
            @{ SourceFile = 'hr-people.csv';       DestinationPath = '/connector-files/tutorial/hr-people.csv' }
            @{ SourceFile = 'staff-directory.csv'; DestinationPath = '/connector-files/tutorial/staff-directory.csv' }
        )
        Write-Host "  OK Sample files copied to /connector-files/tutorial" -ForegroundColor Green

        $tutorialItems = @(Get-TutorialStep -Path $tutorialPage)
        $codeCount = @($tutorialItems | Where-Object Kind -eq 'Code').Count
        $checkCount = @($tutorialItems | Where-Object Kind -ne 'Code').Count
        Write-Host "  OK Read $codeCount PowerShell step(s) and $checkCount checkpoint(s) from the page" -ForegroundColor Green
        if ($codeCount -eq 0 -or $checkCount -eq 0) { throw "The tutorial page gave no PowerShell steps or no checkpoints to run." }
    }
    catch {
        Add-StepResult -Name "Setup" -Success $false -ErrorMsg $_.Exception.Message
        Write-Host "  FAIL $_" -ForegroundColor Red
        $tutorialItems = @()
    }

    # The tutorial's own code, run in this scope so each block sees the variables earlier blocks set.
    $currentStep = $null
    $stepFailed = $false
    foreach ($tutorialItem in $tutorialItems) {
        if ($tutorialItem.Step -ne $currentStep) {
            $currentStep = $tutorialItem.Step
            Write-TestSection $currentStep
        }
        $where = "$($tutorialItem.Step) (page line $($tutorialItem.Line))"

        if ($tutorialItem.Kind -eq 'Code') {
            if ($stepFailed) { continue }
            try {
                Set-StrictMode -Off
                . ([scriptblock]::Create($tutorialItem.Code)) | Out-Host
                Set-StrictMode -Version Latest
                Write-Host "  OK Ran the PowerShell at page line $($tutorialItem.Line)" -ForegroundColor Green
            }
            catch {
                Set-StrictMode -Version Latest
                # A failed step leaves nothing for the steps after it to build on, so stop running code;
                # the checkpoints still report, each failing with what it found.
                $stepFailed = $true
                Add-StepResult -Name $where -Success $false -ErrorMsg "The tutorial's PowerShell failed: $($_.Exception.Message)"
                Write-Host "  FAIL $($_.Exception.Message)" -ForegroundColor Red
            }
            continue
        }

        try {
            if ($tutorialItem.Kind -eq 'Checkpoint') {
                Set-StrictMode -Off
                $result = . ([scriptblock]::Create($tutorialItem.Expression))
                Set-StrictMode -Version Latest
                if ($result -isnot [bool] -or -not $result) {
                    throw "Not true: $($tutorialItem.Expression) (it gave '$result')"
                }
                Add-StepResult -Name "Checkpoint: $where" -Note $tutorialItem.Expression
                Write-Host "  OK Checkpoint: $($tutorialItem.Expression)" -ForegroundColor Green
            }
            else {
                $rows = Get-ConnectorVolumeDataRowCount -Path $tutorialItem.Path
                if ($rows -ne $tutorialItem.Rows) {
                    throw "$($tutorialItem.Path) holds $rows data row(s); the page says $($tutorialItem.Rows)."
                }
                Add-StepResult -Name "Checkpoint: $where" -Note "$($tutorialItem.Path) holds $rows row(s)"
                Write-Host "  OK Checkpoint: $($tutorialItem.Path) holds $rows data rows" -ForegroundColor Green
            }
        }
        catch {
            Set-StrictMode -Version Latest
            Add-StepResult -Name "Checkpoint: $where" -Success $false -ErrorMsg $_.Exception.Message
            Write-Host "  FAIL $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    $failed = @($testResults.Steps | Where-Object { -not $_.Success })
    $testResults.Success = ($testResults.Steps.Count -gt 0) -and ($failed.Count -eq 0)
}

# ─────────────────────────────────────────────────────────────────────────────
# Summary
# ─────────────────────────────────────────────────────────────────────────────
Write-TestSection "Test Results Summary"

$passed = @($testResults.Steps | Where-Object { $_.Success }).Count
$total = @($testResults.Steps).Count
$failedCount = @($testResults.Steps | Where-Object { -not $_.Success }).Count

Write-Host "Scenario: $($testResults.Scenario)" -ForegroundColor Cyan
Write-Host ""
foreach ($testStep in $testResults.Steps) {
    $icon = if ($testStep.Success) { "OK" } else { "FAIL" }
    $color = if ($testStep.Success) { "Green" } else { "Red" }
    Write-Host "  $icon $($testStep.Name)" -ForegroundColor $color
    if (-not $testStep.Success -and $testStep.ContainsKey('Error') -and $testStep.Error) {
        Write-Host "    Error: $($testStep.Error)" -ForegroundColor Red
    }
}
Write-Host ""
Write-Host "Results: $passed passed, $failedCount failed (of $total tests)" -ForegroundColor $(if ($failedCount -eq 0) { "Green" } else { "Red" })

if ($testResults.Success) {
    Write-Host ""
    Write-Host "OK The tutorial works as the page says" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "FAIL The tutorial does not work as the page says" -ForegroundColor Red
    exit 1
}
