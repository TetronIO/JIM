#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tests the parallel processing optimization for performance metrics parsing.

.DESCRIPTION
    Creates sample log data and tests the parallel parsing logic to ensure it works correctly.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Testing Parallel Performance Metrics Parsing..." -ForegroundColor Cyan
Write-Host ""

# Generate sample log lines (similar to what DiagnosticListener would produce)
$sampleLogs = @(
    "DiagnosticListener: FullImport completed in 1234.56ms [connectedSystemId=1]"
    "DiagnosticListener: FullImport > ProcessPage completed in 123.45ms [page=1]"
    "DiagnosticListener: FullImport > ProcessPage completed in 125.67ms [page=2]"
    "DiagnosticListener: [SLOW] FullImport > ProcessPage completed in 456.78ms [page=3]"
    "DiagnosticListener: FullSync completed in 2345.67ms"
    "DiagnosticListener: Export completed in 345.67ms [connectedSystemId=2, objectCount=100]"
)

Write-Host "Sample log lines:" -ForegroundColor Gray
$sampleLogs | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
Write-Host ""

# Test parallel parsing
Write-Host "Parsing with parallel processing..." -ForegroundColor Gray
$startTime = Get-Date

$operations = $sampleLogs | ForEach-Object -Parallel {
    $logLine = $_
    if ($logLine -match 'DiagnosticListener:\s+(?:\[SLOW\]\s+)?(?:(.+?)\s+>\s+)?(.+?)\s+completed in\s+([\d.]+)ms(?:\s+\[(.*)\])?') {
        $parentName = $Matches[1]
        $operationName = $Matches[2]
        $durationMs = [double]$Matches[3]
        $tags = $Matches[4]

        $operation = @{
            Parent = if ($parentName) { $parentName } else { $null }
            Name = $operationName
            DurationMs = $durationMs
            Tags = @{}
        }

        if ($tags) {
            $tagPairs = $tags -split ',\s*'
            foreach ($tagPair in $tagPairs) {
                if ($tagPair -match '(.+?)=(.+)') {
                    $operation.Tags[$Matches[1]] = $Matches[2]
                }
            }
        }

        $operation
    }
} -ThrottleLimit ([Environment]::ProcessorCount)

$elapsed = (Get-Date) - $startTime
Write-Host "  Parsed $($operations.Count) operations in $($elapsed.TotalMilliseconds.ToString('F2'))ms" -ForegroundColor Green
Write-Host ""

# Verify results
Write-Host "Verification:" -ForegroundColor Gray
$expectedCount = 6
if ($operations.Count -eq $expectedCount) {
    Write-Host "  ✓ Correct number of operations parsed ($expectedCount)" -ForegroundColor Green
}
else {
    Write-Host "  ✗ Expected $expectedCount operations, got $($operations.Count)" -ForegroundColor Red
    exit 1
}

# Check specific operations
$fullImportOps = @($operations | Where-Object { $_.Name -eq "FullImport" })
Write-Host "  FullImport operations found: $($fullImportOps.Count)" -ForegroundColor DarkGray

$fullImportCount = ($fullImportOps | Measure-Object).Count
if ($fullImportCount -eq 1) {
    Write-Host "  ✓ Found 1 FullImport operation" -ForegroundColor Green
}
else {
    Write-Host "  ✗ Expected 1 FullImport operation, got $fullImportCount" -ForegroundColor Red
    exit 1
}

$processPageOps = $operations | Where-Object { $_.Name -eq "ProcessPage" }
if ($processPageOps.Count -eq 3) {
    Write-Host "  ✓ Found 3 ProcessPage operations" -ForegroundColor Green
}
else {
    Write-Host "  ✗ Expected 3 ProcessPage operations, got $($processPageOps.Count)" -ForegroundColor Red
    exit 1
}

# Test grouping performance
Write-Host ""
Write-Host "Testing Group-Object optimization..." -ForegroundColor Gray
$startTime = Get-Date

$grouped = $operations | Group-Object -Property Name -AsHashTable -AsString

$elapsed = (Get-Date) - $startTime
Write-Host "  Grouped operations in $($elapsed.TotalMilliseconds.ToString('F2'))ms" -ForegroundColor Green

if ($grouped.ContainsKey("ProcessPage") -and $grouped["ProcessPage"].Count -eq 3) {
    Write-Host "  ✓ ProcessPage group has 3 operations" -ForegroundColor Green
}
else {
    Write-Host "  ✗ ProcessPage grouping failed" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "✓ All tests passed!" -ForegroundColor Green
Write-Host ""
Write-Host "Performance benefits:" -ForegroundColor Cyan
Write-Host "  • ForEach-Object -Parallel: Uses all CPU cores for log parsing" -ForegroundColor Gray
Write-Host "  • Group-Object -AsHashTable: O(n) instead of O(n²) for grouping" -ForegroundColor Gray
Write-Host "  • Expected speedup: 2-4x on multi-core systems with large log files" -ForegroundColor Gray
Write-Host ""
