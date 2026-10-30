# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for KeycloakSignIn-Helpers.ps1, the browserless portal sign-in Scenario 033 drives (#2615).

.DESCRIPTION
    The helpers sign a person in to JIM's portal through the bundled Keycloak over plain HTTP, as a browser does: follow
    JIM's challenge to Keycloak, fill in Keycloak's login form, and carry the reply back to JIM's /signin-oidc with the
    cookies a browser would send. What they decide from a page or a header is pure and proven here against HTML and
    headers shaped like Keycloak 26.8's and ASP.NET's own. The redirect chain itself is proven against a small
    in-process server that plays JIM and Keycloak: it refuses a request whose cookies a browser would have sent (a
    Secure cookie over http://localhost, a cookie scoped to another path), and it advertises Keycloak at an address
    nothing answers, as the bundled stack does where its 8181 bridge is not running.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot 'KeycloakSignIn-Helpers.ps1')

    # Keycloak 26.8's login page, trimmed to the form: the action names the hostname Keycloak is configured with
    # (KC_HOSTNAME), not the address it was reached at.
    $script:KeycloakLoginPage = @'
<!DOCTYPE html>
<html class="" lang="en"><head><title>Sign in to jim</title></head>
<body>
<div id="kc-form"><div id="kc-form-wrapper">
<form id="kc-form-login" class="pf-v5-c-form pf-v5-u-w-100" onsubmit="login.disabled = true; return true;" action="http://localhost:8181/realms/jim/login-actions/authenticate?session_code=q5eOQ&amp;execution=cf82&amp;client_id=jim-web&amp;tab_id=fPiK" method="post" novalidate="novalidate">
  <div class="pf-v5-c-form__group">
    <input id="username" name="username" value="" type="text" autocomplete="username" autofocus aria-invalid=""/>
    <div id="input-error-container-username"></div>
  </div>
  <div class="pf-v5-c-form__group">
    <input id="password" name="password" value="" type="password" autocomplete="current-password" aria-invalid=""/>
    <div id="input-error-container-password"></div>
  </div>
  <div id="kc-form-buttons">
    <input type="hidden" id="id-hidden-input" name="credentialId" />
    <input id="kc-login" type="submit" value="Sign In"/>
  </div>
</form>
</div></div>
</body></html>
'@

    # What Keycloak answers a successful login with when the client asked for response_mode=form_post, as JIM's portal
    # does: no redirect, a page whose form a browser submits to the client on load. The tags are upper case, as
    # Keycloak writes them.
    $script:KeycloakFormPostPage = @'
