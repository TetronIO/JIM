# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for AdfsSignInClient.psm1, the browserless sign-in the AD FS checks drive (#2441).

.DESCRIPTION
    The client signs people in to JIM through the lab's AD FS server over plain HTTP, from the LDAP toolbox container,
    so it only runs in the lab. What it decides from a page is pure and proven here: which forms a page holds, their
    actions and fields, the AD FS forms sign-in page and the form_post reply JIM's portal reads, where a redirect points,
    and the fields AD FS's forms sign-in posts. The HTML is shaped like AD FS 2025's and ASP.NET's own.
#>

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'AdfsSignInClient.psm1') -Force

    $script:AdfsLoginPage = @'
<!DOCTYPE html>
<html lang="en-GB"><head><title>Sign In</title></head>
<body>
<form method="post" id="loginForm" autocomplete="off" novalidate="novalidate" onKeyPress="if (event && event.keyCode == 13) Login.submitLoginRequest();" action="/adfs/oauth2/authorize/?client_id=abc&amp;redirect_uri=https%3a%2f%2fjim.web%3a8443%2fsignin-oidc&amp;client-request-id=1f2e" >
    <div id="error" class="fieldMargin error smallText">
        <span id="errorText" for=""></span>
    </div>
    <input id="userNameInput" name="UserName" type="email" value="" tabindex="1" class="text fullWidth" spellcheck="false" placeholder="someone@example.com" autocomplete="off"/>
    <input id="passwordInput" name="Password" type="password" tabindex="2" class="text fullWidth" placeholder="Password" autocomplete="off"/>
    <input id="optionForms" type="hidden" name="AuthMethod" value="FormsAuthentication"/>
    <span id="submitButton" class="submit" tabindex="4" role="button" onKeyPress="" onclick="return Login.submitLoginRequest();">Sign in</span>
</form>
</body></html>
'@

    $script:AdfsRefusedPage = $script:AdfsLoginPage.Replace('<span id="errorText" for=""></span>', '<span id="errorText" for="">Incorrect user ID or password. Type the correct user ID and password, and try again.</span>')

    $script:FormPostPage = @'
<html><head><title>Working...</title></head><body>
<form method="POST" name="hiddenform" action="https://jim.web:8443/signin-oidc">
<input type="hidden" name="code" value="AAABAAAA&amp;x" />
<input type="hidden" name="state" value="CfDJ8Ab-_q" />
<noscript><p>Script is disabled. Click Submit to continue.</p><input type="submit" value="Submit" /></noscript>
</form><script language="javascript">window.setTimeout('document.forms[0].submit()', 0);</script></body></html>
'@
}

Describe 'ConvertFrom-HtmlForm' {
    It 'reads AD FS''s forms sign-in page: its id, its action decoded, and every named field in order' {
        $forms = @(ConvertFrom-HtmlForm -Html $script:AdfsLoginPage)
        $forms.Count | Should -Be 1
        $forms[0].Id | Should -Be 'loginForm'
        $forms[0].Method | Should -Be 'POST'
        $forms[0].Action | Should -Be '/adfs/oauth2/authorize/?client_id=abc&redirect_uri=https%3a%2f%2fjim.web%3a8443%2fsignin-oidc&client-request-id=1f2e'
        @($forms[0].Fields.Keys) | Should -Be @('UserName', 'Password', 'AuthMethod')
        $forms[0].Fields['AuthMethod'] | Should -Be 'FormsAuthentication'
        $forms[0].Fields['Password'] | Should -Be ''
    }

    It 'reads the form_post reply: the callback and its hidden fields, HTML-decoded, without the submit button' {
        $form = @(ConvertFrom-HtmlForm -Html $script:FormPostPage)[0]
        $form.Action | Should -Be 'https://jim.web:8443/signin-oidc'
        @($form.Fields.Keys) | Should -Be @('code', 'state')
        $form.Fields['code'] | Should -Be 'AAABAAAA&x'
    }

    It 'finds nothing in a page with no form' {
        @(ConvertFrom-HtmlForm -Html '<html><body>Nothing here</body></html>') | Should -BeNullOrEmpty
    }

    It 'reads single-quoted attributes and several forms' {
        $forms = @(ConvertFrom-HtmlForm -Html "<form action='/a'><input name='x' value='1'></form><form id=b action=/b method=get><input name=y></form>")
        $forms.Count | Should -Be 2
        $forms[0].Action | Should -Be '/a'
        $forms[0].Fields['x'] | Should -Be '1'
        $forms[1].Id | Should -Be 'b'
        $forms[1].Method | Should -Be 'GET'
        $forms[1].Fields['y'] | Should -Be ''
    }
}

