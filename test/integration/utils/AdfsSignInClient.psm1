# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    A browserless sign-in to JIM through AD FS, for the Active Directory lab's AD FS sign-in checks (#2441).

.DESCRIPTION
    It does over plain HTTP what a browser does: follows JIM's redirect to AD FS, fills in AD FS's forms sign-in page
    (the lab's farm signs in with forms everywhere: a headless client outside the domain cannot do Windows Integrated
    Authentication), posts the form_post reply back to JIM's portal, and keeps the cookies. It also stands in for the
    browser Connect-JIM opens (Complete-BrowserSignIn), finishing the AD FS sign-in at the module's loopback callback.

    It runs in the LDAP toolbox container (test/integration/docker/ldap-toolbox), the lab's stand-in for an
    administrator's workstation: that container resolves jim.web and the AD FS server's name and trusts their
    certificates (Initialize-AdfsSignInClient.ps1). The functions that read a page are pure and tested on any platform
    (AdfsSignInClient.Tests.ps1); the ones that make requests run only there.

    No function writes a password anywhere: not to the output, an error message or a file.
#>

Set-StrictMode -Version Latest

function ConvertFrom-HtmlForm {
    <#
    .SYNOPSIS
        The forms in an HTML page: Id, Method (upper case, POST when absent), Action (HTML-decoded, as written) and
        Fields, an ordered map of every named input's name to its value. Submit buttons are left out.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Html
    )

    $attribute = '(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*(?:=\s*(?:"(?<value>[^"]*)"|''(?<value>[^'']*)''|(?<value>[^\s"''>]+)))?'
    function Read-Attributes([string]$Tag) {
        $map = [ordered]@{}
        foreach ($match in [regex]::Matches($Tag, $attribute)) {
            $name = $match.Groups['name'].Value.ToLowerInvariant()
            if (-not $map.Contains($name)) { $map[$name] = [System.Net.WebUtility]::HtmlDecode($match.Groups['value'].Value) }
        }
        return $map
    }

    foreach ($form in [regex]::Matches($Html, '(?is)<form\b(?<attributes>[^>]*)>(?<body>.*?)</form>')) {
        $formAttributes = Read-Attributes $form.Groups['attributes'].Value
        $fields = [ordered]@{}
        foreach ($field in [regex]::Matches($form.Groups['body'].Value, '(?is)<input\b(?<attributes>[^>]*?)/?>')) {
            $inputAttributes = Read-Attributes $field.Groups['attributes'].Value
            if (-not $inputAttributes.Contains('name') -or -not $inputAttributes['name']) { continue }
            if ("$($inputAttributes['type'])" -ieq 'submit') { continue }
            $value = if ($inputAttributes.Contains('value')) { $inputAttributes['value'] } else { '' }
            $fields[$inputAttributes['name']] = $value
        }
        [pscustomobject]@{
            Id     = if ($formAttributes.Contains('id')) { $formAttributes['id'] } else { $null }
            Method = if ($formAttributes.Contains('method') -and $formAttributes['method']) { $formAttributes['method'].ToUpperInvariant() } else { 'POST' }
            Action = if ($formAttributes.Contains('action')) { $formAttributes['action'] } else { '' }
            Fields = $fields
        }
    }
}

function Find-AdfsSignInForm {
    <#
    .SYNOPSIS
        AD FS's forms sign-in form on a page: the form that asks for UserName and Password. $null when there is none.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Html
    )

    return (@(ConvertFrom-HtmlForm -Html $Html) | Where-Object { $_.Fields.Contains('UserName') -and $_.Fields.Contains('Password') } | Select-Object -First 1)
}

function Get-AdfsSignInError {
    <#
    .SYNOPSIS
        The reason AD FS's sign-in page gives for refusing a sign-in (its errorText), or $null.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Html
    )

    $match = [regex]::Match($Html, '(?is)<[^>]*\bid\s*=\s*["'']?errorText["'']?[^>]*>(?<text>.*?)</')
    if (-not $match.Success) { return $null }
    $text = [System.Net.WebUtility]::HtmlDecode(($match.Groups['text'].Value -replace '<[^>]+>', '')).Trim()
    if ($text) { return $text } else { return $null }
}

