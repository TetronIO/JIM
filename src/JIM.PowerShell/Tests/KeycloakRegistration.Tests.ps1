# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for registering JIM with Keycloak (#2364): Register-JIMIdentityProvider -Provider Keycloak, the plan
    it makes from what the realm already holds, and how it reaches Keycloak's admin REST API. Keycloak itself is never
    called here.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMKeycloakRegistrationPlan' {

    BeforeAll {
        # A realm as Keycloak 26 creates it, with nothing of JIM's in it.
        $script:NewEmptyRealm = {
            [PSCustomObject]@{
                Clients              = @([PSCustomObject]@{
                    ClientId = 'account'; PublicClient = $true; Enabled = $true; StandardFlowEnabled = $true
                    ImplicitFlowEnabled = $false; DirectAccessGrantsEnabled = $false; ServiceAccountsEnabled = $false
                    RedirectUris = @('/realms/jim/account/*'); PostLogoutRedirectUris = @(); Pkce = ''
                    DefaultClientScopes = @('basic', 'profile', 'email', 'roles', 'web-origins', 'acr')
                    OptionalClientScopes = @('offline_access', 'address', 'phone', 'microprofile-jwt')
                })
                ClientScopes         = @('basic', 'profile', 'email', 'roles', 'web-origins', 'acr', 'offline_access', 'address', 'phone', 'microprofile-jwt' |
                    ForEach-Object { [PSCustomObject]@{ Name = $_; Protocol = 'openid-connect'; AudienceMappers = @() } })
                RealmDefaultScopes   = @('basic', 'profile', 'email', 'roles', 'web-origins', 'acr')
                RealmOptionalScopes  = @('offline_access', 'address', 'phone', 'microprofile-jwt')
            }
        }

        # The same realm once JIM is registered in it as it should be.
        $script:NewCompleteRealm = {
            $realm = & $script:NewEmptyRealm
            $realm.ClientScopes += [PSCustomObject]@{
                Name = 'jim-api'; Protocol = 'openid-connect'
                AudienceMappers = @([PSCustomObject]@{
                    Name = 'jim-api-audience'; IncludedClientAudience = 'jim'; AccessTokenClaim = 'true'; IdTokenClaim = 'false'
                })
            }
            $realm.Clients += [PSCustomObject]@{
                ClientId = 'jim'; PublicClient = $false; Enabled = $true; StandardFlowEnabled = $true
                ImplicitFlowEnabled = $false; DirectAccessGrantsEnabled = $false; ServiceAccountsEnabled = $false
                RedirectUris = @('https://jim.example.com/signin-oidc')
                PostLogoutRedirectUris = @('https://jim.example.com/signout-callback-oidc'); Pkce = 'S256'
                DefaultClientScopes = @('basic', 'profile', 'email', 'roles', 'web-origins', 'acr')
                OptionalClientScopes = @('offline_access', 'address', 'phone', 'microprofile-jwt', 'jim-api')
            }
            $realm.Clients += [PSCustomObject]@{
                ClientId = 'jim-powershell'; PublicClient = $true; Enabled = $true; StandardFlowEnabled = $true
                ImplicitFlowEnabled = $false; DirectAccessGrantsEnabled = $false; ServiceAccountsEnabled = $false
                RedirectUris = @(8400..8409 | ForEach-Object { "http://localhost:$_/callback/" }) + 'https://jim.example.com/api/reference/'
                WebOrigins = @('https://jim.example.com')
                PostLogoutRedirectUris = @(); Pkce = 'S256'
                DefaultClientScopes = @('basic', 'profile', 'email', 'roles', 'web-origins', 'acr')
                OptionalClientScopes = @('offline_access', 'address', 'phone', 'microprofile-jwt', 'jim-api')
            }
            $realm
        }

        $script:Plan = {
            param($Current, [switch]$ResetClientSecret, [string[]]$Url = 'https://jim.example.com', [string]$ClientSecret)
            InModuleScope JIM -Parameters @{ Current = $Current; Reset = [bool]$ResetClientSecret; Url = $Url; Secret = $ClientSecret } {
                param($Current, $Reset, $Url, $Secret)
                $given = if ($Secret) { @{ ClientSecret = (ConvertTo-SecureString $Secret -AsPlainText -Force) } } else { @{} }
                Get-JIMKeycloakRegistrationPlan -Url $Url -ClientId 'jim' -PublicClientId 'jim-powershell' -Current $Current -ResetClientSecret:$Reset @given
            }
        }
    }

    It 'Creates the scope and both clients in a realm that has none of them' {
        $plan = & $script:Plan (& $script:NewEmptyRealm)

        @($plan.Changes.Action | Select-Object -Unique) | Should -Be @('NewClientScope', 'NewClient', 'AddClientScope')
        $plan.ClientId | Should -Be 'jim'
        $plan.PublicClientId | Should -Be 'jim-powershell'
        $plan.ApiScope | Should -Be 'jim-api'

        $scope = $plan.Changes | Where-Object Action -eq 'NewClientScope'
        $scope.Arguments.Name | Should -Be 'jim-api'
        $scope.Arguments.AudienceClientId | Should -Be 'jim'
    }

    It 'Makes the portal a confidential client, redirected to JIM''s sign-in and sign-out, with PKCE' {
        $plan = & $script:Plan (& $script:NewEmptyRealm)

        $portal = ($plan.Changes | Where-Object { $_.Action -eq 'NewClient' -and $_.Target -eq 'jim' }).Arguments.Properties
        $portal.publicClient | Should -BeFalse
        $portal.standardFlowEnabled | Should -BeTrue
        $portal.implicitFlowEnabled | Should -BeFalse
        $portal.directAccessGrantsEnabled | Should -BeFalse
        $portal.serviceAccountsEnabled | Should -BeFalse
        $portal.redirectUris | Should -Be @('https://jim.example.com/signin-oidc')
        $portal.PostLogoutRedirectUris | Should -Be @('https://jim.example.com/signout-callback-oidc')
        $portal.Pkce | Should -Be 'S256'
    }

    It 'Makes the PowerShell module a public client on the loopback ports 8400 to 8409, and the API reference''s sign-in' {
        $plan = & $script:Plan (& $script:NewEmptyRealm)

        $module = ($plan.Changes | Where-Object { $_.Action -eq 'NewClient' -and $_.Target -eq 'jim-powershell' }).Arguments.Properties
        $module.publicClient | Should -BeTrue
        $module.directAccessGrantsEnabled | Should -BeFalse
        $module.redirectUris | Should -Be (@(8400..8409 | ForEach-Object { "http://localhost:$_/callback/" }) + 'https://jim.example.com/api/reference/')
        $module.webOrigins | Should -Be @('https://jim.example.com')
        $module.Pkce | Should -Be 'S256'
    }

    It 'Lets Try It sign in from each of JIM''s addresses, with each address''s origin as a web origin (#2594)' {
        $plan = & $script:Plan (& $script:NewEmptyRealm) -Url 'https://jim.example.com:8443/jim/', 'http://localhost:5200'

        $module = ($plan.Changes | Where-Object { $_.Action -eq 'NewClient' -and $_.Target -eq 'jim-powershell' }).Arguments.Properties
        $module.redirectUris | Should -Contain 'https://jim.example.com:8443/jim/api/reference/'
        $module.redirectUris | Should -Contain 'http://localhost:5200/api/reference/'
        $module.webOrigins | Should -Be @('https://jim.example.com:8443', 'http://localhost:5200')
    }

    It 'Adds Try It''s redirect and origin to a public client registered before it needed them' {
        $realm = & $script:NewCompleteRealm
        $module = $realm.Clients | Where-Object ClientId -eq 'jim-powershell'
        $module.RedirectUris = @(8400..8409 | ForEach-Object { "http://localhost:$_/callback/" })
        $module.WebOrigins = @()

        $change = (& $script:Plan $realm).Changes | Where-Object { $_.Action -eq 'SetClient' -and $_.Target -eq 'jim-powershell' }

        $change.Arguments.Properties.Keys | Sort-Object | Should -Be @('redirectUris', 'webOrigins')
        $change.Description | Should -Match 'https://jim.example.com/api/reference/'
        $change.Description | Should -Match 'web origins'
    }

    It 'Gives each client the scopes JIM signs in with, and jim-api only as optional' {
        $plan = & $script:Plan (& $script:NewEmptyRealm)

        $links = $plan.Changes | Where-Object Action -eq 'AddClientScope' | ForEach-Object { "$($_.Target) $($_.Arguments.Kind) $($_.Arguments.Scope)" }
        $links | Should -Contain 'jim Default basic'
        $links | Should -Contain 'jim Default profile'
        $links | Should -Contain 'jim Default email'
        $links | Should -Contain 'jim Optional jim-api'
        $links | Should -Contain 'jim-powershell Default basic'
        $links | Should -Contain 'jim-powershell Optional jim-api'
        $links | Should -Contain 'jim-powershell Optional offline_access'
        $links | Should -Not -Contain 'jim Optional offline_access'
        $links | Where-Object { $_ -like '* Default jim-api' } | Should -BeNullOrEmpty
    }

    It 'Leaves out a built-in scope the realm does not have, as on Keycloak before 25 with no basic scope' {
        $realm = & $script:NewEmptyRealm
        $realm.ClientScopes = @($realm.ClientScopes | Where-Object Name -ne 'basic')

        $plan = & $script:Plan $realm

        $plan.Changes | Where-Object { $_.Arguments.Scope -eq 'basic' } | Should -BeNullOrEmpty
    }

    It 'Finds nothing to do when the registration is already right' {
        $plan = & $script:Plan (& $script:NewCompleteRealm)

        $plan.Changes | Should -BeNullOrEmpty
    }

    It 'Ignores the order of redirect URIs' {
        $realm = & $script:NewCompleteRealm
        $module = $realm.Clients | Where-Object ClientId -eq 'jim-powershell'
        $module.RedirectUris = @($module.RedirectUris | Sort-Object -Descending)

        (& $script:Plan $realm).Changes | Should -BeNullOrEmpty
    }

    It 'Corrects a client that has drifted without recreating it, naming each difference' {
        $realm = & $script:NewCompleteRealm
        $portal = $realm.Clients | Where-Object ClientId -eq 'jim'
        $portal.RedirectUris = @('https://jim.example.com/*')
        $portal.PostLogoutRedirectUris = @()
        $portal.DirectAccessGrantsEnabled = $true
        $portal.Pkce = ''

        $plan = & $script:Plan $realm

        $plan.Changes | Should -HaveCount 1
        $change = $plan.Changes[0]
        $change.Action | Should -Be 'SetClient'
        $change.Target | Should -Be 'jim'
        $change.Arguments.Properties.Keys | Sort-Object | Should -Be @('directAccessGrantsEnabled', 'Pkce', 'PostLogoutRedirectUris', 'redirectUris')
        $change.Description | Should -Match 'redirect URIs'
        $change.Description | Should -Match 'PKCE'
    }

    It 'Turns a public portal client back into a confidential one' {
        $realm = & $script:NewCompleteRealm
        ($realm.Clients | Where-Object ClientId -eq 'jim').PublicClient = $true

        $plan = & $script:Plan $realm

        $plan.Changes[0].Arguments.Properties.publicClient | Should -BeFalse
    }

    It 'Moves jim-api from a default scope to an optional one' {
        $realm = & $script:NewCompleteRealm
        $portal = $realm.Clients | Where-Object ClientId -eq 'jim'
        $portal.OptionalClientScopes = @($portal.OptionalClientScopes | Where-Object { $_ -ne 'jim-api' })
        $portal.DefaultClientScopes += 'jim-api'

        $plan = & $script:Plan $realm

        @($plan.Changes | ForEach-Object { "$($_.Action) $($_.Target) $($_.Arguments.Kind) $($_.Arguments.Scope)" }) |
            Should -Be @('RemoveClientScope jim Default jim-api', 'AddClientScope jim Optional jim-api')
    }

    It 'Removes jim-api from every other client, and from the realm''s default scopes' {
        $realm = & $script:NewCompleteRealm
        ($realm.Clients | Where-Object ClientId -eq 'account').OptionalClientScopes += 'jim-api'
        $realm.RealmOptionalScopes += 'jim-api'

        $plan = & $script:Plan $realm

        @($plan.Changes | ForEach-Object { "$($_.Action) $($_.Target) $($_.Arguments.Kind)" }) |
            Should -Be @('RemoveRealmDefaultScope jim-api Optional', 'RemoveClientScope account Optional')
    }

    It 'Points the audience back at the portal client when it names another' {
        $realm = & $script:NewCompleteRealm
        ($realm.ClientScopes | Where-Object Name -eq 'jim-api').AudienceMappers[0].IncludedClientAudience = 'jim-web'

        $plan = & $script:Plan $realm

        $plan.Changes | Should -HaveCount 1
        $plan.Changes[0].Action | Should -Be 'SetAudienceMapper'
        $plan.Changes[0].Arguments.AudienceClientId | Should -Be 'jim'
    }

    It 'Adds the audience when jim-api has none, and puts it in the access token only' {
        $realm = & $script:NewCompleteRealm
        ($realm.ClientScopes | Where-Object Name -eq 'jim-api').AudienceMappers = @()

        $plan = & $script:Plan $realm

        $plan.Changes | Should -HaveCount 1
        $plan.Changes[0].Action | Should -Be 'NewAudienceMapper'
    }

    It 'Corrects an audience that is missing from the access token' {
        $realm = & $script:NewCompleteRealm
        ($realm.ClientScopes | Where-Object Name -eq 'jim-api').AudienceMappers[0].AccessTokenClaim = 'false'

        (& $script:Plan $realm).Changes[0].Action | Should -Be 'SetAudienceMapper'
    }

    It 'Refuses to take over a jim-api scope that is not OpenID Connect' {
        $realm = & $script:NewCompleteRealm
        ($realm.ClientScopes | Where-Object Name -eq 'jim-api').Protocol = 'saml'

        { & $script:Plan $realm } | Should -Throw "*jim-api*saml*"
    }

    It 'Plans a new client secret only when asked, and never for a client it is creating' {
        (& $script:Plan (& $script:NewCompleteRealm)).Changes | Should -BeNullOrEmpty

        $reset = & $script:Plan (& $script:NewCompleteRealm) -ResetClientSecret
        @($reset.Changes.Action) | Should -Be @('ResetClientSecret')

        $new = & $script:Plan (& $script:NewEmptyRealm) -ResetClientSecret
        $new.Changes.Action | Should -Not -Contain 'ResetClientSecret'
    }

    It 'Keeps JIM''s port when it is not 443, and drops a trailing slash' {
        $plan = & $script:Plan (& $script:NewEmptyRealm) -Url 'https://jim.example.com:8443/'

        $portal = ($plan.Changes | Where-Object { $_.Action -eq 'NewClient' -and $_.Target -eq 'jim' }).Arguments.Properties
        $portal.redirectUris | Should -Be @('https://jim.example.com:8443/signin-oidc')
        $portal.PostLogoutRedirectUris | Should -Be @('https://jim.example.com:8443/signout-callback-oidc')
    }

    It 'Redirects the portal to each of JIM''s addresses' {
        $plan = & $script:Plan (& $script:NewEmptyRealm) -Url 'http://localhost:5200', 'https://localhost:7000/'

        $portal = ($plan.Changes | Where-Object { $_.Action -eq 'NewClient' -and $_.Target -eq 'jim' }).Arguments.Properties
        $portal.redirectUris | Should -Be @('http://localhost:5200/signin-oidc', 'https://localhost:7000/signin-oidc')
        $portal.PostLogoutRedirectUris | Should -Be @('http://localhost:5200/signout-callback-oidc', 'https://localhost:7000/signout-callback-oidc')
    }

    It 'Creates the portal with the client secret it is given' {
        $plan = & $script:Plan (& $script:NewEmptyRealm) -ClientSecret 'given-secret'

        $portal = ($plan.Changes | Where-Object { $_.Action -eq 'NewClient' -and $_.Target -eq 'jim' }).Arguments
        [System.Net.NetworkCredential]::new('', $portal.Secret).Password | Should -Be 'given-secret'
        ($plan.Changes | Where-Object { $_.Action -eq 'NewClient' -and $_.Target -eq 'jim-powershell' }).Arguments.Secret | Should -BeNullOrEmpty
    }

    It 'Sets the client secret it is given only when the portal has another' {
        $realm = & $script:NewCompleteRealm
        $realm.Clients | Where-Object ClientId -eq 'jim' | Add-Member -NotePropertyName Secret -NotePropertyValue 'given-secret'
        (& $script:Plan $realm -ClientSecret 'given-secret').Changes | Should -BeNullOrEmpty

        $plan = & $script:Plan $realm -ClientSecret 'another-secret'
        @($plan.Changes.Action) | Should -Be @('SetClientSecret')
        $plan.Changes[0].Target | Should -Be 'jim'
        $plan.Changes[0].Description | Should -Be 'Set the client secret to the one given'
        [System.Net.NetworkCredential]::new('', $plan.Changes[0].Arguments.Secret).Password | Should -Be 'another-secret'
    }
}