Describe 'Find-AdfsSignInForm' {
    It 'finds the form that asks for a user name and password' {
        (Find-AdfsSignInForm -Html $script:AdfsLoginPage).Id | Should -Be 'loginForm'
    }

    It 'finds none on a page that is not a sign-in page' {
        Find-AdfsSignInForm -Html $script:FormPostPage | Should -BeNullOrEmpty
    }
}

Describe 'Get-AdfsSignInError' {
    It 'reads the reason AD FS gives for refusing a sign-in' {
        Get-AdfsSignInError -Html $script:AdfsRefusedPage | Should -BeLike 'Incorrect user ID or password*'
    }

    It 'is empty on a page with no error' {
        Get-AdfsSignInError -Html $script:AdfsLoginPage | Should -BeNullOrEmpty
    }
}

Describe 'New-AdfsSignInFields' {
    It 'fills in the user name and password, keeps the page''s other fields, and asks for forms authentication' {
        $form = Find-AdfsSignInForm -Html $script:AdfsLoginPage
        $fields = New-AdfsSignInFields -Form $form -UserName 'jim-sso-admin@panoply.local' -Password 'placeholder-password'
        $fields['UserName'] | Should -Be 'jim-sso-admin@panoply.local'
        $fields['Password'] | Should -Be 'placeholder-password'
        $fields['AuthMethod'] | Should -Be 'FormsAuthentication'
    }

    It 'adds AuthMethod when the page does not carry it' {
        $form = @(ConvertFrom-HtmlForm -Html '<form action="/adfs/ls"><input name="UserName"><input name="Password" type="password"></form>')[0]
        (New-AdfsSignInFields -Form $form -UserName 'u' -Password 'p')['AuthMethod'] | Should -Be 'FormsAuthentication'
    }
}

Describe 'Resolve-SignInUri' {
    It 'resolves a relative action against the page it came from' {
        (Resolve-SignInUri -Base 'https://adfs.panoply.local/adfs/oauth2/authorize/?client_id=abc' -Reference '/adfs/oauth2/authorize/?x=1').AbsoluteUri |
            Should -Be 'https://adfs.panoply.local/adfs/oauth2/authorize/?x=1'
    }

    It 'keeps an absolute one' {
        (Resolve-SignInUri -Base 'https://adfs.panoply.local/adfs/' -Reference 'https://jim.web:8443/signin-oidc').AbsoluteUri |
            Should -Be 'https://jim.web:8443/signin-oidc'
    }
}

Describe 'Test-LoopbackCallback' {
    It 'recognises the PowerShell module''s loopback callback, on any of its ports' {
        Test-LoopbackCallback -Uri 'http://localhost:8400/callback/?code=abc&state=xyz' | Should -BeTrue
        Test-LoopbackCallback -Uri 'http://localhost:8409/callback/?code=abc' | Should -BeTrue
    }

    It 'does not mistake anything else for it' {
        Test-LoopbackCallback -Uri 'https://jim.web:8443/signin-oidc' | Should -BeFalse
        Test-LoopbackCallback -Uri 'http://localhost:8410/callback/' | Should -BeFalse
        Test-LoopbackCallback -Uri 'https://localhost:8400/callback/' | Should -BeFalse
    }
}

