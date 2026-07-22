#Requires -Version 7.0

<#
.SYNOPSIS
    JIM PowerShell Module - Administration module for Junctional Identity Manager

.DESCRIPTION
    This module provides cmdlets for administering JIM (Junctional Identity Manager) from the command line.
    It enables automation, scripting, and integration testing of JIM configurations.

    Features:
    - Connection management with API key authentication
    - Connected Systems management
    - Synchronisation Rules configuration
    - Run Profile execution
    - Metaverse object queries
    - Activity monitoring
    - Certificate management
    - And more...

.NOTES
    Author: Tetron
    Project: https://github.com/TetronIO/JIM
#>

# Module-level variables for connection state
$script:JIMConnection = $null

# Pagination safety limits for -All auto-pagination (see issue #487).
# JIMMaxAllPages caps how many pages -All will fetch before stopping (unless -Force is supplied),
# bounding a runaway sequential fetch. At the maximum page size of 100 this is ~100,000 objects,
# consistent with the API page-depth cap (PaginationRequest.MaxPage).
# JIMAllWarningThreshold is the total-object count above which -All emits an up-front warning that a
# large sequential fetch is under way, so a caller is not surprised by a long-running command.
$script:JIMMaxAllPages = 1000
$script:JIMAllWarningThreshold = 10000

# Get public and private function definition files
$Public = @(Get-ChildItem -Path "$PSScriptRoot/Public" -Recurse -Filter '*.ps1' -ErrorAction SilentlyContinue)
$Private = @(Get-ChildItem -Path "$PSScriptRoot/Private" -Recurse -Filter '*.ps1' -ErrorAction SilentlyContinue)

# Dot source the files
foreach ($import in @($Public + $Private)) {
    try {
        Write-Verbose "Importing $($import.FullName)"
        . $import.FullName
    }
    catch {
        Write-Error "Failed to import function $($import.FullName): $_"
    }
}

# Export public functions
Export-ModuleMember -Function $Public.BaseName