<HTML>  <HEAD>    <TITLE>OIDC Form_Post Response</TITLE>  <SCRIPT> if (typeof history.replaceState === 'function') {  history.replaceState({}, "some title", "http://localhost:8181/realms/jim/login-actions/authenticate?client_id=jim-web&tab_id=HIpHt"); }</SCRIPT></HEAD>  <BODY Onload="document.forms[0].submit()">    <FORM METHOD="POST" ACTION="{0}/signin-oidc">  <INPUT TYPE="HIDDEN" NAME="code" VALUE="07db100c-aa3c.4h0gKuMd" />  <INPUT TYPE="HIDDEN" NAME="iss" VALUE="http://localhost:8181/realms/jim" />  <INPUT TYPE="HIDDEN" NAME="state" VALUE="CfDJ8AcQ-_x" />  <INPUT TYPE="HIDDEN" NAME="session_state" VALUE="4h0gKuMd" />      <NOSCRIPT>        <P>JavaScript is disabled. Click the button below to continue .</P>        <INPUT name="continue" TYPE="SUBMIT" VALUE="CONTINUE" />      </NOSCRIPT>    </FORM>  </BODY></HTML>
'@

    $script:KeycloakRefusedPage = $script:KeycloakLoginPage.Replace(
        '<div id="input-error-container-username"></div>',
        '<div id="input-error-container-username"><div class="pf-v5-c-helper-text__item pf-m-error" id="input-error-username">
                        <span class="pf-v5-c-helper-text__item-text pf-m-error kc-feedback-text">
                            Invalid username or password.
                        </span></div></div>')

    # An in-process server that plays JIM (/, /signin-oidc, /api/v1/userinfo) and Keycloak (/kc/...) on one port.
    # Keycloak is advertised at an origin nothing listens on, so the sign-in only works by reaching it through the
    # origin map, and every cookie rule a browser applies is enforced rather than assumed.
    $script:StartFakeStack = {
        $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $probe.Start()
        $port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port
        $probe.Stop()

        $listener = [System.Net.HttpListener]::new()
        $listener.Prefixes.Add("http://localhost:$port/")
        $listener.Start()

        $job = Start-ThreadJob -ArgumentList $listener, $port, $script:KeycloakLoginPage, $script:KeycloakRefusedPage, $script:KeycloakFormPostPage -ScriptBlock {
            param($listener, $port, $loginPage, $refusedPage, $formPostPage)

            $advertised = 'http://localhost:1'
            while ($listener.IsListening) {
                try { $context = $listener.GetContext() } catch { break }
                $request = $context.Request
                $response = $context.Response
                $path = $request.Url.AbsolutePath
                $cookies = "$($request.Headers['Cookie'])"
                $body = ''
                $response.StatusCode = 200
                $response.ContentType = 'text/html'

                switch -Regex ("$($request.HttpMethod) $path") {
                    '^GET /$' {
                        if ($cookies -match '\.AspNetCore\.Cookies=session') { $body = 'Home' }
                        else {
                            $response.StatusCode = 302
                            $response.RedirectLocation = "$advertised/kc/realms/jim/protocol/openid-connect/auth?state=abc"
                            $response.AppendHeader('Set-Cookie', '.AspNetCore.Correlation.abc=1; path=/signin-oidc; httponly')
                        }
                        break
                    }
                    '^GET /kc/realms/jim/protocol/openid-connect/auth$' {
                        $body = $loginPage.Replace('http://localhost:8181/realms/jim', "$advertised/kc/realms/jim")
                        $response.AppendHeader('Set-Cookie', 'AUTH_SESSION_ID=kc1;Version=1;Path=/kc/realms/jim/;Secure;HttpOnly;SameSite=None')
                        $response.AppendHeader('Set-Cookie', 'KC_RESTART=restart1;Version=1;Path=/kc/realms/jim/;Secure;HttpOnly;SameSite=None')
                        break
                    }
                    '^POST /kc/realms/jim/login-actions/authenticate$' {
                        $form = [System.IO.StreamReader]::new($request.InputStream).ReadToEnd()
                        if ($cookies -notmatch 'AUTH_SESSION_ID=kc1') {
                            $response.StatusCode = 400
                            $body = 'Cookie not found'
                        }
                        elseif ($form -match 'password=good(&|$)') {
                            # response_mode=form_post: a page to submit to JIM, not a redirect to it.
                            $body = $formPostPage.Replace('{0}', "http://localhost:$port")
                            $response.AppendHeader('Set-Cookie', 'KC_RESTART=;Version=1;Path=/kc/realms/jim/;Max-Age=0')
                            $response.AppendHeader('Set-Cookie', 'KEYCLOAK_IDENTITY=identity1;Version=1;Path=/kc/realms/jim/;Secure;HttpOnly')
                        }
                        else { $body = $refusedPage.Replace('http://localhost:8181/realms/jim', "$advertised/kc/realms/jim") }
                        break
                    }
                    '^POST /signin-oidc$' {
                        $form = [System.IO.StreamReader]::new($request.InputStream).ReadToEnd()
                        if ($cookies -notmatch '\.AspNetCore\.Correlation\.abc=1') {
                            $response.StatusCode = 400
                            $body = 'Correlation failed.'
                        }
                        elseif ($cookies -match 'KEYCLOAK_IDENTITY|AUTH_SESSION_ID') {
                            $response.StatusCode = 400
                            $body = 'Keycloak cookies must not reach JIM.'
                        }
                        elseif ($form -notmatch 'code=07db100c-aa3c\.4h0gKuMd' -or $form -notmatch 'state=CfDJ8AcQ-_x') {
                            $response.StatusCode = 400
                            $body = 'The reply''s fields must reach JIM.'
                        }
                        else {
                            $response.StatusCode = 302
                            $response.RedirectLocation = "http://localhost:$port/"
                            $response.AppendHeader('Set-Cookie', '.AspNetCore.Cookies=session; path=/; httponly')
                        }
                        break
                    }
                    '^GET /api/v1/userinfo$' {
                        $response.ContentType = 'application/json'
                        if ($cookies -match '\.AspNetCore\.Cookies=session' -and $request.Headers['X-Requested-With'] -eq 'XMLHttpRequest') {
                            $body = '{"authorised":true,"isAdministrator":true,"metaverseObjectId":"11111111-2222-3333-4444-555555555555","roles":["Administrator"]}'
                        }
                        else { $response.StatusCode = 401 }
                        break
                    }
                    '^GET /api/v1/security/roles$' {
                        $response.StatusCode = if ($cookies -match '\.AspNetCore\.Cookies=session') { 200 } else { 401 }
                        break
                    }
                    default { $response.StatusCode = 404 }
                }

                $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
                $response.ContentLength64 = $bytes.Length
                $response.OutputStream.Write($bytes, 0, $bytes.Length)
                $response.Close()
            }
        }

        [pscustomobject]@{ Listener = $listener; Job = $job; Port = $port; JimUrl = "http://localhost:$port" }
    }
}