Describe 'Get-TryItSignInSetting' {
    BeforeAll {
        # The OpenAPI document as JIM serves it to AD FS deployments (#2544, #2594), and the API reference page with the
        # configuration Scalar writes into it.
        $script:OpenApiDocument = @'
{"components":{"securitySchemes":{"OAuth2":{"type":"oauth2","flows":{"authorizationCode":{
  "authorizationUrl":"https://adfs.panoply.local/adfs/oauth2/authorize/","tokenUrl":"https://adfs.panoply.local/adfs/oauth2/token/",
  "scopes":{"openid":"OpenID Connect","api://11111111-1111-1111-1111-111111111111/openid":"Access JIM API"}}}}}}}
'@
        $script:ReferencePage = @'
<html><body><div id="app"></div><script type="module">
import { initialize } from './scalar.aspnetcore.js'
initialize('/api/openapi/v1.json', true, {"authentication":{"securitySchemes":{"OAuth2":{"flows":{"authorizationCode":{"x-scalar-client-id":"22222222-2222-2222-2222-222222222222","x-usePkce":"SHA-256","selectedScopes":["openid"]}}}}}}, '')
</script></body></html>
'@
        $script:Respond = {
            param([string]$Document, [string]$Page)
            Mock Invoke-SignInRequest -ModuleName AdfsSignInClient -ParameterFilter { "$Uri" -like '*/api/openapi/v1.json' } -MockWith {
                [pscustomobject]@{ Uri = [uri]'https://jim.web:8443/api/openapi/v1.json'; StatusCode = 200; Location = $null; Content = $Document }
            }.GetNewClosure()
            # Scalar redirects the bare path to the page with a trailing slash, the address the browser then signs in from.
            Mock Invoke-SignInRequest -ModuleName AdfsSignInClient -ParameterFilter { "$Uri" -like '*/api/reference' } -MockWith {
                [pscustomobject]@{ Uri = [uri]'https://jim.web:8443/api/reference'; StatusCode = 302; Location = [uri]'https://jim.web:8443/api/reference/'; Content = '' }
            }
            Mock Invoke-SignInRequest -ModuleName AdfsSignInClient -ParameterFilter { "$Uri" -like '*/api/reference/' } -MockWith {
                [pscustomobject]@{
                    Uri = [uri]'https://jim.web:8443/api/reference/'; StatusCode = 200; Location = $null; Content = $Page
                    ContentSecurityPolicy = "default-src 'self'; connect-src 'self' https://adfs.panoply.local; frame-ancestors 'none'"
                }
            }.GetNewClosure()
        }
    }

    It 'reads the endpoints and API scope from the OpenAPI document, and the client ID from the page, as a browser does' {
        & $script:Respond $script:OpenApiDocument $script:ReferencePage

        $settings = Get-TryItSignInSetting -Session ([Microsoft.PowerShell.Commands.WebRequestSession]::new()) -JimUrl 'https://jim.web:8443'

        $settings.AuthorizationUrl | Should -Be 'https://adfs.panoply.local/adfs/oauth2/authorize/'
        $settings.TokenUrl | Should -Be 'https://adfs.panoply.local/adfs/oauth2/token/'
        $settings.ApiScope | Should -Be 'api://11111111-1111-1111-1111-111111111111/openid'
        $settings.ClientId | Should -Be '22222222-2222-2222-2222-222222222222'
    }

    It 'follows the page''s redirect, and signs in from the address the browser ends up on, as Scalar does' {
        & $script:Respond $script:OpenApiDocument $script:ReferencePage

        (Get-TryItSignInSetting -Session ([Microsoft.PowerShell.Commands.WebRequestSession]::new()) -JimUrl 'https://jim.web:8443').RedirectUri |
            Should -Be 'https://jim.web:8443/api/reference/'
    }

    It 'reads the sources the page''s Content Security Policy lets it connect to, which a browser enforces on the token request' {
        & $script:Respond $script:OpenApiDocument $script:ReferencePage

        (Get-TryItSignInSetting -Session ([Microsoft.PowerShell.Commands.WebRequestSession]::new()) -JimUrl 'https://jim.web:8443').ConnectSources |
            Should -Be @("'self'", 'https://adfs.panoply.local')
    }

    It 'has no connect sources when the page has no Content Security Policy' {
        & $script:Respond $script:OpenApiDocument $script:ReferencePage
        Mock Invoke-SignInRequest -ModuleName AdfsSignInClient -ParameterFilter { "$Uri" -like '*/api/reference/' } -MockWith {
            [pscustomobject]@{ Uri = [uri]'https://jim.web:8443/api/reference/'; StatusCode = 200; Location = $null; Content = $script:ReferencePage; ContentSecurityPolicy = $null }
        }

        @((Get-TryItSignInSetting -Session ([Microsoft.PowerShell.Commands.WebRequestSession]::new()) -JimUrl 'https://jim.web:8443').ConnectSources) |
            Should -BeNullOrEmpty
    }

    It 'reads a client ID Scalar names clientId' {
        & $script:Respond $script:OpenApiDocument $script:ReferencePage.Replace('"x-scalar-client-id"', '"clientId"')

        (Get-TryItSignInSetting -Session ([Microsoft.PowerShell.Commands.WebRequestSession]::new()) -JimUrl 'https://jim.web:8443').ClientId |
            Should -Be '22222222-2222-2222-2222-222222222222'
    }

    It 'says so when the page names no client ID' {
        & $script:Respond $script:OpenApiDocument '<html><body>No configuration</body></html>'

        { Get-TryItSignInSetting -Session ([Microsoft.PowerShell.Commands.WebRequestSession]::new()) -JimUrl 'https://jim.web:8443' } |
            Should -Throw '*names no OAuth client ID*'
    }

    It 'says so when the document does not offer exactly one API scope' {
        & $script:Respond $script:OpenApiDocument.Replace(',"api://11111111-1111-1111-1111-111111111111/openid":"Access JIM API"', '') $script:ReferencePage

        { Get-TryItSignInSetting -Session ([Microsoft.PowerShell.Commands.WebRequestSession]::new()) -JimUrl 'https://jim.web:8443' } |
            Should -Throw '*offers 0 API scopes*'
    }
}

