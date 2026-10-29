# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    A browserless portal sign-in to JIM through the bundled Keycloak, for the scenarios that need a person to sign in
    (Scenario 033, #2615).

.DESCRIPTION
    Dot-source this file. It does over plain HTTP what a browser does for the OpenID Connect authorisation code flow
    JIM's portal uses: follows JIM's challenge to Keycloak, fills in Keycloak's login form, and carries Keycloak's reply
    back to JIM's /signin-oidc with the cookies a browser would send. Scenario 031 does the same through AD FS from the
    LDAP toolbox container (AdfsSignInClient.psm1); this runs on the machine that runs the scenario, because the
    bundled Keycloak is published on it.

    Two things a real browser does for free are done here, because .NET's own cookie store does neither:

      - Keycloak marks its login cookies Secure, and a browser still sends them to http://localhost, where .NET's cookie
        store would not. The cookies are kept and sent by these helpers instead, scoped by host and path as a browser
        scopes them (port, and the Secure and SameSite attributes, are ignored, as a browser does on localhost).
      - Keycloak's address as JIM advertises it (KC_HOSTNAME, http://localhost:8181) is only reachable where the runner's
        socat bridge runs. Where it does not, the sign-in reaches Keycloak at the port the stack publishes it on
        (8180) through an origin map, which rewrites each address the redirects name.

    No function writes a password anywhere: not to the output, an error message or a file.
#>

Set-StrictMode -Version Latest

# The form parser is the AD FS client's, which is pure and tested on any platform.
Import-Module (Join-Path $PSScriptRoot 'AdfsSignInClient.psm1') -Function ConvertFrom-HtmlForm -Force -DisableNameChecking

function New-SignInBrowser {
    <#
    .SYNOPSIS
        A browser for a sign-in: a client that follows no redirect by itself and keeps no cookies by itself, an empty
        cookie jar, and the map of origins to rewrite.

    .PARAMETER OriginMap
        Origins to rewrite before a request is made, advertised address to reachable address, such as
        @{ 'http://localhost:8181' = 'http://localhost:8180' }.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory object; changes nothing.')]
    [CmdletBinding()]
    param(
        [System.Collections.IDictionary]$OriginMap = @{}
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    # The sign-in is to localhost; the sandbox's egress proxy has no business in it.
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(60)

    # A copy, so that the sign-in learning an origin leaves the caller's map as it was.
    $origins = @{}
    foreach ($advertised in $OriginMap.Keys) { $origins[$advertised] = $OriginMap[$advertised] }

    return [pscustomobject]@{
        Client    = $client
        Cookies   = [System.Collections.Generic.List[object]]::new()
        OriginMap = $origins
        Counter   = [ref]0
    }
}

function Close-SignInBrowser {
    <#
    .SYNOPSIS
        Disposes a browser's client.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Browser
    )

    $Browser.Client.Dispose()
}

function ConvertFrom-SetCookie {
    <#
    .SYNOPSIS
        Reads one Set-Cookie header: its Name, Value (exactly as sent, quotes included), Path, and whether it deletes
        the cookie (an empty value, Max-Age of zero or less, or an Expires in the past). $null when it is not a cookie.

    .PARAMETER RequestUri
        The request the header answered, whose path is the cookie's when it names none (the directory of the request
        path, RFC 6265 section 5.1.4).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SetCookie,
        [Parameter(Mandatory)][uri]$RequestUri
    )

    $parts = $SetCookie -split ';'
    $pair = $parts[0]
    $equals = $pair.IndexOf('=')
    if ($equals -lt 1) { return $null }

    $name = $pair.Substring(0, $equals).Trim()
    $value = $pair.Substring($equals + 1).Trim()
    $path = $null
    $maxAge = $null
    $expires = $null

    foreach ($attribute in $parts | Select-Object -Skip 1) {
        $text = $attribute.Trim()
        $separator = $text.IndexOf('=')
        $key = if ($separator -ge 0) { $text.Substring(0, $separator).Trim() } else { $text }
        $attributeValue = if ($separator -ge 0) { $text.Substring($separator + 1).Trim() } else { '' }
        switch ($key.ToLowerInvariant()) {
            'path' { if ($attributeValue.StartsWith('/')) { $path = $attributeValue } }
            'max-age' {
                $seconds = 0L
                if ([long]::TryParse($attributeValue, [ref]$seconds)) { $maxAge = $seconds }
            }
            'expires' {
                $when = [DateTimeOffset]::MinValue
                if ([DateTimeOffset]::TryParse($attributeValue, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref]$when)) { $expires = $when }
            }
        }
    }

    if (-not $path) {
        $requestPath = $RequestUri.AbsolutePath
        $lastSlash = $requestPath.LastIndexOf('/')
        $path = if (-not $requestPath.StartsWith('/') -or $lastSlash -le 0) { '/' } else { $requestPath.Substring(0, $lastSlash) }
    }

    # Max-Age outranks Expires when both are given.
    $delete = ($value -eq '') -or
        ($null -ne $maxAge -and $maxAge -le 0) -or
        ($null -eq $maxAge -and $null -ne $expires -and $expires -lt [DateTimeOffset]::UtcNow)

    return [pscustomobject]@{ Name = $name; Value = $value; Path = $path; Delete = $delete }
}

