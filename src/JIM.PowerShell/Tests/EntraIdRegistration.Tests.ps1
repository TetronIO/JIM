# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for registering JIM with Microsoft Entra ID (#2367): Register-JIMIdentityProvider -Provider EntraId, and
    the plan it makes from what the tenant already holds. Microsoft Graph itself is never called here.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force
}

AfterAll {
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
}

Describe 'Get-JIMEntraIdRegistrationPlan' {

    BeforeAll {
        $script:TenantId = '34599ee0-829e-4dd4-8da2-4727a0664d51'
        $script:AppId = '11111111-2222-3333-4444-555555555555'

        # A tenant with nothing of JIM's in it, where the caller may grant consent.
        $script:NewEmptyTenant = {
            [PSCustomObject]@{
                TenantId         = $script:TenantId
                Applications     = @()
                ServicePrincipal = $null
                Consent          = [PSCustomObject]@{ Known = $true; Graph = @(); Api = @() }
                SignedInObjectId = 'aaaaaaaa-0000-0000-0000-000000000001'
            }
        }

        # JIM's registration as it should be, for https://jim.example.com.
        $script:NewRegisteredTenant = {
            $tenant = & $script:NewEmptyTenant
            $tenant.Applications = @([PSCustomObject]@{
                Id                          = 'object-id'
                AppId                       = $script:AppId
                DisplayName                 = 'JIM Identity Management'
                SignInAudience              = 'AzureADMyOrg'
                IdentifierUris              = @("api://$script:AppId")
                WebRedirectUris             = @('https://jim.example.com/signin-oidc', 'https://jim.example.com/signout-callback-oidc')
                SpaRedirectUris             = @('https://jim.example.com/api/reference/')
                PublicClientRedirectUris    = @(8400..8409 | ForEach-Object { "http://localhost:$_/callback/" })
                RequestedAccessTokenVersion = 2
                ApiScopes                   = @([PSCustomObject]@{ Id = 'scope-id'; Value = 'access_as_user'; IsEnabled = $true; Type = 'User' })
                GraphDelegatedScopeIds      = @(
                    '37f7f235-527c-4136-accd-4a02d197296e', '14dad69e-099b-42c9-810b-d002981feec1',
                    '64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0', '7427e0e9-2fba-42fe-b0c0-848c9e6a8182')
                OptionalClaims              = [PSCustomObject]@{ IdToken = @('email'); AccessToken = @('email') }
                PasswordCredentials         = @([PSCustomObject]@{ KeyId = 'key-1'; DisplayName = 'JIM portal' })
            })
            $tenant.ServicePrincipal = [PSCustomObject]@{ Id = 'sp-id' }
            $tenant.Consent = [PSCustomObject]@{ Known = $true; Graph = @('openid', 'profile', 'email', 'offline_access'); Api = @('access_as_user') }
            $tenant
        }

        $script:Plan = {
            param([object]$Current, [switch]$ResetClientSecret, [string[]]$Url = @('https://jim.example.com'))
            InModuleScope JIM -Parameters @{ Current = $Current; Reset = [bool]$ResetClientSecret; Url = $Url } {
                param($Current, $Reset, $Url)
                Get-JIMEntraIdRegistrationPlan -Url $Url -Name 'JIM Identity Management' -Current $Current -ResetClientSecret:$Reset
            }
        }
    }

    It 'Creates the application, its service principal, a client secret and consent in a tenant that has none of them' {
        $plan = & $script:Plan (& $script:NewEmptyTenant)

        @($plan.Changes.Action) | Should -Be @('NewApplication', 'NewServicePrincipal', 'NewClientSecret', 'GrantConsent', 'GrantConsent')
        $plan.ConsentUnknown | Should -BeFalse
    }

    It 'Registers the portal, the PowerShell module and Try It on one application, with version 2 access tokens' {
        $plan = & $script:Plan (& $script:NewEmptyTenant)
        $app = ($plan.Changes | Where-Object Action -eq 'NewApplication').Arguments.Application

        $app.signInAudience | Should -Be 'AzureADMyOrg'
        $app.web.redirectUris | Should -Be @('https://jim.example.com/signin-oidc', 'https://jim.example.com/signout-callback-oidc')
        $app.spa.redirectUris | Should -Be @('https://jim.example.com/api/reference/')
        $app.publicClient.redirectUris | Should -Be @(8400..8409 | ForEach-Object { "http://localhost:$_/callback/" })
        $app.api.requestedAccessTokenVersion | Should -Be 2
        @($app.api.oauth2PermissionScopes | Where-Object { $_.value -eq 'access_as_user' -and $_.isEnabled -and $_.type -eq 'User' }) | Should -HaveCount 1
    }

    It 'Asks for openid, profile, email and offline_access from Microsoft Graph, and nothing more' {
        $plan = & $script:Plan (& $script:NewEmptyTenant)
        $app = ($plan.Changes | Where-Object Action -eq 'NewApplication').Arguments.Application

        $graph = @($app.requiredResourceAccess | Where-Object resourceAppId -eq '00000003-0000-0000-c000-000000000000')
        $graph | Should -HaveCount 1
        @($graph[0].resourceAccess | Where-Object type -eq 'Scope').Count | Should -Be 4
        @($graph[0].resourceAccess | Where-Object type -ne 'Scope') | Should -BeNullOrEmpty
    }

    It 'Grants tenant-wide consent for Graph''s scopes and JIM''s own API scope' {
        $plan = & $script:Plan (& $script:NewEmptyTenant)
        $grants = @($plan.Changes | Where-Object Action -eq 'GrantConsent')

        ($grants | Where-Object { $_.Arguments.Resource -eq 'Graph' }).Arguments.Scopes | Should -Be @('openid', 'profile', 'email', 'offline_access')
        ($grants | Where-Object { $_.Arguments.Resource -eq 'Api' }).Arguments.Scopes | Should -Be @('access_as_user')
    }

    It 'Finds nothing to do when the registration is already right' {
        $plan = & $script:Plan (& $script:NewRegisteredTenant)

        $plan.Changes | Should -BeNullOrEmpty
        $plan.ClientId | Should -Be $script:AppId
        $plan.ApiScope | Should -Be "api://$script:AppId/access_as_user"
    }

    It 'Ignores the order of redirect URIs' {
        $tenant = & $script:NewRegisteredTenant
        $tenant.Applications[0].WebRedirectUris = @('https://jim.example.com/signout-callback-oidc', 'https://jim.example.com/signin-oidc')

        (& $script:Plan $tenant).Changes | Should -BeNullOrEmpty
    }

    It 'Corrects drift without recreating anything, naming each difference' {
        $tenant = & $script:NewRegisteredTenant
        $app = $tenant.Applications[0]
        $app.RequestedAccessTokenVersion = $null
        $app.SpaRedirectUris = @()
        $app.IdentifierUris = @()

        $changes = @((& $script:Plan $tenant).Changes)

        $changes | Should -HaveCount 1
        $changes[0].Action | Should -Be 'SetApplication'
        $changes[0].Description | Should -BeLike '*version 2*'
        $changes[0].Description | Should -BeLike '*api/reference*'
        $changes[0].Description | Should -BeLike "*api://$script:AppId*"
        $changes[0].Arguments.Properties.api.requestedAccessTokenVersion | Should -Be 2
        $changes[0].Arguments.Properties.identifierUris | Should -Be @("api://$script:AppId")
    }

    It 'Adds access_as_user beside the application''s other scopes, keeping them' {
        $tenant = & $script:NewRegisteredTenant
        $tenant.Applications[0].ApiScopes = @([PSCustomObject]@{ Id = 'other-id'; Value = 'reports.read'; IsEnabled = $true; Type = 'User' })
        $tenant.Consent.Api = @()

        $set = (& $script:Plan $tenant).Changes | Where-Object Action -eq 'SetApplication'

        @($set.Arguments.Properties.api.oauth2PermissionScopes.value) | Should -Be @('reports.read', 'access_as_user')
    }

    It 'Keeps the application''s other API permissions when adding Graph''s' {
        $tenant = & $script:NewRegisteredTenant
        $tenant.Applications[0].GraphDelegatedScopeIds = @('37f7f235-527c-4136-accd-4a02d197296e')

        $set = (& $script:Plan $tenant).Changes | Where-Object Action -eq 'SetApplication'

        $graph = $set.Arguments.Properties.requiredResourceAccess | Where-Object resourceAppId -eq '00000003-0000-0000-c000-000000000000'
        @($graph.resourceAccess).Count | Should -Be 4
    }

    It 'Plans a new client secret, retiring the ones it replaces, only when asked' {
        (& $script:Plan (& $script:NewRegisteredTenant)).Changes | Should -BeNullOrEmpty

        $changes = @((& $script:Plan (& $script:NewRegisteredTenant) -ResetClientSecret).Changes)
        $changes | Should -HaveCount 1
        $changes[0].Action | Should -Be 'ResetClientSecret'
        $changes[0].Arguments.RetireKeyIds | Should -Be @('key-1')
    }

    It 'Makes a client secret for a registration that has none' {
        $tenant = & $script:NewRegisteredTenant
        $tenant.Applications[0].PasswordCredentials = @()

        @((& $script:Plan $tenant).Changes.Action) | Should -Be @('NewClientSecret')
    }

    It 'Leaves consent out of the plan, and says so, when the caller cannot see it' {
        $tenant = & $script:NewRegisteredTenant
        $tenant.Consent = [PSCustomObject]@{ Known = $false; Graph = @(); Api = @() }

        $plan = & $script:Plan $tenant

        $plan.Changes | Should -BeNullOrEmpty
        $plan.ConsentUnknown | Should -BeTrue
    }

    It 'Refuses to guess between two applications with JIM''s name' {
        $tenant = & $script:NewRegisteredTenant
        $tenant.Applications = @($tenant.Applications[0], $tenant.Applications[0])

        { & $script:Plan $tenant } | Should -Throw '*more than one*'
    }

    It 'Redirects the portal and Try It to each of JIM''s addresses, keeping a port and dropping a trailing slash' {
        $plan = & $script:Plan (& $script:NewEmptyTenant) -Url @('https://jim.example.com:8443/', 'https://jim2.example.com')
        $app = ($plan.Changes | Where-Object Action -eq 'NewApplication').Arguments.Application

        $app.web.redirectUris | Should -Contain 'https://jim.example.com:8443/signin-oidc'
        $app.web.redirectUris | Should -Contain 'https://jim2.example.com/signout-callback-oidc'
        $app.spa.redirectUris | Should -Be @('https://jim.example.com:8443/api/reference/', 'https://jim2.example.com/api/reference/')
    }
}