Describe 'ConvertFrom-SetCookie' {
    It 'reads the name, the value and the path, ignoring attributes a browser does not enforce on http://localhost' {
        $cookie = ConvertFrom-SetCookie -SetCookie 'AUTH_SESSION_ID=bGRU;Version=1;Path=/realms/jim/;Secure;HttpOnly;SameSite=None' -RequestUri 'http://localhost:8180/realms/jim/protocol/openid-connect/auth'
        $cookie.Name | Should -Be 'AUTH_SESSION_ID'
        $cookie.Value | Should -Be 'bGRU'
        $cookie.Path | Should -Be '/realms/jim/'
        $cookie.Delete | Should -BeFalse
    }

    It 'keeps a quoted value and an equals sign inside a value exactly as sent' {
        (ConvertFrom-SetCookie -SetCookie 'KC_AUTH_SESSION_HASH="d6cc/3D+JP=";Version=1;Path=/realms/jim/;Max-Age=60' -RequestUri 'http://localhost/').Value | Should -Be '"d6cc/3D+JP="'
    }

    It 'takes the path from the request when the cookie names none (the directory of the request path)' {
        (ConvertFrom-SetCookie -SetCookie '.AspNetCore.Correlation.abc=1; httponly' -RequestUri 'http://localhost:5200/portal/sign-in/start').Path | Should -Be '/portal/sign-in'
        (ConvertFrom-SetCookie -SetCookie 'a=1' -RequestUri 'http://localhost:5200/').Path | Should -Be '/'
    }

    It 'marks a cookie for deletion by Max-Age=0, by an empty value, and by an Expires in the past' {
        (ConvertFrom-SetCookie -SetCookie 'KC_RESTART=;Version=1;Path=/realms/jim/;Max-Age=0' -RequestUri 'http://localhost/').Delete | Should -BeTrue
        (ConvertFrom-SetCookie -SetCookie 'KC_RESTART=;Path=/' -RequestUri 'http://localhost/').Delete | Should -BeTrue
        (ConvertFrom-SetCookie -SetCookie 'a=b; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/' -RequestUri 'http://localhost/').Delete | Should -BeTrue
        (ConvertFrom-SetCookie -SetCookie 'a=b; Expires=Fri, 01 Jan 2100 00:00:00 GMT; Path=/' -RequestUri 'http://localhost/').Delete | Should -BeFalse
    }
}