Describe 'ConvertTo-JIMKeycloakAuthority' {

    It 'Splits an authority into the server and the realm' {
        InModuleScope JIM {
            $authority = ConvertTo-JIMKeycloakAuthority -Authority 'https://sso.example.com/realms/jim/'

            $authority.ServerUrl | Should -Be 'https://sso.example.com'
            $authority.Realm | Should -Be 'jim'
            $authority.Authority | Should -Be 'https://sso.example.com/realms/jim'
        }
    }

    It 'Keeps a path Keycloak is served under, as older installations use /auth' {
        InModuleScope JIM {
            $authority = ConvertTo-JIMKeycloakAuthority -Authority 'https://sso.example.com/auth/realms/Tetron'

            $authority.ServerUrl | Should -Be 'https://sso.example.com/auth'
            $authority.Realm | Should -Be 'Tetron'
        }
    }

    It 'Refuses an address that names no realm' {
        InModuleScope JIM {
            { ConvertTo-JIMKeycloakAuthority -Authority 'https://sso.example.com/' } | Should -Throw '*realms*'
        }
    }

    It 'Refuses plain HTTP except to this computer' {
        InModuleScope JIM {
            { ConvertTo-JIMKeycloakAuthority -Authority 'http://sso.example.com/realms/jim' } | Should -Throw '*https*'
            (ConvertTo-JIMKeycloakAuthority -Authority 'http://localhost:8181/realms/jim').Realm | Should -Be 'jim'
        }
    }
}