Describe 'Get-JIMEntraIdTokenTenantId' {

    It 'Reads the tenant from a Graph token''s tid claim' {
        InModuleScope JIM {
            $encode = { param($json) [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json)).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
            $jwt = (& $encode '{"alg":"none"}') + '.' + (& $encode '{"tid":"34599ee0-829e-4dd4-8da2-4727a0664d51","aud":"https://graph.microsoft.com"}') + '.sig'

            Get-JIMEntraIdTokenTenantId -AccessToken (ConvertTo-SecureString $jwt -AsPlainText -Force) | Should -Be '34599ee0-829e-4dd4-8da2-4727a0664d51'
        }
    }

    It 'Refuses a token that is not a JSON Web Token' {
        InModuleScope JIM {
            { Get-JIMEntraIdTokenTenantId -AccessToken (ConvertTo-SecureString 'opaque' -AsPlainText -Force) } | Should -Throw '*JSON Web Token*'
        }
    }
}

Describe 'Register-JIMIdentityProvider -Provider EntraId' {

    BeforeEach {
        InModuleScope JIM {
            $script:Token = ConvertTo-SecureString 'graph-token' -AsPlainText -Force
            Mock Get-JIMEntraIdRegistrationState {
                [PSCustomObject]@{
                    TenantId = '34599ee0-829e-4dd4-8da2-4727a0664d51'; Applications = @(); ServicePrincipal = $null
                    Consent = [PSCustomObject]@{ Known = $true; Graph = @(); Api = @() }; SignedInObjectId = 'aaaaaaaa-0000-0000-0000-000000000001'
                }
            }
            Mock Invoke-JIMEntraIdRegistrationChange {
                if ($Change.Action -eq 'NewApplication') { $Connection.AppId = '11111111-2222-3333-4444-555555555555' }
                if ($Change.Action -in 'NewClientSecret', 'ResetClientSecret') { 'generated-secret' }
            }
            Mock Get-JIMEntraIdDeviceCodeToken { ConvertTo-SecureString 'device-token' -AsPlainText -Force }
            Mock Get-JIMEntraIdTokenTenantId { '34599ee0-829e-4dd4-8da2-4727a0664d51' }
        }
    }

    It 'Needs the tenant' {
        InModuleScope JIM {
            { Register-JIMIdentityProvider -Provider EntraId -Url 'https://jim.example.com' -ErrorAction Stop } | Should -Throw '*-TenantId*'
        }
    }

    It 'Refuses Entra ID''s parameters for another provider' {
        InModuleScope JIM {
            { Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -TenantId 'tetronjim.onmicrosoft.com' -AccessToken $script:Token -ErrorAction Stop } |
                Should -Throw '*Entra ID*'
        }
    }

    It 'Registers JIM and returns what JIM needs, with the secret kept secure' {
        InModuleScope JIM {
            $result = Register-JIMIdentityProvider -Provider EntraId -Url 'https://jim.example.com' -TenantId 'tetronjim.onmicrosoft.com' -AccessToken $script:Token -Confirm:$false

            $result.PSObject.TypeNames | Should -Contain 'JIM.IdentityProviderRegistration'
            $result.Provider | Should -Be 'EntraId'
            $result.Authority | Should -Be 'https://login.microsoftonline.com/34599ee0-829e-4dd4-8da2-4727a0664d51/v2.0'
            $result.ClientId | Should -Be '11111111-2222-3333-4444-555555555555'
            $result.PublicClientId | Should -Be '11111111-2222-3333-4444-555555555555'
            $result.ApiScope | Should -Be 'api://11111111-2222-3333-4444-555555555555/access_as_user'
            $result.ClaimType | Should -Be 'oid'
            $result.MvAttribute | Should -Be 'Object Identifier'
            $result.InitialAdmin | Should -Be 'aaaaaaaa-0000-0000-0000-000000000001'
            [System.Net.NetworkCredential]::new('', $result.ClientSecret).Password | Should -Be 'generated-secret'
            Should -Invoke Get-JIMEntraIdDeviceCodeToken -Times 0
        }
    }

    It 'Signs in with a device code when given no token' {
        InModuleScope JIM {
            $null = Register-JIMIdentityProvider -Provider EntraId -Url 'https://jim.example.com' -TenantId 'tetronjim.onmicrosoft.com' -Confirm:$false

            Should -Invoke Get-JIMEntraIdDeviceCodeToken -Times 1 -ParameterFilter { $TenantId -eq 'tetronjim.onmicrosoft.com' }
        }
    }

    It 'Warns that consent is still needed when it cannot see it, and still returns what JIM needs' {
        InModuleScope JIM {
            Mock Get-JIMEntraIdRegistrationState {
                [PSCustomObject]@{
                    TenantId = '34599ee0-829e-4dd4-8da2-4727a0664d51'; Applications = @(); ServicePrincipal = $null
                    Consent = [PSCustomObject]@{ Known = $false; Graph = @(); Api = @() }; SignedInObjectId = $null
                }
            }

            $result = Register-JIMIdentityProvider -Provider EntraId -Url 'https://jim.example.com' -TenantId 'tetronjim.onmicrosoft.com' -AccessToken $script:Token -Confirm:$false -WarningVariable warnings -WarningAction SilentlyContinue

            @($warnings) -join ' ' | Should -BeLike '*consent*'
            $result.InitialAdmin | Should -BeNullOrEmpty
        }
    }

    It 'Accepts JIM on plain HTTP only at this computer''s own address' {
        InModuleScope JIM {
            { Register-JIMIdentityProvider -Provider EntraId -Url 'http://jim.example.com' -TenantId 'tetronjim.onmicrosoft.com' -AccessToken $script:Token -ErrorAction Stop } |
                Should -Throw '*https*'
            (Register-JIMIdentityProvider -Provider EntraId -Url 'http://localhost:5200' -TenantId 'tetronjim.onmicrosoft.com' -AccessToken $script:Token -Confirm:$false).ClientId |
                Should -Not -BeNullOrEmpty
        }
    }

    It 'Changes nothing with -WhatIf' {
        InModuleScope JIM {
            $result = Register-JIMIdentityProvider -Provider EntraId -Url 'https://jim.example.com' -TenantId 'tetronjim.onmicrosoft.com' -AccessToken $script:Token -WhatIf

            $result | Should -BeNullOrEmpty
            Should -Invoke Invoke-JIMEntraIdRegistrationChange -Times 0
        }
    }
}

