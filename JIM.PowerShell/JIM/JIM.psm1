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
    - Sync Rules configuration
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