Describe 'ConvertFrom-EntraIdSignInPage' {
    BeforeAll {
        # The shape of Microsoft Entra ID's sign-in page: the page's state in a $Config object, in a CDATA script block.
        $script:NewEntraPage = {
            param([string]$Config)
            "<!DOCTYPE html><html><head><script type=`"text/javascript`" nonce='abc'>//<![CDATA[`n`$Config=$Config;`n//]]></script> `r`n<script type=`"text/javascript`">var x = { a: 1 };</script></head><body></body></html>"
        }
    }

    It 'reads the page''s id, where it posts, and the tokens a post must carry' {
        $page = ConvertFrom-EntraIdSignInPage -Html (& $script:NewEntraPage '{"pgid":"ConvergedSignIn","urlPost":"/tenant/login","sFT":"flow-token","sCtx":"context","canary":"canary-value","sErrorCode":"","text":"a }; inside a string"}')

        $page.PageId | Should -Be 'ConvergedSignIn'
        $page.PostTo | Should -Be '/tenant/login'
        $page.FlowToken | Should -Be 'flow-token'
        $page.Context | Should -Be 'context'
        $page.Canary | Should -Be 'canary-value'
        $page.ErrorCode | Should -BeNullOrEmpty
    }

    It 'reads the error Entra ID gives for refusing a sign-in' {
        $page = ConvertFrom-EntraIdSignInPage -Html (& $script:NewEntraPage '{"pgid":"ConvergedSignIn","urlPost":"/tenant/login","sFT":"f","sCtx":"c","canary":"k","sErrorCode":"50126","strServiceExceptionMessage":"Your account or password is incorrect."}')

        $page.ErrorCode | Should -Be '50126'
        $page.ErrorText | Should -Be 'Your account or password is incorrect.'
    }

    It 'finds nothing on a page that is not Entra ID''s' {
        ConvertFrom-EntraIdSignInPage -Html '<html><body><form action="/signin-oidc" method="post"></form></body></html>' | Should -BeNullOrEmpty
    }
}

Describe 'New-EntraIdSignInFields' {
    It 'posts the person''s name and password with the page''s tokens, declining to stay signed in' {
        $page = [pscustomobject]@{ PageId = 'ConvergedSignIn'; PostTo = '/t/login'; FlowToken = 'f'; Context = 'c'; Canary = 'k' }

        $fields = New-EntraIdSignInFields -Page $page -UserName 'jim-test-admin@contoso.onmicrosoft.com' -Password 'pw'

        $fields.login | Should -Be 'jim-test-admin@contoso.onmicrosoft.com'
        $fields.loginfmt | Should -Be 'jim-test-admin@contoso.onmicrosoft.com'
        $fields.passwd | Should -Be 'pw'
        $fields.flowToken | Should -Be 'f'
        $fields.ctx | Should -Be 'c'
        $fields.canary | Should -Be 'k'
    }
}