Describe 'Invoke-JIMEntraIdRegistrationChange' {

    It 'retires the replaced client secrets, trying again while Graph has not caught up with the new one' {
        InModuleScope JIM {
            $script:RemoveCalls = 0
            Mock Start-Sleep {}
            Mock Invoke-JIMEntraIdGraphApi -ParameterFilter { $Path -like '*/addPassword' } { [pscustomobject]@{ secretText = 'new-secret' } }
            Mock Invoke-JIMEntraIdGraphApi -ParameterFilter { $Path -like '*/removePassword' } {
                $script:RemoveCalls++
                if ($script:RemoveCalls -lt 3) { throw 'Microsoft Graph refused POST /applications/x/removePassword: 400' }
            }
            $connection = @{ ApplicationId = 'object-id' }
            $change = [pscustomobject]@{ Action = 'ResetClientSecret'; Arguments = @{ RetireKeyIds = @('key-1') } }

            Invoke-JIMEntraIdRegistrationChange -Connection $connection -Change $change | Should -Be 'new-secret'
            $script:RemoveCalls | Should -Be 3
        }
    }

    It 'gives up retiring a secret after a few tries, saying which' {
        InModuleScope JIM {
            Mock Start-Sleep {}
            Mock Invoke-JIMEntraIdGraphApi -ParameterFilter { $Path -like '*/addPassword' } { [pscustomobject]@{ secretText = 'new-secret' } }
            Mock Invoke-JIMEntraIdGraphApi -ParameterFilter { $Path -like '*/removePassword' } { throw 'Microsoft Graph refused POST /applications/x/removePassword: 400' }
            $change = [pscustomobject]@{ Action = 'ResetClientSecret'; Arguments = @{ RetireKeyIds = @('key-1') } }

            { Invoke-JIMEntraIdRegistrationChange -Connection @{ ApplicationId = 'object-id' } -Change $change } | Should -Throw '*key-1*'
        }
    }
}

Describe 'Invoke-JIMEntraIdGraphApi' {

    It 'reports the status and the exception''s message when Graph''s error body is empty' {
        InModuleScope JIM {
            Mock Invoke-RestMethod {
                $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::BadRequest)
                $exception = [Microsoft.PowerShell.Commands.HttpResponseException]::new('Response status code does not indicate success: 400 (Bad Request).', $response)
                $record = [System.Management.Automation.ErrorRecord]::new($exception, 'WebCmdletWebResponseException', 'InvalidOperation', $null)
                $record.ErrorDetails = [System.Management.Automation.ErrorDetails]::new('')
                throw $record
            }

            { Invoke-JIMEntraIdGraphApi -Connection @{ Token = (ConvertTo-SecureString 't' -AsPlainText -Force) } -Method Post -Path '/applications/x/removePassword' -Body @{} } |
                Should -Throw '*removePassword*400*Bad Request*'
        }
    }
}