function New-AdfsSignInFields {
    <#
    .SYNOPSIS
        The fields to post to AD FS's forms sign-in: the page's own, with the user name and password filled in and
        AuthMethod FormsAuthentication.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'A lab account''s password, posted in a form body as a browser posts it.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds a map; changes nothing.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Form,
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][string]$Password
    )

    $fields = [ordered]@{}
    foreach ($key in $Form.Fields.Keys) { $fields[$key] = $Form.Fields[$key] }
    $fields['UserName'] = $UserName
    $fields['Password'] = $Password
    $fields['AuthMethod'] = 'FormsAuthentication'
    return $fields
}

function Resolve-SignInUri {
    <#
    .SYNOPSIS
        A form action or redirect location, resolved against the page it came from.
    #>
    [CmdletBinding()]
    [OutputType([uri])]
    param(
        [Parameter(Mandatory)][string]$Base,
        [Parameter(Mandatory)][string]$Reference
    )

    return [uri]::new([uri]$Base, $Reference)
}

function Test-LoopbackCallback {
    <#
    .SYNOPSIS
        Whether a URI is the PowerShell module's loopback callback (http://localhost:8400 to 8409/callback/), where the
        sign-in Connect-JIM starts ends.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][string]$Uri
    )

    $parsed = $null
    if (-not [uri]::TryCreate($Uri, [UriKind]::Absolute, [ref]$parsed)) { return $false }
    return ($parsed.Scheme -eq 'http' -and $parsed.Host -eq 'localhost' -and $parsed.Port -ge 8400 -and $parsed.Port -le 8409 -and
        $parsed.AbsolutePath -eq '/callback/')
}

function Invoke-SignInRequest {
    <#
    .SYNOPSIS
        One request in a sign-in session, never following a redirect. Returns Uri, StatusCode, Location (resolved, or
        $null) and Content.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [Parameter(Mandatory)][uri]$Uri,
        [ValidateSet('GET', 'POST')][string]$Method = 'GET',
        [System.Collections.IDictionary]$Body,
        [hashtable]$Headers = @{}
    )

    # With -MaximumRedirection 0 a redirect comes back as the response and also as a "maximum redirection count has
    # been exceeded" error, whose exception is a bare InvalidOperationException; -ErrorAction Stop would make every
    # redirect throw. So the error is captured instead: a response is used whatever its status, and a request that got
    # none (no connection, a refused certificate) throws what went wrong. -AllowInsecureRedirect, because AD FS's last
    # redirect for Connect-JIM goes from HTTPS to the module's http://localhost loopback callback, and without it
    # PowerShell refuses even to return that redirect (again a bare InvalidOperationException). Nothing is followed
    # automatically: Invoke-SignInRedirects decides each hop.
    $parameters = @{
        Uri = $Uri; Method = $Method; WebSession = $Session; Headers = $Headers
        MaximumRedirection = 0; SkipHttpErrorCheck = $true; AllowInsecureRedirect = $true
        ErrorAction = 'SilentlyContinue'; ErrorVariable = 'requestErrors'
    }
    if ($Body) { $parameters.Body = $Body; $parameters.ContentType = 'application/x-www-form-urlencoded' }
    $requestErrors = $null
    $response = Invoke-WebRequest @parameters
    if ($null -eq $response) {
        $reason = if ($requestErrors) { @($requestErrors)[0].Exception.Message } else { 'no response' }
        throw "$Method $($Uri.GetLeftPart([UriPartial]::Path)) failed: $reason"
    }

    $location = $null
    if ($response.Headers.ContainsKey('Location')) {
        $location = Resolve-SignInUri -Base $Uri.AbsoluteUri -Reference ([string]@($response.Headers['Location'])[0])
    }
    return [pscustomobject]@{ Uri = $Uri; StatusCode = [int]$response.StatusCode; Location = $location; Content = [string]$response.Content }
}

function Invoke-SignInRedirects {
    <#
    .SYNOPSIS
        Follows redirects with GET from a response, up to 20, and returns the first response that is not one. Stops
        without following a redirect to the module's loopback callback (Test-LoopbackCallback), returning that one.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [Parameter(Mandatory)][object]$Response
    )

    $current = $Response
    for ($hop = 0; $hop -lt 20; $hop++) {
        if ($current.StatusCode -lt 300 -or $current.StatusCode -ge 400 -or -not $current.Location) { return $current }
        if (Test-LoopbackCallback -Uri $current.Location.AbsoluteUri) { return $current }
        $current = Invoke-SignInRequest -Session $Session -Uri $current.Location
    }
    throw "More than 20 redirects from $($Response.Uri)."
}

