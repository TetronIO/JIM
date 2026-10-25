# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    The browser Connect-JIM opens, in the AD FS sign-in checks (#2441): signs in on AD FS with forms and delivers the
    code to the module's loopback listener.

.DESCRIPTION
    Run by the xdg-open Initialize-AdfsSignInClient.ps1 writes, with the authorisation URL as its only argument, exactly
    as Connect-JIM runs a desktop's browser. The person's name and password come from JIM_SIGN_IN_USERNAME and
    JIM_SIGN_IN_PASSWORD, which Invoke-AdfsSignInCheck.ps1 inherited from docker exec; neither is ever printed.

.PARAMETER Url
    The AD FS authorisation URL Connect-JIM built.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'One line to the opener''s log.')]
param(
    [Parameter(Mandatory)][string]$Url
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    Import-Module (Join-Path $PSScriptRoot 'AdfsSignInClient.psm1') -Force
    Complete-BrowserSignIn -Url $Url -UserName $env:JIM_SIGN_IN_USERNAME -Password $env:JIM_SIGN_IN_PASSWORD
    Write-Host "$(Get-Date -Format o) Signed in $($env:JIM_SIGN_IN_USERNAME) and delivered the code to the loopback listener"
}
catch {
    Write-Host "$(Get-Date -Format o) Browser sign-in failed: $($_.Exception.Message)"
    exit 1
}
