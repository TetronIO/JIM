# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Registering JIM with Microsoft Entra ID (#2367), for Register-JIMIdentityProvider -Provider EntraId.
#
# Entra ID is configured through Microsoft Graph with Invoke-RestMethod, so nothing beyond PowerShell is needed. As for
# AD FS and Keycloak, what the tenant holds is read once, the plan is worked out here (a pure function, tested without
# Graph), and each planned change is then applied on its own, so -WhatIf and -Confirm see each change separately.
#
# One application serves the portal (Web platform, a client secret), the PowerShell module (Mobile and desktop, the
# loopback redirect URIs) and the API reference's Try It (single-page application, which Entra ID requires for a
# browser to redeem its code), and is JIM's API too (api://{client ID}, the access_as_user scope). It asks for version 2
# access tokens, as Microsoft recommends, whose audience is the client ID; JIM requires access_as_user in their scp.

$script:JIMEntraIdGraph = 'https://graph.microsoft.com/v1.0'
$script:JIMEntraIdGraphAppId = '00000003-0000-0000-c000-000000000000'
$script:JIMEntraIdApiScope = 'access_as_user'

# Microsoft Graph's delegated permissions JIM signs people in with, by their fixed ids. offline_access gives the
# PowerShell module a refresh token.
$script:JIMEntraIdGraphScopes = [ordered]@{
    openid         = '37f7f235-527c-4136-accd-4a02d197296e'
    profile        = '14dad69e-099b-42c9-810b-d002981feec1'
    email          = '64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0'
    offline_access = '7427e0e9-2fba-42fe-b0c0-848c9e6a8182'
}

# Microsoft's own public client for signing in to Graph from a command line (the Microsoft Graph Command Line Tools
# application), used to sign an administrator in when no token is given, so nothing has to be registered first. It
# accepts a browser sign-in redirected to http://localhost on any port, and a device code.
$script:JIMEntraIdSignInClientId = '14d82eec-204b-4c2f-b7e8-296a70dab67e'
$script:JIMEntraIdSignInScopes = @(
    'https://graph.microsoft.com/Application.ReadWrite.All'
    'https://graph.microsoft.com/DelegatedPermissionGrant.ReadWrite.All'
    'https://graph.microsoft.com/User.Read'
)

$script:JIMEntraIdSecretName = 'JIM portal'