Describe 'Get-JIMKeycloakAdminToken' {

    It 'Signs in as an administrator through admin-cli, in the realm given' {
        InModuleScope JIM {
            Mock Invoke-RestMethod { [PSCustomObject]@{ access_token = 'token'; expires_in = 60 } }
            $credential = [pscredential]::new('admin', (ConvertTo-SecureString 'pw' -AsPlainText -Force))

            $token = Get-JIMKeycloakAdminToken -ServerUrl 'https://sso.example.com' -AdminRealm 'master' -Credential $credential

            $token.AccessToken | Should -Be 'token'
            Should -Invoke Invoke-RestMethod -Times 1 -ParameterFilter {
                $Uri -eq 'https://sso.example.com/realms/master/protocol/openid-connect/token' -and
                $Body.grant_type -eq 'password' -and $Body.client_id -eq 'admin-cli' -and
                $Body.username -eq 'admin' -and $Body.password -eq 'pw'
            }
        }
    }

    It 'Signs in as a service account with client credentials' {
        InModuleScope JIM {
            Mock Invoke-RestMethod { [PSCustomObject]@{ access_token = 'token'; expires_in = 300 } }
            $credential = [pscredential]::new('jim-registrar', (ConvertTo-SecureString 's3cret' -AsPlainText -Force))

            $null = Get-JIMKeycloakAdminToken -ServerUrl 'https://sso.example.com' -AdminRealm 'jim' -ClientCredential $credential

            Should -Invoke Invoke-RestMethod -Times 1 -ParameterFilter {
                $Uri -eq 'https://sso.example.com/realms/jim/protocol/openid-connect/token' -and
                $Body.grant_type -eq 'client_credentials' -and $Body.client_id -eq 'jim-registrar' -and $Body.client_secret -eq 's3cret'
            }
        }
    }
}

