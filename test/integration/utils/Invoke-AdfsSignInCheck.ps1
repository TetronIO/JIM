# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    One AD FS sign-in check (#2441), run in the LDAP toolbox container: signs a person in to JIM and reports what JIM
    says about them, as one line beginning ADFS-SIGN-IN-RESULT followed by JSON.

.DESCRIPTION
    Portal      Signs in to the portal through AD FS (Invoke-PortalSignIn) and reads /api/v1/userinfo with the portal's
                cookie, then asks an Administrator-only API (GET /api/v1/security/roles) for its status.
    ConnectJim  Runs Connect-JIM interactively, as an administrator would: PKCE through the PowerShell module's own
                public client, with the "browser" Initialize-AdfsSignInClient.ps1 wrote first on PATH. Then
                Test-JIMConnection, Get-JIMUserInfo, and Get-JIMRole (Administrator only), all with the bearer token
                Connect-JIM obtained.
    TryIt       Signs in as the API reference's Try It does (Invoke-TryItSignIn, #2594): the client, endpoints and scope
                the page and its OpenAPI document give, PKCE back to /api/reference, and the code exchanged with JIM's
                origin, reporting the Access-Control-Allow-Origin AD FS answered. Then /api/v1/userinfo and the
                Administrator-only API with that bearer token.

    The person's name and password come from JIM_SIGN_IN_USERNAME and JIM_SIGN_IN_PASSWORD (docker exec -e NAME, so
    neither is on a command line); neither is ever printed. A failure is reported in the result, not thrown, so the
    scenario reads every check the same way.

.PARAMETER Check
    Portal, ConnectJim or TryIt.

.PARAMETER JimUrl
    JIM's address as this container reaches it (https://jim.web:8443).

.PARAMETER ClientDirectory
    Where the scenario copied the sign-in client and the JIM PowerShell module (JIM/JIM.psd1).

.PARAMETER BinDirectory
    Where Initialize-AdfsSignInClient.ps1 wrote the opener.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'The result line is read from the container''s output.')]
param(
    [Parameter(Mandatory)][ValidateSet('Portal', 'ConnectJim', 'TryIt')][string]$Check,
    [Parameter(Mandatory)][string]$JimUrl,
    [Parameter(Mandatory)][string]$ClientDirectory,
    [Parameter(Mandatory)][string]$BinDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-StatusFromError {
    # The HTTP status a JIM cmdlet's error names (403, 401 ...), or the message when it names none.
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)
    $text = "$($ErrorRecord.Exception.Message) $($ErrorRecord.ErrorDetails)"
    if ($text -match '\b(401|403)\b' -or $text -match '(?i)\b(Forbidden|Unauthorized)\b') {
        if ($text -match '403' -or $text -match '(?i)Forbidden') { return 403 } else { return 401 }
    }
    return $text.Trim()
}

$result = [ordered]@{ check = $Check; userName = $env:JIM_SIGN_IN_USERNAME; ok = $false }
try {
    if (-not $env:JIM_SIGN_IN_USERNAME -or -not $env:JIM_SIGN_IN_PASSWORD) {
        throw 'JIM_SIGN_IN_USERNAME and JIM_SIGN_IN_PASSWORD must be set.'
    }
    Import-Module (Join-Path $ClientDirectory 'AdfsSignInClient.psm1') -Force

    if ($Check -eq 'Portal') {
        $session = Invoke-PortalSignIn -JimUrl $JimUrl -UserName $env:JIM_SIGN_IN_USERNAME -Password $env:JIM_SIGN_IN_PASSWORD
        $result.userInfo = Get-PortalUserInfo -JimUrl $JimUrl -Session $session
        $result.administratorApiStatus = Get-PortalAdministratorApiStatus -JimUrl $JimUrl -Session $session
    }
    elseif ($Check -eq 'TryIt') {
        $tryIt = Invoke-TryItSignIn -JimUrl $JimUrl -UserName $env:JIM_SIGN_IN_USERNAME -Password $env:JIM_SIGN_IN_PASSWORD
        # The settings, never the token.
        $result.tryIt = [ordered]@{
            clientId = $tryIt.Settings.ClientId; authorizationUrl = $tryIt.Settings.AuthorizationUrl; tokenUrl = $tryIt.Settings.TokenUrl
            apiScope = $tryIt.Settings.ApiScope; origin = $tryIt.Origin; allowedOrigin = $tryIt.AllowedOrigin
        }
        $api = Get-BearerApiResult -JimUrl $JimUrl -AccessToken $tryIt.AccessToken
        $result.userInfo = $api.UserInfo
        $result.administratorApiStatus = $api.AdministratorApiStatus
    }
    else {
        $env:PATH = "$($BinDirectory):$($env:PATH)"
        Import-Module (Join-Path $ClientDirectory 'JIM' 'JIM.psd1') -Force
        Connect-JIM -Url $JimUrl -NoPersist -TimeoutSeconds 180 | Out-Null
        $connection = Test-JIMConnection
        $result.connection = [ordered]@{ connected = [bool]$connection.Connected; authMethod = "$($connection.AuthMethod)" }
        $result.userInfo = Get-JIMUserInfo
        try {
            Get-JIMRole -ErrorAction Stop | Out-Null
            $result.administratorApiStatus = 200
        }
        catch {
            $result.administratorApiStatus = Get-StatusFromError -ErrorRecord $_
        }
        Disconnect-JIM -ErrorAction SilentlyContinue
    }
    $result.ok = $true
}
catch {
    $result.error = $_.Exception.Message
    # Where it failed, so a lab failure names the line rather than only an exception's words.
    $result.at = @("$($_.ScriptStackTrace)" -split "`r?`n" | Where-Object { $_ } | Select-Object -First 4)
    $log = Join-Path $BinDirectory 'browser.log'
    if ($Check -eq 'ConnectJim' -and (Test-Path -LiteralPath $log)) {
        $result.browserLog = @(Get-Content -LiteralPath $log -Tail 5)
    }
}

Write-Host ('ADFS-SIGN-IN-RESULT ' + ($result | ConvertTo-Json -Depth 6 -Compress))
