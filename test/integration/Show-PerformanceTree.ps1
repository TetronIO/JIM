#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Displays hierarchical performance tree from the most recent test results.

.DESCRIPTION
    Reads the latest performance metrics JSON file and displays a hierarchical tree
    view of operations with friendly time formatting.

.PARAMETER Scenario
    The scenario name (e.g., "Scenario1-HRToIdentityDirectory"). If not specified, uses the most recent file.

.PARAMETER Template
    The template name (e.g., "Nano"). If not specified, uses the most recent file.

.PARAMETER File
    Specific JSON file to display. If specified, overrides Scenario and Template.

.EXAMPLE
    ./Show-PerformanceTree.ps1
    Shows the most recent performance metrics file.

.EXAMPLE
    ./Show-PerformanceTree.ps1 -Scenario Scenario1-HRToIdentityDirectory -Template Nano
    Shows the most recent metrics for the specified scenario and template.

.EXAMPLE
    ./Show-PerformanceTree.ps1 -File results/performance/hostname/Scenario1-HRToIdentityDirectory-Nano-2025-12-24_102142.json
    Shows the specified metrics file.
#>

param(
    [string]$Scenario,
    [string]$Template,
    [string]$File
)

# Colours for output
$RED = "`e[31m"
$GREEN = "`e[32m"
$YELLOW = "`e[33m"
$CYAN = "`e[36m"
$GRAY = "`e[90m"
$NC = "`e[0m"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

# Helper function to format milliseconds into friendly time values
function Format-FriendlyTime {
    param([double]$Ms)

    if ($Ms -lt 1000) {
        # Less than 1 second - show milliseconds
        return "$($Ms.ToString('F1'))ms"
    }
    elseif ($Ms -lt 60000) {
        # Less than 1 minute - show seconds with 1 decimal place
        $secs = $Ms / 1000
        return "$($secs.ToString('F1'))s"
    }
    elseif ($Ms -lt 3600000) {
        # Less than 1 hour - show minutes and seconds
        $totalSecs = [int]($Ms / 1000)
        $mins = [Math]::Floor($totalSecs / 60)
        $secs = $totalSecs % 60
        if ($secs -eq 0) {
            return "${mins}m"
        }
        return "${mins}m ${secs}s"
    }
    else {
        # 1 hour or more - show hours, minutes, seconds
        $totalSecs = [int]($Ms / 1000)
        $hours = [Math]::Floor($totalSecs / 3600)
        $mins = [Math]::Floor(($totalSecs % 3600) / 60)
        $secs = $totalSecs % 60
        if ($mins -eq 0 -and $secs -eq 0) {
            return "${hours}h"
        }
        elseif ($secs -eq 0) {
            return "${hours}h ${mins}m"
        }
        return "${hours}h ${mins}m ${secs}s"
    }
}

# Recursive function to display tree with ASCII art
function Show-OperationTree {
    param(
        [hashtable]$Operation,
        [string]$Prefix = "",
        [bool]$IsLast = $true,
        [bool]$IsRoot = $false
    )

    # Guard against null operation or divide by zero
    if ($null -eq $Operation -or $Operation["Count"] -eq 0) {
        return
    }

    $avgMs = $Operation["TotalMs"] / $Operation["Count"]
    $totalTime = Format-FriendlyTime -Ms $Operation["TotalMs"]
    $avgTime = Format-FriendlyTime -Ms $avgMs
    $countSuffix = if ($Operation["Count"] -gt 1) { " (${GRAY}$($Operation["Count"])x, avg $avgTime${NC})" } else { "" }

    # Tree characters for display
    $connector = if ($IsLast) { "└─ " } else { "├─ " }
    $displayPrefix = if ($IsRoot) { "" } else { $Prefix + $connector }

    Write-Host ("$displayPrefix{0,-50} {1,12}$countSuffix" -f $Operation["Name"], $totalTime)

    # Sort children by total time descending (handle null/empty)
    $children = $Operation["Children"]
    if ($null -ne $children -and $children.Count -gt 0) {
        $sortedChildren = @($children | Sort-Object -Property TotalMs -Descending)

        # Calculate prefix for children
        if ($IsRoot) {
            # Root's children get no inherited prefix (they start the tree branches)
            $childPrefix = ""
        }
        else {
            # Non-root: extend prefix with continuation line or spaces
            $extension = if ($IsLast) { "   " } else { "│  " }
            $childPrefix = $Prefix + $extension
        }

        for ($i = 0; $i -lt $sortedChildren.Count; $i++) {
            $isLastChild = ($i -eq ($sortedChildren.Count - 1))
            Show-OperationTree -Operation $sortedChildren[$i] -Prefix $childPrefix -IsLast $isLastChild -IsRoot $false
        }
    }
}