Describe 'Get-BrowserCookieHeader' {
    BeforeEach {
        $script:Browser = New-SignInBrowser
    }

    It 'sends a Secure cookie over plain http to localhost, as a browser does and .NET''s own cookie store does not' {
        Set-BrowserCookie -Browser $script:Browser -Uri 'http://localhost:8180/realms/jim/protocol/openid-connect/auth' -SetCookie 'AUTH_SESSION_ID=kc1;Path=/realms/jim/;Secure;HttpOnly'
        Get-BrowserCookieHeader -Browser $script:Browser -Uri 'http://localhost:8180/realms/jim/login-actions/authenticate' | Should -Be 'AUTH_SESSION_ID=kc1'
    }

    It 'sends a cookie only to paths under its own, and not to a path that merely starts with the same letters' {
        Set-BrowserCookie -Browser $script:Browser -Uri 'http://localhost:5200/' -SetCookie '.AspNetCore.Correlation.abc=1; path=/signin-oidc'
        Get-BrowserCookieHeader -Browser $script:Browser -Uri 'http://localhost:5200/signin-oidc?code=1' | Should -Be '.AspNetCore.Correlation.abc=1'
        Get-BrowserCookieHeader -Browser $script:Browser -Uri 'http://localhost:5200/' | Should -BeNullOrEmpty
        Get-BrowserCookieHeader -Browser $script:Browser -Uri 'http://localhost:5200/signin-oidc-other' | Should -BeNullOrEmpty
    }

    It 'sends the cookies of one host, whichever port, and none to another host' {
        Set-BrowserCookie -Browser $script:Browser -Uri 'http://localhost:8180/realms/jim/' -SetCookie 'a=1; Path=/'
        Get-BrowserCookieHeader -Browser $script:Browser -Uri 'http://localhost:5200/anything' | Should -Be 'a=1'
        Get-BrowserCookieHeader -Browser $script:Browser -Uri 'http://jim.web:8080/anything' | Should -BeNullOrEmpty
    }

    It 'replaces a cookie of the same name and path, and drops one the server deletes' {
        Set-BrowserCookie -Browser $script:Browser -Uri 'http://localhost/' -SetCookie @('a=1; Path=/', 'b=2; Path=/')
        Set-BrowserCookie -Browser $script:Browser -Uri 'http://localhost/' -SetCookie 'a=3; Path=/'
        Get-BrowserCookieHeader -Browser $script:Browser -Uri 'http://localhost/' | Should -Be 'a=3; b=2'
        Set-BrowserCookie -Browser $script:Browser -Uri 'http://localhost/' -SetCookie 'a=; Path=/; Max-Age=0'
        Get-BrowserCookieHeader -Browser $script:Browser -Uri 'http://localhost/' | Should -Be 'b=2'
    }

    It 'sends the longest path first, as a browser orders them' {
        Set-BrowserCookie -Browser $script:Browser -Uri 'http://localhost/' -SetCookie @('wide=1; Path=/', 'narrow=2; Path=/realms/jim/')
        Get-BrowserCookieHeader -Browser $script:Browser -Uri 'http://localhost/realms/jim/login' | Should -Be 'narrow=2; wide=1'
    }
}

Describe 'Convert-ToReachableUri' {
    It 'rewrites an advertised origin to the address it is reachable at, keeping the path and query' {
        $map = @{ 'http://localhost:8181' = 'http://localhost:8180' }
        (Convert-ToReachableUri -Uri 'http://localhost:8181/realms/jim/login-actions/authenticate?session_code=a&execution=b' -OriginMap $map).AbsoluteUri |
            Should -Be 'http://localhost:8180/realms/jim/login-actions/authenticate?session_code=a&execution=b'
    }

    It 'leaves an address it has no mapping for alone' {
        (Convert-ToReachableUri -Uri 'http://localhost:5200/signin-oidc?code=1' -OriginMap @{ 'http://localhost:8181' = 'http://localhost:8180' }).AbsoluteUri |
            Should -Be 'http://localhost:5200/signin-oidc?code=1'
        (Convert-ToReachableUri -Uri 'http://localhost:5200/' -OriginMap @{}).AbsoluteUri | Should -Be 'http://localhost:5200/'
    }
}

Describe 'Find-KeycloakLoginForm' {
    It 'reads the login form: its action decoded, and its fields in order' {
        $form = Find-KeycloakLoginForm -Html $script:KeycloakLoginPage
        $form.Id | Should -Be 'kc-form-login'
        $form.Method | Should -Be 'POST'
        $form.Action | Should -Be 'http://localhost:8181/realms/jim/login-actions/authenticate?session_code=q5eOQ&execution=cf82&client_id=jim-web&tab_id=fPiK'
        @($form.Fields.Keys) | Should -Be @('username', 'password', 'credentialId')
    }

    It 'finds nothing on a page that asks for no password' {
        Find-KeycloakLoginForm -Html '<html><body><form action="/x"><input name="code" value="1"/></form></body></html>' | Should -BeNullOrEmpty
        Find-KeycloakLoginForm -Html '' | Should -BeNullOrEmpty
    }
}