function Get-JIMEntraIdAdminToken {
    <#
    .SYNOPSIS
        Signs a person who administers the tenant's applications in to Microsoft Graph, and returns the access token as a
        secure string.

    .DESCRIPTION
        Through the browser by default: authorisation code with PKCE, redirected to this computer's loopback address,
        so Conditional Access, multifactor authentication and device compliance apply as they do to any sign-in.
        Microsoft recommends blocking the device code flow, so it is used only when asked for, for a console with no
        browser in a tenant that still allows it.
    #>
    [CmdletBinding()]
    [OutputType([securestring])]
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [switch]$UseDeviceCode,
        # The Microsoft Graph delegated permissions to ask for; by default those JIM's own sign-in registration needs.
        [string[]]$Scopes = $script:JIMEntraIdSignInScopes
    )

    if ($UseDeviceCode) {
        return Get-JIMEntraIdDeviceCodeToken -TenantId $TenantId -Scopes $Scopes
    }

    $base = "https://login.microsoftonline.com/$([uri]::EscapeDataString($TenantId))/oauth2/v2.0"
    $tokens = Invoke-OAuthBrowserFlow -AuthorizeEndpoint "$base/authorize" -TokenEndpoint "$base/token" `
        -ClientId $script:JIMEntraIdSignInClientId -Scopes $Scopes -RedirectPath '/'
    return ConvertTo-SecureString -String $tokens.AccessToken -AsPlainText -Force
}

function Get-JIMEntraIdDeviceCodeToken {
    <#
    .SYNOPSIS
        Signs in to Microsoft Graph with a device code, as a person who administers the tenant's applications, and
        returns the access token as a secure string.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'The sign-in prompt is for the person at the console, and must reach them however output is redirected.')]
    [CmdletBinding()]
    [OutputType([securestring])]
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [string[]]$Scopes = $script:JIMEntraIdSignInScopes
    )

    $base = "https://login.microsoftonline.com/$([uri]::EscapeDataString($TenantId))/oauth2/v2.0"
    $code = Invoke-RestMethod -Method Post -Uri "$base/devicecode" -ErrorAction Stop -Body @{
        client_id = $script:JIMEntraIdSignInClientId; scope = $Scopes -join ' '
    }
    Write-Host $code.message -ForegroundColor Yellow

    $deadline = [datetime]::UtcNow.AddSeconds([int]$code.expires_in)
    $interval = [Math]::Max([int]$code.interval, 1)
    while ([datetime]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds $interval
        try {
            $token = Invoke-RestMethod -Method Post -Uri "$base/token" -ErrorAction Stop -Body @{
                grant_type = 'urn:ietf:params:oauth:grant-type:device_code'
                client_id = $script:JIMEntraIdSignInClientId; device_code = $code.device_code
            }
            return ConvertTo-SecureString -String $token.access_token -AsPlainText -Force
        }
        catch [Microsoft.PowerShell.Commands.HttpResponseException] {
            $reason = ($_.ErrorDetails.Message | ConvertFrom-Json -ErrorAction SilentlyContinue).error
            if ($reason -eq 'slow_down') { $interval += 5; continue }
            if ($reason -ne 'authorization_pending') {
                # Conditional Access commonly blocks the device code flow, as Microsoft recommends, so say how else.
                throw ("Entra ID did not sign you in to Microsoft Graph with a device code: $($_.ErrorDetails.Message) " +
                    'Where Conditional Access blocks device code sign-in, run this again without -UseDeviceCode to sign in through a browser, or pass -AccessToken with a Microsoft Graph token.')
            }
        }
    }
    throw 'The device code expired before the sign-in was completed. Run this again.'
}

function Get-JIMEntraIdTokenTenantId {
    # The tenant a Graph access token was issued in: its tid claim. Read without validating the token, which Graph does.
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][securestring]$AccessToken)

    $parts = [System.Net.NetworkCredential]::new('', $AccessToken).Password.Split('.')
    if ($parts.Count -lt 2) { throw 'The access token is not a JSON Web Token.' }
    $payload = $parts[1].Replace('-', '+').Replace('_', '/')
    $payload += '=' * ((4 - $payload.Length % 4) % 4)
    $claims = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
    if (-not $claims.tid) { throw 'The access token names no tenant (tid): it is not a Microsoft Graph token.' }
    return [string]$claims.tid
}

function Invoke-JIMEntraIdGraphApi {
    <#
    .SYNOPSIS
        Calls Microsoft Graph with the connection's token.

    .PARAMETER Path
        The path under https://graph.microsoft.com/v1.0, such as /applications.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Connection,
        [ValidateSet('Get', 'Post', 'Patch', 'Delete')][string]$Method = 'Get',
        [Parameter(Mandatory)][string]$Path,
        [object]$Body
    )

    $request = @{
        Method      = $Method
        Uri         = "$script:JIMEntraIdGraph$Path"
        Headers     = @{ Authorization = "Bearer $([System.Net.NetworkCredential]::new('', $Connection.Token).Password)" }
        ErrorAction = 'Stop'
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 10 -Compress
        $request.ContentType = 'application/json'
    }
    try {
        Invoke-RestMethod @request
    }
    catch {
        # Graph's error body can be empty, so the exception's own message, which carries the status, is always given.
        $body = "$($_.ErrorDetails.Message)".Trim()
        $reason = if ($body) { "$($_.Exception.Message) $body" } else { $_.Exception.Message }
        throw "Microsoft Graph refused $($Method.ToUpperInvariant()) $Path`: $reason"
    }
}

