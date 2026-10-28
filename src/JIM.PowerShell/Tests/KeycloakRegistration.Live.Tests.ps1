# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Register-JIMIdentityProvider -Provider Keycloak against a real Keycloak (#2364).

.DESCRIPTION
    KeycloakRegistration.Tests.ps1 proves the plan without Keycloak. This proves that what the plan asks for is what
    Keycloak's admin REST API does, and that the registration it makes signs people in: a person signs in through each
    client with the authorisation code flow and PKCE, and the access token carries the audience JIM requires.

    Each run makes a realm of its own and deletes it afterwards. Skipped unless JIM_TEST_KEYCLOAK_URL names a Keycloak
    (such as http://localhost:18080) whose master realm administrator is JIM_TEST_KEYCLOAK_ADMIN, with the password in
    JIM_TEST_KEYCLOAK_ADMIN_PASSWORD. The keycloak-registration-tests job in ci.yml starts one.

    It also proves that the development realm, .devcontainer/keycloak/jim-realm.json, registers JIM exactly as the
    command does, so development and CI sign in with the configuration a customer gets: the command, run against it
    with development's values from .env.example, finds nothing to change. Skipped unless JIM_TEST_KEYCLOAK_DEV_REALM
    names the realm that file was imported as.
#>

BeforeDiscovery {
    $script:KeycloakAvailable = [bool]$env:JIM_TEST_KEYCLOAK_URL
    $script:DevRealmAvailable = [bool]$env:JIM_TEST_KEYCLOAK_URL -and [bool]$env:JIM_TEST_KEYCLOAK_DEV_REALM
}

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    $script:JimUrl = 'https://jim.example.com'

    function script:Get-AdminToken {
        (Invoke-RestMethod -Method Post -Uri "$($env:JIM_TEST_KEYCLOAK_URL)/realms/master/protocol/openid-connect/token" -Body @{
            grant_type = 'password'; client_id = 'admin-cli'
            username = $env:JIM_TEST_KEYCLOAK_ADMIN; password = $env:JIM_TEST_KEYCLOAK_ADMIN_PASSWORD
        }).access_token
    }

    function script:Invoke-Admin {
        param([string]$Method = 'Get', [string]$Path, [object]$Body)
        $request = @{
            Method = $Method; Uri = "$($env:JIM_TEST_KEYCLOAK_URL)/admin/realms$Path"
            Headers = @{ Authorization = "Bearer $(Get-AdminToken)" }
        }
        if ($PSBoundParameters.ContainsKey('Body')) {
            $request.Body = ConvertTo-Json -InputObject $Body -Depth 10
            $request.ContentType = 'application/json'
        }
        $response = Invoke-RestMethod @request
        $response
    }

    function script:Get-Client {
        param([string]$ClientId)
        Invoke-Admin -Path "/$($script:Realm)/clients?clientId=$ClientId" | Where-Object clientId -ceq $ClientId
    }

    function script:Register {
        param([hashtable]$With = @{})
        $parameters = @{
            Provider = 'Keycloak'; Url = $script:JimUrl; Authority = $script:Authority; Credential = $script:AdminCredential
            Confirm = $false
        }
        foreach ($key in $With.Keys) { $parameters[$key] = $With[$key] }
        Register-JIMIdentityProvider @parameters
    }

    function script:ConvertFrom-Jwt {
        param([string]$Token)
        $payload = $Token.Split('.')[1].Replace('-', '+').Replace('_', '/')
        $payload += '=' * ((4 - $payload.Length % 4) % 4)
        [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
    }

    # Signs the test person in through a client, as a browser would: the authorisation code flow with PKCE, the
    # login form filled in, and the code exchanged at the token endpoint. Returns the token response.
    function script:Invoke-SignIn {
        param(
            [string]$ClientId, [string]$RedirectUri, [string]$Scope, [securestring]$ClientSecret,
            [string]$Authority = $script:Authority, [string]$UserName = $script:PersonName, [string]$Password = $script:PersonPassword,
            # The page a browser makes the token request from; Keycloak must allow it (CORS) or the browser drops the answer.
            [string]$Origin
        )

        $verifier = -join ((1..64) | ForEach-Object { [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~' | Get-Random })
        $hash = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::ASCII.GetBytes($verifier))
        $challenge = [Convert]::ToBase64String($hash).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $query = @{
            client_id = $ClientId; response_type = 'code'; scope = $Scope; redirect_uri = $RedirectUri; state = 'jim-test'
            code_challenge = $challenge; code_challenge_method = 'S256'
        }
        $queryString = ($query.GetEnumerator() | ForEach-Object { "$($_.Key)=$([uri]::EscapeDataString($_.Value))" }) -join '&'

        $login = Invoke-WebRequest -Uri "$Authority/protocol/openid-connect/auth?$queryString" -MaximumRedirection 0 -SkipHttpErrorCheck
        $login.StatusCode | Should -Be 200 -Because "Keycloak shows its login form, not $($login.Headers.Location) $($login.Content)"
        $action = [System.Net.WebUtility]::HtmlDecode([regex]::Match($login.Content, 'id="kc-form-login"[^>]*action="([^"]+)"').Groups[1].Value)
        if (-not $action) { $action = [System.Net.WebUtility]::HtmlDecode([regex]::Match($login.Content, '<form[^>]*action="([^"]+)"').Groups[1].Value) }
        $action | Should -Not -BeNullOrEmpty -Because 'Keycloak shows its login form'

        # Posted with HttpClient: Invoke-WebRequest throws on the redirect even with -MaximumRedirection 0. Keycloak
        # marks its login cookies Secure, which a browser still sends to http://localhost but .NET's cookie store does
        # not over plain HTTP, so they go back as a header.
        $cookies = @($login.Headers['Set-Cookie'] | ForEach-Object { ($_ -split ';')[0] }) -join '; '
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.UseCookies = $false
        $client = [System.Net.Http.HttpClient]::new($handler)
        try {
            $form = [System.Collections.Generic.Dictionary[string, string]]::new()
            $form['username'] = $UserName
            $form['password'] = $Password
            $form['credentialId'] = ''
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $action)
            $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($form)
            $null = $request.Headers.TryAddWithoutValidation('Cookie', $cookies)
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
            $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
        finally {
            $client.Dispose()
        }
        $page = ([regex]::Replace($content, '<[^>]+>', ' ') -replace '\s+', ' ').Trim()
        [int]$response.StatusCode | Should -Be 302 -Because "Keycloak accepts the sign-in and returns to $RedirectUri, not: $($page.Substring(0, [Math]::Min(600, $page.Length)))"
        $location = $response.Headers.Location.OriginalString
        $location | Should -BeLike "$RedirectUri*"
        $code = [regex]::Match($location, '[?&]code=([^&]+)').Groups[1].Value

        $body = @{
            grant_type = 'authorization_code'; code = [uri]::UnescapeDataString($code); redirect_uri = $RedirectUri
            client_id = $ClientId; code_verifier = $verifier
        }
        if ($ClientSecret) { $body.client_secret = [System.Net.NetworkCredential]::new('', $ClientSecret).Password }
        if (-not $Origin) {
            return Invoke-RestMethod -Method Post -Uri "$Authority/protocol/openid-connect/token" -Body $body
        }

        $tokenResponse = Invoke-WebRequest -Method Post -Uri "$Authority/protocol/openid-connect/token" -Body $body -Headers @{ Origin = $Origin }
        "$($tokenResponse.Headers['Access-Control-Allow-Origin'])" | Should -Be $Origin -Because 'Keycloak lets a browser on that page read the tokens'
        $tokenResponse.Content | ConvertFrom-Json
    }

    if (-not $env:JIM_TEST_KEYCLOAK_URL) { return }

    # A realm of its own, a person to sign in, and a client that is not JIM's.
    $script:Realm = "jim-registration-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $script:Authority = "$($env:JIM_TEST_KEYCLOAK_URL)/realms/$($script:Realm)"
    $script:AdminCredential = [pscredential]::new($env:JIM_TEST_KEYCLOAK_ADMIN, (ConvertTo-SecureString $env:JIM_TEST_KEYCLOAK_ADMIN_PASSWORD -AsPlainText -Force))
    $script:PersonName = 'alice'
    $script:PersonPassword = [guid]::NewGuid().ToString()

    Invoke-Admin -Method Post -Path '' -Body @{ realm = $script:Realm; enabled = $true }
    Invoke-Admin -Method Post -Path "/$($script:Realm)/users" -Body @{
        username = $script:PersonName; enabled = $true; emailVerified = $true
        firstName = 'Alice'; lastName = 'Smith'; email = 'alice@example.com'
        credentials = @(@{ type = 'password'; value = $script:PersonPassword; temporary = $false })
    }
    $script:PersonId = (Invoke-Admin -Path "/$($script:Realm)/users?username=$($script:PersonName)&exact=true")[0].id
    Invoke-Admin -Method Post -Path "/$($script:Realm)/clients" -Body @{
        clientId = 'another-app'; publicClient = $true; standardFlowEnabled = $true; redirectUris = @('http://localhost:9000/*')
    }
}

AfterAll {
    if ($env:JIM_TEST_KEYCLOAK_URL -and $script:Realm) {
        Invoke-Admin -Method Delete -Path "/$($script:Realm)"
    }
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Register-JIMIdentityProvider -Provider Keycloak against Keycloak' -Skip:(-not $script:KeycloakAvailable) {

    It 'Registers JIM in an empty realm and returns what JIM needs' {
        $script:First = Register

        $script:First.Authority | Should -Be $script:Authority
        $script:First.ClientId | Should -Be 'jim'
        $script:First.PublicClientId | Should -Be 'jim-powershell'
        $script:First.ApiScope | Should -Be 'jim-api'
        $script:First.ClaimType | Should -Be 'sub'
        $script:First.Changes | Should -Not -BeNullOrEmpty

        $portal = Get-Client 'jim'
        $stored = (Invoke-Admin -Path "/$($script:Realm)/clients/$($portal.id)/client-secret").value
        [System.Net.NetworkCredential]::new('', $script:First.ClientSecret).Password | Should -Be $stored
    }

    It 'Changes nothing on a second run, and returns no secret' {
        $second = Register

        $second.Changes | Should -BeNullOrEmpty
        $second.ClientSecret | Should -BeNullOrEmpty
    }

    It 'Signs a person in to the portal, with JIM''s audience and the person''s profile in the tokens' {
        $tokens = Invoke-SignIn -ClientId 'jim' -RedirectUri "$($script:JimUrl)/signin-oidc" -Scope 'openid profile email jim-api' -ClientSecret $script:First.ClientSecret

        $access = ConvertFrom-Jwt $tokens.access_token
        @($access.aud) | Should -Contain 'jim'
        $access.sub | Should -Be $script:PersonId

        $id = ConvertFrom-Jwt $tokens.id_token
        $id.sub | Should -Be $script:PersonId
        $id.name | Should -Be 'Alice Smith'
        $id.email | Should -Be 'alice@example.com'
    }

    It 'Signs the same person in to the PowerShell module, with a refresh token' {
        $tokens = Invoke-SignIn -ClientId 'jim-powershell' -RedirectUri 'http://localhost:8403/callback/' -Scope 'openid profile jim-api offline_access'

        $access = ConvertFrom-Jwt $tokens.access_token
        @($access.aud) | Should -Contain 'jim'
        $access.sub | Should -Be $script:PersonId
        $tokens.refresh_token | Should -Not -BeNullOrEmpty
    }

    It 'Signs the same person in to the API reference''s Try It, from JIM''s page, with JIM''s audience (#2594)' {
        $tokens = Invoke-SignIn -ClientId 'jim-powershell' -RedirectUri "$($script:JimUrl)/api/reference" -Scope 'openid jim-api' `
            -Origin $script:JimUrl

        $access = ConvertFrom-Jwt $tokens.access_token
        @($access.aud) | Should -Contain 'jim'
        $access.sub | Should -Be $script:PersonId
    }

    It 'Reports drift with -WhatIf and changes nothing, then repairs it' {
        $portal = Invoke-Admin -Path "/$($script:Realm)/clients/$((Get-Client 'jim').id)"
        $portal.redirectUris = @("$($script:JimUrl)/*")
        $portal.directAccessGrantsEnabled = $true
        Invoke-Admin -Method Put -Path "/$($script:Realm)/clients/$($portal.id)" -Body $portal
        $scope = Invoke-Admin -Path "/$($script:Realm)/client-scopes" | Where-Object name -eq 'jim-api'
        $another = Get-Client 'another-app'
        Invoke-Admin -Method Put -Path "/$($script:Realm)/clients/$($another.id)/optional-client-scopes/$($scope.id)"
        Invoke-Admin -Method Put -Path "/$($script:Realm)/default-optional-client-scopes/$($scope.id)"

        $preview = Register -With @{ WhatIf = $true }
        $preview | Should -BeNullOrEmpty
        (Invoke-Admin -Path "/$($script:Realm)/clients/$($portal.id)").redirectUris | Should -Be @("$($script:JimUrl)/*")

        $repair = Register
        $repair.Changes | Should -HaveCount 3
        $repair.Changes | Should -Contain "jim-api: Stop adding it to every new client as a realm optional scope"
        $repair.Changes | Where-Object { $_ -like 'another-app: Remove jim-api*' } | Should -Not -BeNullOrEmpty

        $repaired = Invoke-Admin -Path "/$($script:Realm)/clients/$($portal.id)"
        $repaired.redirectUris | Should -Be @("$($script:JimUrl)/signin-oidc")
        $repaired.directAccessGrantsEnabled | Should -BeFalse
        @((Invoke-Admin -Path "/$($script:Realm)/clients/$($another.id)/optional-client-scopes").name) | Should -Not -Contain 'jim-api'
        @((Invoke-Admin -Path "/$($script:Realm)/default-optional-client-scopes").name) | Should -Not -Contain 'jim-api'
        (Register).Changes | Should -BeNullOrEmpty
    }

    It 'Replaces the client secret when asked, and the new one signs people in' {
        $reset = Register -With @{ ResetClientSecret = $true }

        $reset.Changes | Should -Be @('jim: Replace the client secret')
        $newSecret = [System.Net.NetworkCredential]::new('', $reset.ClientSecret).Password
        $newSecret | Should -Not -Be ([System.Net.NetworkCredential]::new('', $script:First.ClientSecret).Password)

        $tokens = Invoke-SignIn -ClientId 'jim' -RedirectUri "$($script:JimUrl)/signin-oidc" -Scope 'openid jim-api' -ClientSecret $reset.ClientSecret
        @((ConvertFrom-Jwt $tokens.access_token).aud) | Should -Contain 'jim'
    }

    It 'Runs as a service account that administers only JIM''s realm' {
        Invoke-Admin -Method Post -Path "/$($script:Realm)/clients" -Body @{
            clientId = 'jim-registrar'; publicClient = $false; serviceAccountsEnabled = $true; standardFlowEnabled = $false
        }
        $registrar = Get-Client 'jim-registrar'
        $serviceUser = Invoke-Admin -Path "/$($script:Realm)/clients/$($registrar.id)/service-account-user"
        $management = Get-Client 'realm-management'
        $realmAdmin = Invoke-Admin -Path "/$($script:Realm)/clients/$($management.id)/roles/realm-admin"
        Invoke-Admin -Method Post -Path "/$($script:Realm)/users/$($serviceUser.id)/role-mappings/clients/$($management.id)" -Body @($realmAdmin)
        $secret = (Invoke-Admin -Path "/$($script:Realm)/clients/$($registrar.id)/client-secret").value
        $serviceCredential = [pscredential]::new('jim-registrar', (ConvertTo-SecureString $secret -AsPlainText -Force))

        $result = Register-JIMIdentityProvider -Provider Keycloak -Url $script:JimUrl -Authority $script:Authority `
            -ClientCredential $serviceCredential -AdminRealm $script:Realm -Confirm:$false

        $result.Changes | Should -BeNullOrEmpty
    }
}

Describe 'The development realm' -Skip:(-not $script:DevRealmAvailable) {

    BeforeAll {
        # Development's sign-in settings, as .env.example gives them.
        $script:DevSettings = @{}
        foreach ($line in Get-Content (Join-Path $PSScriptRoot '..' '..' '..' '.env.example')) {
            if ($line -match '^(JIM_SSO_[A-Z_]+)=(.*)$') { $script:DevSettings[$Matches[1]] = $Matches[2].Trim('"') }
        }
        $script:DevAuthority = "$($env:JIM_TEST_KEYCLOAK_URL)/realms/$($env:JIM_TEST_KEYCLOAK_DEV_REALM)"
        # The addresses development serves JIM on: docker-compose.override.yml's port, and .vscode/launch.json's two.
        $script:DevUrls = @('http://localhost:5200', 'https://localhost:7000')
    }

    It 'Registers JIM exactly as the command does, with the values .env.example gives JIM' {
        $result = Register-JIMIdentityProvider -Provider Keycloak -Url $script:DevUrls -Authority $script:DevAuthority `
            -Credential $script:AdminCredential -ClientId $script:DevSettings.JIM_SSO_CLIENT_ID `
            -ClientSecret (ConvertTo-SecureString $script:DevSettings.JIM_SSO_SECRET -AsPlainText -Force) -Confirm:$false

        $result.Changes | Should -BeNullOrEmpty -Because 'jim-realm.json should hold what Register-JIMIdentityProvider creates; update it to match'
        $result.ApiScope | Should -Be $script:DevSettings.JIM_SSO_API_SCOPE
        $result.ClaimType | Should -Be $script:DevSettings.JIM_SSO_CLAIM_TYPE
        $result.MvAttribute | Should -Be $script:DevSettings.JIM_SSO_MV_ATTRIBUTE
    }

    It 'Signs development''s administrator in to the portal as JIM''s initial administrator' {
        $tokens = Invoke-SignIn -Authority $script:DevAuthority -UserName 'admin' -Password 'admin' `
            -ClientId $script:DevSettings.JIM_SSO_CLIENT_ID -RedirectUri 'http://localhost:5200/signin-oidc' -Scope 'openid profile email jim-api' `
            -ClientSecret (ConvertTo-SecureString $script:DevSettings.JIM_SSO_SECRET -AsPlainText -Force)

        $access = ConvertFrom-Jwt $tokens.access_token
        @($access.aud) | Should -Contain $script:DevSettings.JIM_SSO_CLIENT_ID
        $access.sub | Should -Be $script:DevSettings.JIM_SSO_INITIAL_ADMIN
    }
}