# Find the metrics file to display
$metricsFile = $null

if ($File) {
    # Use specified file
    if (Test-Path $File) {
        $metricsFile = Get-Item $File
    }
    else {
        Write-Host "${RED}Error: File not found: $File${NC}"
        exit 1
    }
}
else {
    # Find in results directory
    $hostname = [System.Net.Dns]::GetHostName()
    $perfDir = Join-Path $scriptRoot "results" "performance" $hostname

    if (-not (Test-Path $perfDir)) {
        Write-Host "${RED}Error: No performance results found at $perfDir${NC}"
        Write-Host "${YELLOW}Run an integration test first to generate metrics.${NC}"
        exit 1
    }

    # Build filter pattern
    if ($Scenario -and $Template) {
        $pattern = "$Scenario-$Template-*.json"
    }
    elseif ($Scenario) {
        $pattern = "$Scenario-*.json"
    }
    elseif ($Template) {
        $pattern = "*-$Template-*.json"
    }
    else {
        $pattern = "*.json"
    }

    # Find most recent file matching pattern
    $metricsFile = Get-ChildItem $perfDir -Filter $pattern |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $metricsFile) {
        Write-Host "${RED}Error: No metrics files found matching pattern: $pattern${NC}"
        Write-Host "${YELLOW}Available files in ${perfDir}:${NC}"
        Get-ChildItem $perfDir -Filter "*.json" | ForEach-Object { Write-Host "  - $($_.Name)" }
        exit 1
    }
}

# Load and display metrics
Write-Host ""
Write-Host "${CYAN}Performance Metrics: $($metricsFile.Name)${NC}"
Write-Host "${GRAY}File: $($metricsFile.FullName)${NC}"
Write-Host ""

$metrics = Get-Content $metricsFile.FullName | ConvertFrom-Json

Write-Host "${CYAN}Test Details:${NC}"
Write-Host "  Scenario:  $($metrics.Scenario)"
Write-Host "  Template:  $($metrics.Template)"
Write-Host "  Timestamp: $($metrics.Timestamp)"
Write-Host "  Operations: $($metrics.Operations.Count)"
Write-Host ""

if ($metrics.Operations.Count -eq 0) {
    Write-Host "${YELLOW}No operations found in metrics file.${NC}"
    exit 0
}

Write-Host "${CYAN}Performance Breakdown (Hierarchical):${NC}"
Write-Host "${GRAY}Note: Times show CUMULATIVE totals across all invocations. Child totals may exceed${NC}"
Write-Host "${GRAY}parent totals when operations are called multiple times within loops (e.g., per page).${NC}"
Write-Host ""

# Build parent-child relationships and calculate totals
# NOTE: Child times represent CUMULATIVE time across all invocations, not time within a single parent invocation.
# When a child operation is called multiple times within a loop (e.g., once per page), the sum of all child
# invocations may exceed the parent's single invocation time. This is expected behaviour.

# Filter out operations with empty or null names first
$validOperations = $metrics.Operations | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) }

# Group operations by name and calculate totals in parallel
$operationsByName = $validOperations | Group-Object -Property Name -AsHashTable -AsString

# Calculate totals for each operation group
foreach ($key in $operationsByName.Keys) {
    $ops = $operationsByName[$key]
    $operationsByName[$key] = @{
        Name = $key
        Parent = $ops[0].Parent  # All operations with same name should have same parent
        TotalMs = ($ops.DurationMs | Measure-Object -Sum).Sum
        Count = $ops.Count
        Children = @()
    }
}

# Link children to parents
foreach ($opName in $operationsByName.Keys) {
    $op = $operationsByName[$opName]
    if ($op.Parent -and $operationsByName.ContainsKey($op.Parent)) {
        $operationsByName[$op.Parent].Children += $op
    }
}

# Find and display root operations (those without parents or whose parents aren't in the data)
$roots = $operationsByName.Values | Where-Object {
    -not $_.Parent -or -not $operationsByName.ContainsKey($_.Parent)
} | Sort-Object -Property TotalMs -Descending

for ($i = 0; $i -lt $roots.Count; $i++) {
    $isLastRoot = ($i -eq ($roots.Count - 1))
    Show-OperationTree -Operation $roots[$i] -Prefix "" -IsLast $isLastRoot -IsRoot $true
}

Write-Host ""
Write-Host "${GREEN}✓ Performance tree displayed${NC}"