Describe 'Complete-EntraIdSignIn' {
    BeforeAll {
        $script:EntraPage = {
            param([string]$PageId, [string]$PostTo, [string]$ErrorCode = '', [string]$ErrorText = '')
            "<script>//<![CDATA[`n`$Config={`"pgid`":`"$PageId`",`"urlPost`":`"$PostTo`",`"sFT`":`"f`",`"sCtx`":`"c`",`"canary`":`"k`",`"sErrorCode`":`"$ErrorCode`",`"strServiceExceptionMessage`":`"$ErrorText`"};`n//]]></script>"
        }
        $script:Session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        $script:AtEntra = [pscustomobject]@{ Uri = [uri]'https://login.microsoftonline.com/t/oauth2/v2.0/authorize'; StatusCode = 200; Location = $null; Content = (& $script:EntraPage 'ConvergedSignIn' '/t/login') }
    }

    It 'signs in and returns where Entra ID sends the browser next' {
        Mock -ModuleName AdfsSignInClient Invoke-SignInRequest {
            [pscustomobject]@{ Uri = $Uri; StatusCode = 302; Location = [uri]'http://localhost:8400/callback/?code=x'; Content = '' }
        }

        $result = Complete-EntraIdSignIn -Session $script:Session -Response $script:AtEntra -UserName 'u@contoso.onmicrosoft.com' -Password 'pw'

        $result.Location.AbsoluteUri | Should -BeLike 'http://localhost:8400/callback/*'
        Should -Invoke -ModuleName AdfsSignInClient Invoke-SignInRequest -Times 1 -ParameterFilter { $Uri.AbsoluteUri -eq 'https://login.microsoftonline.com/t/login' -and $Method -eq 'POST' }
    }

    It 'answers "stay signed in?" when Entra ID asks it' {
        Mock -ModuleName AdfsSignInClient Invoke-SignInRequest {
            if ($Uri.AbsolutePath -eq '/t/login') {
                return [pscustomobject]@{ Uri = $Uri; StatusCode = 200; Location = $null; Content = (& $script:EntraPage 'KmsiInterrupt' '/kmsi') }
            }
            [pscustomobject]@{ Uri = $Uri; StatusCode = 200; Location = $null; Content = '<form method="POST" action="https://jim.example.com/signin-oidc"><input type="hidden" name="code" value="x" /></form>' }
        }

        $result = Complete-EntraIdSignIn -Session $script:Session -Response $script:AtEntra -UserName 'u@contoso.onmicrosoft.com' -Password 'pw'

        $result.Content | Should -BeLike '*signin-oidc*'
        Should -Invoke -ModuleName AdfsSignInClient Invoke-SignInRequest -Times 1 -ParameterFilter { $Uri.AbsolutePath -eq '/kmsi' }
    }

    It 'throws with Entra ID''s own reason when it refuses the sign-in' {
        Mock -ModuleName AdfsSignInClient Invoke-SignInRequest {
            [pscustomobject]@{ Uri = $Uri; StatusCode = 200; Location = $null; Content = (& $script:EntraPage 'ConvergedSignIn' '/t/login' '50126' 'Your account or password is incorrect.') }
        }

        { Complete-EntraIdSignIn -Session $script:Session -Response $script:AtEntra -UserName 'u@contoso.onmicrosoft.com' -Password 'wrong' } |
            Should -Throw '*refused the sign-in for u@contoso.onmicrosoft.com*50126*incorrect*'
    }

    It 'names a page it does not know as Entra ID''s, never as JIM''s fault' {
        Mock -ModuleName AdfsSignInClient Invoke-SignInRequest {
            [pscustomobject]@{ Uri = $Uri; StatusCode = 200; Location = $null; Content = (& $script:EntraPage 'ConvergedProofUpRedirect' '/t/proofup') }
        }

        { Complete-EntraIdSignIn -Session $script:Session -Response $script:AtEntra -UserName 'u@contoso.onmicrosoft.com' -Password 'pw' } |
            Should -Throw "*Entra ID's sign-in did not go as expected*ConvergedProofUpRedirect*"
    }
}

Describe 'Complete-IdentityProviderSignIn' {
    It 'signs in on AD FS''s page and on Entra ID''s, and refuses any other' {
        Mock -ModuleName AdfsSignInClient Complete-AdfsSignIn { 'adfs' }
        Mock -ModuleName AdfsSignInClient Complete-EntraIdSignIn { 'entra' }
        $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
        $page = { param($content) [pscustomobject]@{ Uri = [uri]'https://idp.example.com/'; StatusCode = 200; Location = $null; Content = $content } }

        Complete-IdentityProviderSignIn -Session $session -Response (& $page $script:AdfsLoginPage) -UserName 'u' -Password 'p' | Should -Be 'adfs'
        Complete-IdentityProviderSignIn -Session $session -Response (& $page "<script>//<![CDATA[`n`$Config={`"pgid`":`"ConvergedSignIn`",`"urlPost`":`"/t/login`"};`n//]]></script>") -UserName 'u' -Password 'p' | Should -Be 'entra'
        { Complete-IdentityProviderSignIn -Session $session -Response (& $page '<html></html>') -UserName 'u' -Password 'p' } | Should -Throw '*sign-in page*'
    }
}