function Set-BrowserCookie {
    <#
    .SYNOPSIS
        Applies the Set-Cookie headers of a response to a browser's cookie jar: a cookie of the same host, name and path
        is replaced (keeping its place in the order), and one the server deletes is dropped.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Updates an in-memory jar.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Browser,
        [Parameter(Mandatory)][uri]$Uri,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$SetCookie
    )

    foreach ($header in $SetCookie) {
        $cookie = ConvertFrom-SetCookie -SetCookie $header -RequestUri $Uri
        if (-not $cookie) { continue }

        $existing = $Browser.Cookies | Where-Object { $_.Host -eq $Uri.Host -and $_.Name -ceq $cookie.Name -and $_.Path -ceq $cookie.Path } | Select-Object -First 1
        if ($cookie.Delete) {
            if ($existing) { $null = $Browser.Cookies.Remove($existing) }
        }
        elseif ($existing) {
            $existing.Value = $cookie.Value
        }
        else {
            $Browser.Counter.Value++
            $Browser.Cookies.Add([pscustomobject]@{
                    Host = $Uri.Host; Name = $cookie.Name; Value = $cookie.Value; Path = $cookie.Path; Order = $Browser.Counter.Value
                })
        }
    }
}

function Get-BrowserCookieHeader {
    <#
    .SYNOPSIS
        The Cookie header a browser would send to a URI: the cookies of its host whose path the request path falls
        under, longest path first and then oldest first. $null when there are none.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][object]$Browser,
        [Parameter(Mandatory)][uri]$Uri
    )

    $requestPath = $Uri.AbsolutePath
    $matching = @($Browser.Cookies | Where-Object {
            $_.Host -eq $Uri.Host -and
            ($requestPath -ceq $_.Path -or
            ($requestPath.StartsWith($_.Path, [StringComparison]::Ordinal) -and ($_.Path.EndsWith('/') -or $requestPath[$_.Path.Length] -eq '/')))
        } | Sort-Object @{ Expression = { $_.Path.Length }; Descending = $true }, Order)
    if ($matching.Count -eq 0) { return $null }
    return (($matching | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '; ')
}

function Convert-ToReachableUri {
    <#
    .SYNOPSIS
        A URI with its origin replaced by the address it is reachable at, when the map has one for it; otherwise the
        URI as given. The path and query are kept.
    #>
    [CmdletBinding()]
    [OutputType([uri])]
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.IDictionary]$OriginMap
    )

    $origin = $Uri.GetLeftPart([UriPartial]::Authority)
    foreach ($advertised in $OriginMap.Keys) {
        if ([string]::Equals(([uri]"$advertised").GetLeftPart([UriPartial]::Authority), $origin, [StringComparison]::OrdinalIgnoreCase)) {
            return [uri]::new(([uri]"$($OriginMap[$advertised])").GetLeftPart([UriPartial]::Authority) + $Uri.PathAndQuery)
        }
    }
    return $Uri
}