function Get-JIMEntraIdRegistrationState {
    <#
    .SYNOPSIS
        Reads what the tenant holds that JIM's registration depends on: the applications with JIM's name, the service
        principal of the one there is, the consent granted to it, and who is signed in.

    .DESCRIPTION
        Consent is reported unknown, rather than failing the run, when the caller may not read it: an application
        holding only Application.ReadWrite.OwnedBy, as JIM's checks do, maintains the registration but cannot grant
        consent, which an administrator gives once.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Connection,
        [Parameter(Mandatory)][string]$Name
    )

    $select = 'id,appId,displayName,signInAudience,identifierUris,web,spa,publicClient,api,requiredResourceAccess,optionalClaims,passwordCredentials'
    $filter = [uri]::EscapeDataString("displayName eq '$($Name.Replace("'", "''"))'")
    $applications = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/applications?`$filter=$filter&`$select=$select").value | ForEach-Object {
        $graph = @($_.requiredResourceAccess | Where-Object resourceAppId -eq $script:JIMEntraIdGraphAppId)
        [PSCustomObject]@{
            Id                          = [string]$_.id
            AppId                       = [string]$_.appId
            DisplayName                 = [string]$_.displayName
            SignInAudience              = [string]$_.signInAudience
            IdentifierUris              = @($_.identifierUris)
            WebRedirectUris             = @($_.web.redirectUris)
            SpaRedirectUris             = @($_.spa.redirectUris)
            PublicClientRedirectUris    = @($_.publicClient.redirectUris)
            RequestedAccessTokenVersion = $_.api.requestedAccessTokenVersion
            ApiScopes                   = @($_.api.oauth2PermissionScopes | ForEach-Object {
                [PSCustomObject]@{ Id = [string]$_.id; Value = [string]$_.value; IsEnabled = [bool]$_.isEnabled; Type = [string]$_.type; Raw = $_ } })
            GraphDelegatedScopeIds      = @($graph.resourceAccess | Where-Object type -eq 'Scope' | ForEach-Object { [string]$_.id })
            OtherResourceAccess         = @($_.requiredResourceAccess | Where-Object resourceAppId -ne $script:JIMEntraIdGraphAppId)
            OptionalClaims              = [PSCustomObject]@{
                IdToken = @($_.optionalClaims.idToken.name); AccessToken = @($_.optionalClaims.accessToken.name); Raw = $_.optionalClaims }
            PasswordCredentials         = @($_.passwordCredentials | ForEach-Object { [PSCustomObject]@{ KeyId = [string]$_.keyId; DisplayName = [string]$_.displayName } })
        }
    })

    $servicePrincipal = $null
    $consent = [PSCustomObject]@{ Known = $true; Graph = @(); Api = @(); GraphGrantId = $null; ApiGrantId = $null }
    if ($applications.Count -eq 1) {
        $sp = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/servicePrincipals?`$filter=appId eq '$($applications[0].AppId)'&`$select=id").value) | Select-Object -First 1
        if ($sp) {
            $servicePrincipal = [PSCustomObject]@{ Id = [string]$sp.id }
            try {
                $grants = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/oauth2PermissionGrants?`$filter=clientId eq '$($sp.id)'").value |
                    Where-Object consentType -eq 'AllPrincipals')
                $own = $grants | Where-Object resourceId -eq $sp.id | Select-Object -First 1
                $graphGrant = $grants | Where-Object resourceId -ne $sp.id | Select-Object -First 1
                $consent.Api = @(if ($own) { "$($own.scope)".Split(' ', [StringSplitOptions]::RemoveEmptyEntries) })
                $consent.ApiGrantId = if ($own) { [string]$own.id }
                $consent.Graph = @(if ($graphGrant) { "$($graphGrant.scope)".Split(' ', [StringSplitOptions]::RemoveEmptyEntries) })
                $consent.GraphGrantId = if ($graphGrant) { [string]$graphGrant.id }
            }
            catch {
                Write-Verbose "Consent could not be read: $($_.Exception.Message)"
                $consent.Known = $false
            }
        }
    }
    if ($consent.Known) {
        # Whether the caller may grant consent: reading grants is the cheapest test, and needs the same permission.
        try { $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Path '/oauth2PermissionGrants?$top=1' }
        catch { $consent.Known = $false }
    }

    $signedIn = $null
    try { $signedIn = [string](Invoke-JIMEntraIdGraphApi -Connection $Connection -Path '/me?$select=id').id }
    catch { Write-Verbose 'The token is not a person''s, so nobody is signed in.' }

    [PSCustomObject]@{
        TenantId         = $Connection.TenantId
        Applications     = $applications
        ServicePrincipal = $servicePrincipal
        Consent          = $consent
        SignedInObjectId = $signedIn
    }
}

function New-JIMEntraIdChange {
    param([string]$Action, [string]$Target, [string]$Description, [hashtable]$Arguments = @{})

    [PSCustomObject]@{ Action = $Action; Target = $Target; Description = $Description; Arguments = $Arguments }
}

function Get-JIMEntraIdRegistrationPlan {
    <#
    .SYNOPSIS
        Works out what has to change in the tenant for JIM's registration to be as it should be.

    .PARAMETER Url
        JIM's addresses, as people reach it.

    .PARAMETER Name
        The application's display name, by which it is found.

    .PARAMETER Current
        What the tenant holds, as Get-JIMEntraIdRegistrationState reads it.

    .PARAMETER ResetClientSecret
        Plan a new client secret even if the application has one, retiring the ones it has.

    .OUTPUTS
        The client ID and API scope (unknown until the application exists), whether consent could be seen, and the
        changes in the order they must be made.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Url,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][object]$Current,
        [switch]$ResetClientSecret
    )

    $applications = @($Current.Applications)
    if ($applications.Count -gt 1) {
        throw "The tenant holds more than one application called '$Name' ($(($applications.AppId) -join ', ')). Rename or remove the ones that are not JIM's, or give -Name the one that is, and run this again."
    }
    $app = $applications | Select-Object -First 1

    $baseUrls = @($Url | ForEach-Object { $_.TrimEnd('/') })
    $webRedirects = @($baseUrls | ForEach-Object { "$_/signin-oidc"; "$_/signout-callback-oidc" })
    $spaRedirects = @($baseUrls | ForEach-Object { "$_/api/reference/" })
    $loopbackRedirects = @(Get-JIMLoopbackRedirectUri)
    $graphScopeIds = @($script:JIMEntraIdGraphScopes.Values)
    $changes = [System.Collections.Generic.List[object]]::new()

    $newScope = {
        @{
            id = [guid]::NewGuid().ToString(); value = $script:JIMEntraIdApiScope; type = 'User'; isEnabled = $true
            adminConsentDisplayName = 'Use JIM as the signed-in person'
            adminConsentDescription = 'Lets the application call JIM''s API as the signed-in person, with that person''s roles in JIM.'
            userConsentDisplayName = 'Use JIM as you'
            userConsentDescription = 'Lets the application call JIM''s API as you, with your roles in JIM.'
        }
    }
    $graphAccess = @{
        resourceAppId = $script:JIMEntraIdGraphAppId
        resourceAccess = @($graphScopeIds | ForEach-Object { @{ id = $_; type = 'Scope' } })
    }
    $optionalEmail = @{ name = 'email'; essential = $false }

    if (-not $app) {
        $changes.Add((New-JIMEntraIdChange -Action 'NewApplication' -Target $Name `
            -Description 'Create the application: the portal, the PowerShell module, the API reference''s Try It and JIM''s API, with version 2 access tokens' `
            -Arguments @{ Application = @{
                displayName = $Name; signInAudience = 'AzureADMyOrg'
                web = @{ redirectUris = $webRedirects; implicitGrantSettings = @{ enableIdTokenIssuance = $false; enableAccessTokenIssuance = $false } }
                spa = @{ redirectUris = $spaRedirects }
                publicClient = @{ redirectUris = $loopbackRedirects }
                api = @{ requestedAccessTokenVersion = 2; oauth2PermissionScopes = @(& $newScope) }
                requiredResourceAccess = @($graphAccess)
                optionalClaims = @{ idToken = @($optionalEmail); accessToken = @($optionalEmail) }
            } }))
        $changes.Add((New-JIMEntraIdChange -Action 'NewServicePrincipal' -Target $Name -Description 'Create its service principal, so people can sign in to it'))
        $changes.Add((New-JIMEntraIdChange -Action 'NewClientSecret' -Target $Name -Description 'Make the portal''s client secret'))
    }
    else {
        $drift = [ordered]@{}
        $described = [System.Collections.Generic.List[string]]::new()
        if ($app.SignInAudience -ne 'AzureADMyOrg') {
            $drift.signInAudience = 'AzureADMyOrg'; $described.Add('accept accounts in this tenant only')
        }
        $identifierUri = "api://$($app.AppId)"
        if (-not (Test-JIMSameSet $app.IdentifierUris @($identifierUri))) {
            $drift.identifierUris = @($identifierUri); $described.Add("set the API's identifier to $identifierUri")
        }
        if (-not (Test-JIMSameSet $app.WebRedirectUris $webRedirects)) {
            $drift.web = @{ redirectUris = $webRedirects }; $described.Add("set the portal's redirect URIs to $($webRedirects -join ', ')")
        }
        if (-not (Test-JIMSameSet $app.SpaRedirectUris $spaRedirects)) {
            $drift.spa = @{ redirectUris = $spaRedirects }; $described.Add("set Try It's redirect URIs to $($spaRedirects -join ', ')")
        }
        if (-not (Test-JIMSameSet $app.PublicClientRedirectUris $loopbackRedirects)) {
            $drift.publicClient = @{ redirectUris = $loopbackRedirects }; $described.Add('set the PowerShell module''s redirect URIs to the loopback ports 8400 to 8409')
        }

        $scopes = @($app.ApiScopes)
        $jimScope = $scopes | Where-Object Value -ceq $script:JIMEntraIdApiScope | Select-Object -First 1
        $api = @{}
        if ($app.RequestedAccessTokenVersion -ne 2) {
            $api.requestedAccessTokenVersion = 2; $described.Add('issue version 2 access tokens')
        }
        if (-not $jimScope -or -not $jimScope.IsEnabled -or $jimScope.Type -ne 'User') {
            # Graph replaces the whole list, so the application's other scopes go back as they are.
            $others = @($scopes | Where-Object { $_.Value -cne $script:JIMEntraIdApiScope } | ForEach-Object { if ($_.PSObject.Properties['Raw'] -and $_.Raw) { $_.Raw } else { @{ id = $_.Id; value = $_.Value; type = $_.Type; isEnabled = $_.IsEnabled } } })
            $ours = & $newScope
            if ($jimScope) { $ours.id = $jimScope.Id }
            $api.oauth2PermissionScopes = @($others) + @($ours)
            $described.Add("offer the $($script:JIMEntraIdApiScope) scope")
        }
        if ($api.Count -gt 0) { $drift.api = $api }

        $missingGraph = @($graphScopeIds | Where-Object { $_ -notin @($app.GraphDelegatedScopeIds) })
        if ($missingGraph.Count -gt 0) {
            $kept = @($app.GraphDelegatedScopeIds | Where-Object { $_ -notin $graphScopeIds } | ForEach-Object { @{ id = $_; type = 'Scope' } })
            $merged = $graphAccess.Clone()
            $merged.resourceAccess = @($graphAccess.resourceAccess) + $kept
            $other = @(if ($app.PSObject.Properties['OtherResourceAccess']) { $app.OtherResourceAccess })
            $drift.requiredResourceAccess = @($merged) + $other
            $described.Add('ask Microsoft Graph for openid, profile, email and offline_access')
        }

        if ('email' -notin @($app.OptionalClaims.IdToken) -or 'email' -notin @($app.OptionalClaims.AccessToken)) {
            $drift.optionalClaims = @{ idToken = @($optionalEmail); accessToken = @($optionalEmail) }
            $described.Add('put the person''s email address in their tokens')
        }

        if ($drift.Count -gt 0) {
            $text = $described -join ', '
            $changes.Add((New-JIMEntraIdChange -Action 'SetApplication' -Target $Name `
                -Description ($text.Substring(0, 1).ToUpperInvariant() + $text.Substring(1)) -Arguments @{ Properties = [hashtable]$drift }))
        }
        if (-not $Current.ServicePrincipal) {
            $changes.Add((New-JIMEntraIdChange -Action 'NewServicePrincipal' -Target $Name -Description 'Create its service principal, so people can sign in to it'))
        }
        $secrets = @($app.PasswordCredentials)
        if ($secrets.Count -eq 0) {
            $changes.Add((New-JIMEntraIdChange -Action 'NewClientSecret' -Target $Name -Description 'Make the portal''s client secret'))
        }
        elseif ($ResetClientSecret) {
            $changes.Add((New-JIMEntraIdChange -Action 'ResetClientSecret' -Target $Name -Description 'Replace the portal''s client secret' `
                -Arguments @{ RetireKeyIds = @($secrets.KeyId) }))
        }
    }

    # Tenant-wide consent, so nobody signing in is asked for it; only an administrator may give it.
    $consentUnknown = -not $Current.Consent.Known
    if (-not $consentUnknown) {
        $consent = $Current.Consent
        $graphNames = @($script:JIMEntraIdGraphScopes.Keys)
        if (@($graphNames | Where-Object { $_ -notin @($consent.Graph) }).Count -gt 0) {
            $changes.Add((New-JIMEntraIdChange -Action 'GrantConsent' -Target $Name `
                -Description "Consent for everyone to $($graphNames -join ', ') from Microsoft Graph" `
                -Arguments @{ Resource = 'Graph'; Scopes = $graphNames; GrantId = $(if ($consent.PSObject.Properties['GraphGrantId']) { $consent.GraphGrantId }) }))
        }
        if ($script:JIMEntraIdApiScope -notin @($consent.Api)) {
            $changes.Add((New-JIMEntraIdChange -Action 'GrantConsent' -Target $Name `
                -Description "Consent for everyone to $($script:JIMEntraIdApiScope) on JIM's API" `
                -Arguments @{ Resource = 'Api'; Scopes = @($script:JIMEntraIdApiScope); GrantId = $(if ($consent.PSObject.Properties['ApiGrantId']) { $consent.ApiGrantId }) }))
        }
    }

    $clientId = if ($app) { $app.AppId }
    [PSCustomObject]@{
        ClientId       = $clientId
        ApiScope       = if ($clientId) { "api://$clientId/$($script:JIMEntraIdApiScope)" }
        ConsentUnknown = $consentUnknown
        Changes        = $changes.ToArray()
    }
}

function Invoke-JIMEntraIdRegistrationChange {
    <#
    .SYNOPSIS
        Makes one planned change through Microsoft Graph. Returns the client secret when the change made one.

    .DESCRIPTION
        The connection carries the application's object id, client ID and service principal id from one change to the
        next, since a new application's are only known once it exists.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Connection,
        [Parameter(Mandatory)][object]$Change
    )

    $arguments = $Change.Arguments
    switch ($Change.Action) {
        'NewApplication' {
            $app = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path '/applications' -Body $arguments.Application
            $Connection.ApplicationId = [string]$app.id
            $Connection.AppId = [string]$app.appId
            # The identifier names the client ID, which only exists once the application does.
            $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Patch -Path "/applications/$($app.id)" -Body @{ identifierUris = @("api://$($app.appId)") }
        }
        'SetApplication' {
            $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Patch -Path "/applications/$($Connection.ApplicationId)" -Body $arguments.Properties
        }
        'NewServicePrincipal' {
            $sp = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path '/servicePrincipals' -Body @{ appId = $Connection.AppId }
            $Connection.ServicePrincipalId = [string]$sp.id
        }
        { $_ -in 'NewClientSecret', 'ResetClientSecret' } {
            $made = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path "/applications/$($Connection.ApplicationId)/addPassword" `
                -Body @{ passwordCredential = @{ displayName = $script:JIMEntraIdSecretName } }
            # Graph can refuse a change to the application's credentials until the one just made has replicated, so each
            # replaced secret is retired with a few tries, a little longer apart each time.
            foreach ($keyId in @($arguments.RetireKeyIds | Where-Object { $_ })) {
                for ($attempt = 1; ; $attempt++) {
                    try {
                        $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path "/applications/$($Connection.ApplicationId)/removePassword" -Body @{ keyId = $keyId }
                        break
                    }
                    catch {
                        if ($attempt -ge 5) {
                            throw "The new client secret was made, but the one it replaces ($keyId) could not be retired: $($_.Exception.Message)"
                        }
                        Start-Sleep -Seconds (5 * $attempt)
                    }
                }
            }
            return [string]$made.secretText
        }
        'GrantConsent' {
            $resourceId = if ($arguments.Resource -eq 'Api') { $Connection.ServicePrincipalId }
                else { [string](@((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/servicePrincipals?`$filter=appId eq '$script:JIMEntraIdGraphAppId'&`$select=id").value) | Select-Object -First 1).id }
            $scope = $arguments.Scopes -join ' '
            if ($arguments.GrantId) {
                $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Patch -Path "/oauth2PermissionGrants/$($arguments.GrantId)" -Body @{ scope = $scope }
            }
            else {
                $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path '/oauth2PermissionGrants' -Body @{
                    clientId = $Connection.ServicePrincipalId; consentType = 'AllPrincipals'; resourceId = $resourceId; scope = $scope }
            }
        }
        default { throw "Unknown change: $($Change.Action)" }
    }
}