Describe 'Register-JIMIdentityProvider -Provider Keycloak' {

    BeforeEach {
        InModuleScope JIM {
            $script:Credential = [pscredential]::new('admin', (ConvertTo-SecureString 'pw' -AsPlainText -Force))
            Mock Get-JIMKeycloakAdminToken { [PSCustomObject]@{ AccessToken = 'token'; ExpiresAt = [datetime]::UtcNow.AddMinutes(1) } }
            Mock Get-JIMKeycloakRegistrationState {
                [PSCustomObject]@{
                    Clients = @(); ClientScopes = @('basic', 'profile', 'email', 'offline_access' | ForEach-Object {
                        [PSCustomObject]@{ Name = $_; Protocol = 'openid-connect'; AudienceMappers = @() } })
                    RealmDefaultScopes = @(); RealmOptionalScopes = @()
                }
            }
            Mock Invoke-JIMKeycloakRegistrationChange {
                if ($Change.Action -in 'NewClient', 'ResetClientSecret' -and $Change.Arguments.Properties.publicClient -ne $true) { 'generated-secret' }
            }
        }
    }

    It 'Needs the Keycloak authority and a credential' {
        { Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -ErrorAction Stop } | Should -Throw '*-Authority*'
    }

    It 'Refuses Keycloak''s parameters for AD FS' {
        InModuleScope JIM {
            { Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -ErrorAction Stop } |
                Should -Throw '*AD FS*'
        }
    }

    It 'Accepts JIM on plain HTTP only at this computer''s own address' {
        InModuleScope JIM {
            { Register-JIMIdentityProvider -Provider Keycloak -Url 'http://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -ErrorAction Stop } |
                Should -Throw '*https*'

            $result = Register-JIMIdentityProvider -Provider Keycloak -Url 'http://localhost:5200' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -Confirm:$false
            $result.ClientId | Should -Be 'jim'
        }
    }

    It 'Registers JIM and returns what JIM needs, with the secret kept secure' {
        InModuleScope JIM {
            $result = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim/' -Credential $script:Credential -Confirm:$false

            $result.PSObject.TypeNames | Should -Contain 'JIM.IdentityProviderRegistration'
            $result.Provider | Should -Be 'Keycloak'
            $result.Authority | Should -Be 'https://sso.example.com/realms/jim'
            $result.ClientId | Should -Be 'jim'
            $result.PublicClientId | Should -Be 'jim-powershell'
            $result.ApiScope | Should -Be 'jim-api'
            $result.ClaimType | Should -Be 'sub'
            $result.MvAttribute | Should -Be 'Subject Identifier'
            $result.ClientSecret | Should -BeOfType [securestring]
            [System.Net.NetworkCredential]::new('', $result.ClientSecret).Password | Should -Be 'generated-secret'
            $result.Changes | Should -Not -BeNullOrEmpty
            Should -Invoke Get-JIMKeycloakAdminToken -Times 1 -ParameterFilter { $ServerUrl -eq 'https://sso.example.com' -and $AdminRealm -eq 'master' }
            Should -Invoke Get-JIMKeycloakRegistrationState -Times 1 -ParameterFilter { $Connection.Realm -eq 'jim' }
        }
    }

    It 'Uses the client identifiers it is given' {
        InModuleScope JIM {
            $result = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -ClientId 'jim-web' -PublicClientId 'jim-cli' -Confirm:$false

            $result.ClientId | Should -Be 'jim-web'
            $result.PublicClientId | Should -Be 'jim-cli'
            Should -Invoke Invoke-JIMKeycloakRegistrationChange -ParameterFilter { $Change.Action -eq 'NewClientScope' -and $Change.Arguments.AudienceClientId -eq 'jim-web' }
        }
    }

    It 'Signs in to the administrators'' realm it is given' {
        InModuleScope JIM {
            $null = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -ClientCredential $script:Credential -AdminRealm 'jim' -Confirm:$false

            Should -Invoke Get-JIMKeycloakAdminToken -Times 1 -ParameterFilter { $AdminRealm -eq 'jim' -and $ClientCredential.UserName -eq 'admin' }
        }
    }

    It 'Changes nothing and returns no secret when the registration is already right' {
        InModuleScope JIM {
            Mock Get-JIMKeycloakRegistrationPlan {
                [PSCustomObject]@{ ClientId = 'jim'; PublicClientId = 'jim-powershell'; ApiScope = 'jim-api'; Changes = @() }
            }

            $result = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential

            $result.ClientSecret | Should -BeNullOrEmpty
            $result.Changes | Should -BeNullOrEmpty
            Should -Invoke Invoke-JIMKeycloakRegistrationChange -Times 0
        }
    }

    It 'Reports drift with -WhatIf and changes nothing' {
        InModuleScope JIM {
            $result = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -WhatIf

            Should -Invoke Invoke-JIMKeycloakRegistrationChange -Times 0
            $result | Should -BeNullOrEmpty
        }
    }

    It 'Says how to get a new secret when a later change fails after the secret was made' {
        InModuleScope JIM {
            Mock Invoke-JIMKeycloakRegistrationChange {
                if ($Change.Action -eq 'NewClient' -and -not $Change.Arguments.Properties.publicClient) { 'generated-secret' }
                if ($Change.Action -eq 'AddClientScope') { throw 'Keycloak said no' }
            }
            Mock Write-Warning { }

            { Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -Confirm:$false -ErrorAction Stop } |
                Should -Throw '*Keycloak said no*'

            Should -Invoke Write-Warning -Times 1 -Exactly -ParameterFilter { $Message -match '-ResetClientSecret' }
        }
    }

    It 'Registers each of JIM''s addresses' {
        InModuleScope JIM {
            $null = Register-JIMIdentityProvider -Provider Keycloak -Url 'http://localhost:5200', 'https://localhost:7000' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -Confirm:$false

            Should -Invoke Invoke-JIMKeycloakRegistrationChange -ParameterFilter {
                $Change.Action -eq 'NewClient' -and $Change.Target -eq 'jim' -and
                ($Change.Arguments.Properties.redirectUris -join ' ') -eq 'http://localhost:5200/signin-oidc https://localhost:7000/signin-oidc'
            }
        }
    }

    It 'Refuses any of JIM''s addresses that is not https, or http on this computer' {
        InModuleScope JIM {
            { Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com', 'http://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -ErrorAction Stop } |
                Should -Throw "*'http://jim.example.com'*"
        }
    }

    It 'Uses the client secret it is given, reads the portal''s to compare, and returns it once set' {
        InModuleScope JIM {
            $secret = ConvertTo-SecureString 'given-secret' -AsPlainText -Force
            Mock Invoke-JIMKeycloakRegistrationChange { if ($Change.Action -eq 'NewClient' -and $Change.Arguments.Secret) { 'given-secret' } }

            $result = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -ClientSecret $secret -Confirm:$false

            [System.Net.NetworkCredential]::new('', $result.ClientSecret).Password | Should -Be 'given-secret'
            Should -Invoke Get-JIMKeycloakRegistrationState -Times 1 -ParameterFilter { $SecretOf -eq 'jim' }
        }
    }

    It 'Does not read the portal''s secret when it is not given one' {
        InModuleScope JIM {
            $null = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -Confirm:$false

            Should -Invoke Get-JIMKeycloakRegistrationState -Times 1 -ParameterFilter { -not $SecretOf }
        }
    }

    It 'Refuses a given client secret with -ResetClientSecret, and for AD FS' {
        InModuleScope JIM {
            $secret = ConvertTo-SecureString 'given-secret' -AsPlainText -Force
            { Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -ClientSecret $secret -ResetClientSecret -ErrorAction Stop } |
                Should -Throw '*-ResetClientSecret*'
            { Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential $script:Credential -ClientSecret $secret -ErrorAction Stop } |
                Should -Throw '*-ClientSecret are for Keycloak*'
        }
    }
}

Describe 'Get-JIMKeycloakRegistrationState' {

    It 'Reads the client secret of the client it is asked about, and of no other' {
        InModuleScope JIM {
            Mock Invoke-JIMKeycloakAdminApi {
                switch -Wildcard ($Path) {
                    '/clients' {
                        [PSCustomObject]@{ id = 'u1'; clientId = 'jim'; defaultClientScopes = @(); optionalClientScopes = @(); attributes = [PSCustomObject]@{} }
                        [PSCustomObject]@{ id = 'u2'; clientId = 'other'; defaultClientScopes = @(); optionalClientScopes = @(); attributes = [PSCustomObject]@{} }
                    }
                    '/clients/u1/client-secret' { [PSCustomObject]@{ type = 'secret'; value = 'stored-secret' } }
                    default { }
                }
            }

            $state = Get-JIMKeycloakRegistrationState -Connection @{ Realm = 'jim' } -SecretOf 'jim'

            ($state.Clients | Where-Object ClientId -eq 'jim').Secret | Should -Be 'stored-secret'
            ($state.Clients | Where-Object ClientId -eq 'other').Secret | Should -BeNullOrEmpty
            Should -Invoke Invoke-JIMKeycloakAdminApi -Times 0 -ParameterFilter { $Path -eq '/clients/u2/client-secret' }
        }
    }
}

Describe 'Invoke-JIMKeycloakAdminApi' {

    It 'Hands back each item of a list, which Invoke-RestMethod returns as one array' {
        InModuleScope JIM {
            Mock Invoke-RestMethod { , @([PSCustomObject]@{ clientId = 'account' }, [PSCustomObject]@{ clientId = 'jim' }) }
            $connection = @{
                ServerUrl = 'https://sso.example.com'; Realm = 'jim'; AdminRealm = 'master'
                Token = [PSCustomObject]@{ AccessToken = 'token'; ExpiresAt = [datetime]::UtcNow.AddMinutes(5) }
            }

            $clients = Invoke-JIMKeycloakAdminApi -Connection $connection -Path '/clients' | Where-Object clientId -ceq 'jim'

            @($clients) | Should -HaveCount 1
            Should -Invoke Invoke-RestMethod -Times 1 -ParameterFilter { $Uri -eq 'https://sso.example.com/admin/realms/jim/clients' }
        }
    }

    It 'Signs in again when the token is about to expire' {
        InModuleScope JIM {
            Mock Invoke-RestMethod { }
            Mock Get-JIMKeycloakAdminToken { [PSCustomObject]@{ AccessToken = 'fresh'; ExpiresAt = [datetime]::UtcNow.AddMinutes(1) } }
            $connection = @{
                ServerUrl = 'https://sso.example.com'; Realm = 'jim'; AdminRealm = 'master'
                Token = [PSCustomObject]@{ AccessToken = 'stale'; ExpiresAt = [datetime]::UtcNow.AddSeconds(5) }
            }

            $null = Invoke-JIMKeycloakAdminApi -Connection $connection -Path '/clients'

            Should -Invoke Get-JIMKeycloakAdminToken -Times 1
            Should -Invoke Invoke-RestMethod -Times 1 -ParameterFilter { $Headers.Authorization -eq 'Bearer fresh' }
        }
    }
}