function Invoke-BrowserRequest {
    <#
    .SYNOPSIS
        One request in a sign-in, never following a redirect: the browser's cookies for the address sent, the response's
        cookies kept. Returns Uri (as requested, after the origin map), StatusCode, Location (absolute, or $null) and
        Content.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Browser,
        [Parameter(Mandatory)][uri]$Uri,
        [ValidateSet('GET', 'POST')][string]$Method = 'GET',
        [System.Collections.IDictionary]$Form,
        [hashtable]$Headers = @{}
    )

    $target = Convert-ToReachableUri -Uri $Uri -OriginMap $Browser.OriginMap
    $message = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $target)
    try {
        $cookieHeader = Get-BrowserCookieHeader -Browser $Browser -Uri $target
        if ($cookieHeader) { $null = $message.Headers.TryAddWithoutValidation('Cookie', $cookieHeader) }
        foreach ($name in $Headers.Keys) { $null = $message.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) }
        if ($Form) {
            $fields = [System.Collections.Generic.Dictionary[string, string]]::new()
            foreach ($key in $Form.Keys) { $fields[[string]$key] = [string]$Form[$key] }
            $message.Content = [System.Net.Http.FormUrlEncodedContent]::new($fields)
        }

        try {
            $response = $Browser.Client.SendAsync($message).GetAwaiter().GetResult()
        }
        catch [System.Net.Http.HttpRequestException] {
            # Names where it was going, never the query: it can carry a one-time code.
            throw "$Method $($target.GetLeftPart([UriPartial]::Path)) failed: $($_.Exception.Message)"
        }

        try {
            $setCookies = $null
            if ($response.Headers.TryGetValues('Set-Cookie', [ref]$setCookies)) {
                Set-BrowserCookie -Browser $Browser -Uri $target -SetCookie @($setCookies)
            }
            $location = if ($response.Headers.Location) { [uri]::new($target, $response.Headers.Location) } else { $null }
            return [pscustomobject]@{
                Uri = $target; StatusCode = [int]$response.StatusCode; Location = $location
                Content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $message.Dispose()
    }
}

function Invoke-BrowserRedirects {
    <#
    .SYNOPSIS
        Follows redirects with GET from a response, up to 20, and returns the first response that is not one.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Browser,
        [Parameter(Mandatory)][object]$Response
    )

    $current = $Response
    for ($hop = 0; $hop -lt 20; $hop++) {
        if ($current.StatusCode -lt 300 -or $current.StatusCode -ge 400 -or -not $current.Location) { return $current }
        $current = Invoke-BrowserRequest -Browser $Browser -Uri $current.Location
    }
    throw "More than 20 redirects from $($Response.Uri.GetLeftPart([UriPartial]::Path))."
}

function Find-KeycloakLoginForm {
    <#
    .SYNOPSIS
        Keycloak's login form on a page: the form that asks for a username and a password. $null when there is none.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Html
    )

    return (@(ConvertFrom-HtmlForm -Html $Html) | Where-Object { $_.Fields.Contains('username') -and $_.Fields.Contains('password') } | Select-Object -First 1)
}

function Get-KeycloakSignInError {
    <#
    .SYNOPSIS
        The reason Keycloak's login page gives for refusing a sign-in, or $null.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Html
    )

    $match = [regex]::Match($Html, '(?is)class="[^"]*\bkc-feedback-text\b[^"]*"[^>]*>(?<text>.*?)</span>')
    if (-not $match.Success) { $match = [regex]::Match($Html, '(?is)\bid="input-error"[^>]*>(?<text>.*?)</span>') }
    if (-not $match.Success) { return $null }
    $text = ([System.Net.WebUtility]::HtmlDecode(($match.Groups['text'].Value -replace '<[^>]+>', '')) -replace '\s+', ' ').Trim()
    if ($text) { return $text } else { return $null }
}