function Complete-AdfsSignIn {
    <#
    .SYNOPSIS
        Signs in on AD FS's forms sign-in page, and returns where AD FS sends the browser next: a page with the
        form_post reply for a confidential client, or a redirect to the loopback callback for the PowerShell module.
        Throws, with AD FS's own reason, when it refuses the sign-in.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'A lab account''s password, posted in a form body as a browser posts it.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [Parameter(Mandatory)][object]$Response,
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][string]$Password
    )

    $form = Find-AdfsSignInForm -Html $Response.Content
    if (-not $form) {
        throw "AD FS did not show its forms sign-in page: $($Response.StatusCode) at $($Response.Uri.GetLeftPart([UriPartial]::Path))."
    }
    $posted = Invoke-SignInRequest -Session $Session -Uri (Resolve-SignInUri -Base $Response.Uri.AbsoluteUri -Reference $form.Action) -Method POST `
        -Body (New-AdfsSignInFields -Form $form -UserName $UserName -Password $Password)
    $result = Invoke-SignInRedirects -Session $Session -Response $posted

    if (Find-AdfsSignInForm -Html $result.Content) {
        $reason = Get-AdfsSignInError -Html $result.Content
        throw "AD FS refused the sign-in for $UserName$(if ($reason) { ": $reason" } else { '.' })"
    }
    return $result
}

function Invoke-PortalSignIn {
    <#
    .SYNOPSIS
        Signs a person in to JIM's portal through AD FS, as a browser does, and returns the session holding the
        portal's cookie: GET the portal, follow JIM's challenge to AD FS, sign in with forms, post the form_post reply to
        /signin-oidc, and follow JIM back to the portal.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'A lab account''s password, posted in a form body as a browser posts it.')]
    [CmdletBinding()]
    [OutputType([Microsoft.PowerShell.Commands.WebRequestSession])]
    param(
        [Parameter(Mandatory)][string]$JimUrl,
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][string]$Password
    )

    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $atAdfs = Invoke-SignInRedirects -Session $session -Response (Invoke-SignInRequest -Session $session -Uri "$JimUrl/")
    $reply = Complete-AdfsSignIn -Session $session -Response $atAdfs -UserName $UserName -Password $Password

    $formPost = @(ConvertFrom-HtmlForm -Html $reply.Content) | Where-Object { $_.Action -like '*/signin-oidc*' } | Select-Object -First 1
    if (-not $formPost) {
        throw "After signing in, AD FS did not send the form_post reply to JIM's /signin-oidc: $($reply.StatusCode) at $($reply.Uri.GetLeftPart([UriPartial]::Path))."
    }
    $callback = Invoke-SignInRequest -Session $session -Uri (Resolve-SignInUri -Base $reply.Uri.AbsoluteUri -Reference $formPost.Action) -Method POST -Body $formPost.Fields
    $landed = Invoke-SignInRedirects -Session $session -Response $callback
    if ($landed.StatusCode -ne 200 -or $landed.Uri.AbsolutePath -like '/sign-in-failed*') {
        throw "JIM did not complete the sign-in: $($landed.StatusCode) at $($landed.Uri.GetLeftPart([UriPartial]::Path))."
    }
    return $session
}

function Get-PortalUserInfo {
    <#
    .SYNOPSIS
        GET /api/v1/userinfo with the portal's cookie: who JIM says the signed-in person is.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$JimUrl,
        [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$Session
    )

    $response = Invoke-SignInRequest -Session $Session -Uri "$JimUrl/api/v1/userinfo" -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' }
    if ($response.StatusCode -ne 200) { throw "GET /api/v1/userinfo answered $($response.StatusCode)." }
    return ($response.Content | ConvertFrom-Json)
}

function Get-PortalAdministratorApiStatus {
    <#
    .SYNOPSIS
        The status an Administrator-only API (GET /api/v1/security/roles) answers the portal's cookie with: 200 for an
        administrator, 403 for anyone else. XMLHttpRequest asks for the status rather than the cookie handler's redirect
        to an access-denied page.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][string]$JimUrl,
        [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$Session
    )

    return (Invoke-SignInRequest -Session $Session -Uri "$JimUrl/api/v1/security/roles" -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' }).StatusCode
}

function Complete-BrowserSignIn {
    <#
    .SYNOPSIS
        What the browser Connect-JIM opens does: from the AD FS authorisation URL, sign in with forms and follow AD FS to
        the module's loopback callback, delivering the code to the listener Connect-JIM is waiting on.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'A lab account''s password, posted in a form body as a browser posts it.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][string]$Password
    )

    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $atAdfs = Invoke-SignInRedirects -Session $session -Response (Invoke-SignInRequest -Session $session -Uri $Url)
    # Already signed in to AD FS (its own session cookie), it would redirect straight to the callback.
    $result = if (Find-AdfsSignInForm -Html $atAdfs.Content) {
        Complete-AdfsSignIn -Session $session -Response $atAdfs -UserName $UserName -Password $Password
    } else { $atAdfs }

    if (-not $result.Location -or -not (Test-LoopbackCallback -Uri $result.Location.AbsoluteUri)) {
        throw "AD FS did not send the browser to the module's loopback callback: $($result.StatusCode) at $($result.Uri.GetLeftPart([UriPartial]::Path))."
    }
    $delivered = Invoke-SignInRequest -Session $session -Uri $result.Location
    if ($delivered.StatusCode -ne 200) { throw "The module's loopback listener answered $($delivered.StatusCode)." }
}

function Get-TryItSignInSetting {
    <#
    .SYNOPSIS
        What the API reference's Try It signs in with (#2594), read as the browser reads it: the authorisation and token
        endpoints and the API scope from the OpenAPI document JIM serves, and the client ID from the API reference page's
        Scalar configuration.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Microsoft.PowerShell.Commands.WebRequestSession]$Session,
        [Parameter(Mandatory)][string]$JimUrl
    )

    $document = Invoke-SignInRequest -Session $Session -Uri "$JimUrl/api/openapi/v1.json"
    if ($document.StatusCode -ne 200) { throw "GET /api/openapi/v1.json answered $($document.StatusCode)." }
    $flow = ($document.Content | ConvertFrom-Json).components.securitySchemes.OAuth2.flows.authorizationCode
    $apiScopes = @($flow.scopes.PSObject.Properties.Name | Where-Object { $_ -ne 'openid' })
    if ($apiScopes.Count -ne 1) { throw "The OpenAPI document's OAuth2 flow offers $($apiScopes.Count) API scopes, not one: $($apiScopes -join ', ')." }

    $page = Invoke-SignInRequest -Session $Session -Uri "$JimUrl/api/reference"
    if ($page.StatusCode -ne 200) { throw "GET /api/reference answered $($page.StatusCode)." }
    # Scalar writes its configuration into the page; the flow's client ID is x-scalar-client-id there (clientId in
    # older versions).
    $clientId = [regex]::Match($page.Content, '"(?:x-scalar-client-id|clientId)"\s*:\s*"([^"]+)"').Groups[1].Value
    if (-not $clientId) { throw 'The API reference page names no OAuth client ID.' }

    return [pscustomobject]@{
        AuthorizationUrl = [string]$flow.authorizationUrl; TokenUrl = [string]$flow.tokenUrl; ApiScope = [string]$apiScopes[0]; ClientId = $clientId
    }
}

function Invoke-TryItSignIn {
    <#
    .SYNOPSIS
        Signs a person in as the API reference's Try It does (#2594), and returns its bearer token with what the sign-in
        showed: the settings it used, and the Access-Control-Allow-Origin AD FS answered the token request with.

    .DESCRIPTION
        From the settings Get-TryItSignInSetting reads: an authorisation code request with PKCE and no secret, redirected
        back to JIM's /api/reference; AD FS's forms sign-in; then the code exchanged at the token endpoint with JIM's
        origin in Origin, as the page's script sends it. A browser keeps the token only when AD FS answers with that
        origin in Access-Control-Allow-Origin, so it is returned for the caller to check rather than assumed.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'A lab account''s password, posted in a form body as a browser posts it.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$JimUrl,
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][string]$Password
    )

    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $settings = Get-TryItSignInSetting -Session $session -JimUrl $JimUrl
    $redirectUri = "$JimUrl/api/reference"
    $origin = ([uri]$JimUrl).GetLeftPart([UriPartial]::Authority)

    $verifier = -join ((1..64) | ForEach-Object { [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~' | Get-Random })
    $challenge = [Convert]::ToBase64String([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::ASCII.GetBytes($verifier))).
        TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $query = [ordered]@{
        client_id = $settings.ClientId; response_type = 'code'; redirect_uri = $redirectUri; scope = "openid $($settings.ApiScope)"
        state = 'try-it'; code_challenge = $challenge; code_challenge_method = 'S256'
    }
    $queryString = ($query.GetEnumerator() | ForEach-Object { "$($_.Key)=$([uri]::EscapeDataString($_.Value))" }) -join '&'

    $atAdfs = Invoke-SignInRedirects -Session $session -Response (Invoke-SignInRequest -Session $session -Uri "$($settings.AuthorizationUrl)?$queryString")
    $returned = Complete-AdfsSignIn -Session $session -Response $atAdfs -UserName $UserName -Password $Password
    if ($returned.Uri.GetLeftPart([UriPartial]::Path) -ne $redirectUri) {
        throw "AD FS did not return the browser to $($redirectUri): $($returned.StatusCode) at $($returned.Uri.GetLeftPart([UriPartial]::Path))."
    }
    $code = [regex]::Match($returned.Uri.Query, '[?&]code=([^&]+)').Groups[1].Value
    if (-not $code) {
        $reason = [regex]::Match($returned.Uri.Query, '[?&]error_description=([^&]+)').Groups[1].Value
        throw "AD FS returned to the API reference without a code$(if ($reason) { ": $([uri]::UnescapeDataString($reason))" } else { '.' })"
    }

    $token = Invoke-WebRequest -Method POST -Uri $settings.TokenUrl -Headers @{ Origin = $origin } -SkipHttpErrorCheck -Body @{
        grant_type = 'authorization_code'; code = [uri]::UnescapeDataString($code); redirect_uri = $redirectUri
        client_id = $settings.ClientId; code_verifier = $verifier
    }
    if ([int]$token.StatusCode -ne 200) { throw "AD FS refused Try It's token request: $([int]$token.StatusCode) $($token.Content)" }
    $allowedOrigin = if ($token.Headers.ContainsKey('Access-Control-Allow-Origin')) { [string]@($token.Headers['Access-Control-Allow-Origin'])[0] } else { $null }

    return [pscustomobject]@{
        Settings = $settings; Origin = $origin; AllowedOrigin = $allowedOrigin
        AccessToken = [string]($token.Content | ConvertFrom-Json).access_token
    }
}

function Get-BearerApiResult {
    <#
    .SYNOPSIS
        Who JIM says a bearer token's holder is (GET /api/v1/userinfo), and the status an Administrator-only API
        (GET /api/v1/security/roles) answers it with.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$JimUrl,
        [Parameter(Mandatory)][string]$AccessToken
    )

    $headers = @{ Authorization = "Bearer $AccessToken" }
    $userInfo = Invoke-WebRequest -Uri "$JimUrl/api/v1/userinfo" -Headers $headers -SkipHttpErrorCheck
    if ([int]$userInfo.StatusCode -ne 200) { throw "GET /api/v1/userinfo answered $([int]$userInfo.StatusCode) to Try It's token." }
    $roles = Invoke-WebRequest -Uri "$JimUrl/api/v1/security/roles" -Headers $headers -SkipHttpErrorCheck
    return [pscustomobject]@{ UserInfo = ($userInfo.Content | ConvertFrom-Json); AdministratorApiStatus = [int]$roles.StatusCode }
}

Export-ModuleMember -Function @(
    'ConvertFrom-HtmlForm', 'Find-AdfsSignInForm', 'Get-AdfsSignInError', 'New-AdfsSignInFields', 'Resolve-SignInUri',
    'Test-LoopbackCallback', 'Invoke-SignInRequest', 'Invoke-SignInRedirects', 'Complete-AdfsSignIn', 'Invoke-PortalSignIn',
    'Get-PortalUserInfo', 'Get-PortalAdministratorApiStatus', 'Complete-BrowserSignIn', 'Get-TryItSignInSetting',
    'Invoke-TryItSignIn', 'Get-BearerApiResult'
)
