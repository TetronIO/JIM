<#
.SYNOPSIS
    Configure JIM for Scenario 1 - HR to Enterprise Directory

.DESCRIPTION
    Creates Connected Systems, Sync Rules, and Run Profiles for testing
    HR CSV to Samba AD provisioning

    NOTE: This script requires:
    - API Key Authentication (#175)
    - PowerShell Module (#176)
    Both are planned but not yet implemented.

.PARAMETER ApiKey
    JIM API key for authentication

.PARAMETER BaseUrl
    JIM API base URL (default: http://localhost:5203)

.EXAMPLE
    ./Setup-Scenario1.ps1 -ApiKey $env:JIM_API_KEY
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$ApiKey = $env:JIM_API_KEY,

    [Parameter(Mandatory=$false)]
    [string]$BaseUrl = "http://localhost:5203"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "===========================================" -ForegroundColor Yellow
Write-Host " Scenario 1 Setup - NOT YET IMPLEMENTED" -ForegroundColor Yellow
Write-Host "===========================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "This script requires:" -ForegroundColor Yellow
Write-Host "  - API Key Authentication (Issue #175)" -ForegroundColor Yellow
Write-Host "  - PowerShell Module (Issue #176)" -ForegroundColor Yellow
Write-Host ""
Write-Host "Once implemented, this script will:" -ForegroundColor Gray
Write-Host "  1. Connect to JIM API using API key" -ForegroundColor Gray
Write-Host "  2. Create HR CSV Connected System (Source)" -ForegroundColor Gray
Write-Host "  3. Create Samba AD Connected System (Target)" -ForegroundColor Gray
Write-Host "  4. Create Inbound Sync Rule (HR -> Metaverse)" -ForegroundColor Gray
Write-Host "  5. Create Outbound Sync Rule (Metaverse -> AD)" -ForegroundColor Gray
Write-Host "  6. Create Run Profile for full synchronisation" -ForegroundColor Gray
Write-Host ""
Write-Host "For now, configure JIM manually via the web UI at:" -ForegroundColor Cyan
Write-Host "  $BaseUrl" -ForegroundColor Cyan
Write-Host ""

# TODO: Implement once dependencies are available
# Import-Module JIM.PowerShell
# Connect-JIM -ApiKey $ApiKey -BaseUrl $BaseUrl
# ... rest of configuration

exit 1