function Invoke-KeycloakPortalSignIn {
    <#
    .SYNOPSIS
        Signs a person in to JIM's portal through Keycloak, as a browser does, and returns the browser holding the
        portal's cookie: GET the portal, follow JIM's challenge to Keycloak, sign in on its login form, submit
        Keycloak's form_post reply to JIM's /signin-oidc, and follow JIM on to the portal.

    .PARAMETER JimUrl
        JIM's address, which must be one the Keycloak client allows as a redirect (http://localhost:5200 in the
        bundled realm).

    .PARAMETER UserName
        The person's Keycloak user name.

    .PARAMETER Password
        Their password. It is posted to Keycloak's login form and nowhere else.

    .PARAMETER KeycloakUrl
        Where Keycloak is reachable from here, such as http://localhost:8180, the port the stack publishes it on. The
        address JIM's challenge names (what Keycloak is configured to advertise, http://localhost:8181) is rewritten to
        it, so the sign-in works whether or not the runner's bridge from 8181 is running.

    .PARAMETER OriginMap
        Advertised address to reachable address for any further origin, as New-SignInBrowser takes it.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The bundled development realm''s password, posted in a form body as a browser posts it.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$JimUrl,
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][string]$Password,
        [string]$KeycloakUrl,
        [System.Collections.IDictionary]$OriginMap = @{}
    )

    $browser = New-SignInBrowser -OriginMap $OriginMap
    try {
        $challenge = Invoke-BrowserRequest -Browser $browser -Uri "$($JimUrl.TrimEnd('/'))/"
        if ($KeycloakUrl -and $challenge.Location) {
            $advertised = $challenge.Location.GetLeftPart([UriPartial]::Authority)
            if ($advertised -ne ([uri]$JimUrl).GetLeftPart([UriPartial]::Authority)) { $browser.OriginMap[$advertised] = $KeycloakUrl.TrimEnd('/') }
        }
        $atKeycloak = Invoke-BrowserRedirects -Browser $browser -Response $challenge
        $form = Find-KeycloakLoginForm -Html $atKeycloak.Content
        if (-not $form) {
            throw "Keycloak did not show its login form: $($atKeycloak.StatusCode) at $($atKeycloak.Uri.GetLeftPart([UriPartial]::Path))."
        }

        $fields = [ordered]@{}
        foreach ($key in $form.Fields.Keys) { $fields[$key] = $form.Fields[$key] }
        $fields['username'] = $UserName
        $fields['password'] = $Password

        $posted = Invoke-BrowserRequest -Browser $browser -Uri ([uri]::new($atKeycloak.Uri, $form.Action)) -Method POST -Form $fields
        $landed = Invoke-BrowserRedirects -Browser $browser -Response $posted

        # JIM's portal asks for response_mode=form_post, so Keycloak answers with a page whose form a browser submits
        # to JIM's /signin-oidc on load; this submits it. A reply as a redirect (response_mode=query) needs no more.
        $reply = @(ConvertFrom-HtmlForm -Html $landed.Content) | Where-Object { $_.Action -like '*/signin-oidc*' } | Select-Object -First 1
        if ($reply) {
            $callback = Invoke-BrowserRequest -Browser $browser -Uri ([uri]::new($landed.Uri, $reply.Action)) -Method POST -Form $reply.Fields
            $landed = Invoke-BrowserRedirects -Browser $browser -Response $callback
        }

        if (Find-KeycloakLoginForm -Html $landed.Content) {
            $reason = Get-KeycloakSignInError -Html $landed.Content
            throw "Keycloak refused the sign-in for ${UserName}$(if ($reason) { ": $reason" } else { '.' })"
        }
        if ($landed.StatusCode -ne 200 -or $landed.Uri.AbsolutePath -like '/sign-in-failed*') {
            throw "JIM did not complete the sign-in: $($landed.StatusCode) at $($landed.Uri.GetLeftPart([UriPartial]::Path))."
        }
        return $browser
    }
    catch {
        Close-SignInBrowser -Browser $browser
        throw
    }
}

function Get-KeycloakPortalUserInfo {
    <#
    .SYNOPSIS
        GET /api/v1/userinfo with the portal's cookie: who JIM says the signed-in person is.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Browser,
        [Parameter(Mandatory)][string]$JimUrl
    )

    # XMLHttpRequest asks for the status rather than the cookie handler's redirect to a sign-in page.
    $response = Invoke-BrowserRequest -Browser $Browser -Uri "$($JimUrl.TrimEnd('/'))/api/v1/userinfo" -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' }
    if ($response.StatusCode -ne 200) { throw "GET /api/v1/userinfo answered $($response.StatusCode)." }
    return ($response.Content | ConvertFrom-Json)
}

function Get-KeycloakPortalAdministratorApiStatus {
    <#
    .SYNOPSIS
        The status an Administrator-only API (GET /api/v1/security/roles) answers the portal's cookie with: 200 for an
        administrator, 403 for anyone else.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][object]$Browser,
        [Parameter(Mandatory)][string]$JimUrl
    )

    return (Invoke-BrowserRequest -Browser $Browser -Uri "$($JimUrl.TrimEnd('/'))/api/v1/security/roles" -Headers @{ 'X-Requested-With' = 'XMLHttpRequest' }).StatusCode
}