Describe 'Get-KeycloakSignInError' {
    It 'reads the reason Keycloak gives for refusing a sign-in' {
        Get-KeycloakSignInError -Html $script:KeycloakRefusedPage | Should -Be 'Invalid username or password.'
    }

    It 'finds no error on the page Keycloak shows first' {
        Get-KeycloakSignInError -Html $script:KeycloakLoginPage | Should -BeNullOrEmpty
    }
}

Describe 'Invoke-KeycloakPortalSignIn' {
    BeforeAll {
        $script:Stack = & $script:StartFakeStack
        $script:OriginMap = @{ 'http://localhost:1' = $script:Stack.JimUrl }
    }

    AfterAll {
        $script:Stack.Listener.Stop()
        $script:Stack.Job | Wait-Job -Timeout 10 | Out-Null
        $script:Stack.Job | Remove-Job -Force
    }

    It 'signs a person in through JIM''s challenge, Keycloak''s login form and JIM''s callback, and returns the browser holding JIM''s cookie' {
        $browser = Invoke-KeycloakPortalSignIn -JimUrl $script:Stack.JimUrl -UserName 'admin' -Password 'good' -OriginMap $script:OriginMap
        Get-BrowserCookieHeader -Browser $browser -Uri "$($script:Stack.JimUrl)/api/v1/userinfo" | Should -Match '\.AspNetCore\.Cookies=session'
    }

    It 'reads who JIM says the signed-in person is, and the status of an Administrator-only API' {
        $browser = Invoke-KeycloakPortalSignIn -JimUrl $script:Stack.JimUrl -UserName 'admin' -Password 'good' -OriginMap $script:OriginMap
        $userInfo = Get-KeycloakPortalUserInfo -Browser $browser -JimUrl $script:Stack.JimUrl
        $userInfo.isAdministrator | Should -BeTrue
        $userInfo.metaverseObjectId | Should -Be '11111111-2222-3333-4444-555555555555'
        Get-KeycloakPortalAdministratorApiStatus -Browser $browser -JimUrl $script:Stack.JimUrl | Should -Be 200
    }

    It 'throws Keycloak''s own reason when it refuses the sign-in, and never the password' {
        { Invoke-KeycloakPortalSignIn -JimUrl $script:Stack.JimUrl -UserName 'admin' -Password 'bad-password-value' -OriginMap $script:OriginMap } |
            Should -Throw -ExpectedMessage '*Keycloak refused the sign-in for admin: Invalid username or password.*'
        $thrown = try { Invoke-KeycloakPortalSignIn -JimUrl $script:Stack.JimUrl -UserName 'admin' -Password 'bad-password-value' -OriginMap $script:OriginMap } catch { "$($_.Exception.Message)" }
        $thrown | Should -Not -Match 'bad-password-value'
    }

    It 'learns the advertised Keycloak address from JIM''s challenge when told only where Keycloak is reachable' {
        $browser = Invoke-KeycloakPortalSignIn -JimUrl $script:Stack.JimUrl -UserName 'admin' -Password 'good' -KeycloakUrl $script:Stack.JimUrl
        (Get-KeycloakPortalUserInfo -Browser $browser -JimUrl $script:Stack.JimUrl).isAdministrator | Should -BeTrue
    }

    It 'does not change the origin map it was given' {
        $map = @{}
        Invoke-KeycloakPortalSignIn -JimUrl $script:Stack.JimUrl -UserName 'admin' -Password 'good' -KeycloakUrl $script:Stack.JimUrl -OriginMap $map | Out-Null
        $map.Count | Should -Be 0
    }

    It 'fails with where it stopped when the advertised Keycloak cannot be reached (no origin map)' {
        { Invoke-KeycloakPortalSignIn -JimUrl $script:Stack.JimUrl -UserName 'admin' -Password 'good' } | Should -Throw
    }

    It 'reports a browser that is not signed in as 401 from the userinfo and roles endpoints' {
        $browser = New-SignInBrowser
        { Get-KeycloakPortalUserInfo -Browser $browser -JimUrl $script:Stack.JimUrl } | Should -Throw -ExpectedMessage '*401*'
        Get-KeycloakPortalAdministratorApiStatus -Browser $browser -JimUrl $script:Stack.JimUrl | Should -Be 401
    }
}
