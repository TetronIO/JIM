# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for EntraIdLab-Helpers.ps1: how the Entra ID sign-in checks (Scenario 035, #2367) get a Microsoft Graph
    token, register JIM in the test tenant, and point JIM at it. Neither Entra ID nor Graph is called.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot 'EntraIdLab-Helpers.ps1')
    $script:SavedEnvironment = @{}
    foreach ($name in 'ACTIONS_ID_TOKEN_REQUEST_URL', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'JIM_SSO_ENTRA_GRAPH_TOKEN') {
        $script:SavedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }
}

AfterAll {
    foreach ($name in $script:SavedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $script:SavedEnvironment[$name])
    }
    Get-ChildItem Env: | Where-Object Name -like 'JIM_AD_LAB_ENTRA_*' | ForEach-Object { Remove-Item "Env:$($_.Name)" }
}

Describe 'Get-EntraIdLabGraphToken' {

    BeforeEach {
        foreach ($name in 'ACTIONS_ID_TOKEN_REQUEST_URL', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'JIM_SSO_ENTRA_GRAPH_TOKEN') {
            [Environment]::SetEnvironmentVariable($name, $null)
        }
    }

    It 'exchanges GitHub''s OIDC token for a Graph token, as the CI app''s federated credential, in GitHub Actions' {
        $env:ACTIONS_ID_TOKEN_REQUEST_URL = 'https://token.actions.example/request?api-version=2.0'
        $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN = 'request-token'
        Mock Invoke-RestMethod -ParameterFilter { $Method -eq 'Get' } { [pscustomobject]@{ value = 'github-oidc-token' } }
        Mock Invoke-RestMethod -ParameterFilter { $Method -eq 'Post' } { [pscustomobject]@{ access_token = 'graph-token' } }

        $token = Get-EntraIdLabGraphToken -TenantId 'tenant-id' -ClientId 'ci-client-id'

        [System.Net.NetworkCredential]::new('', $token).Password | Should -Be 'graph-token'
        Should -Invoke Invoke-RestMethod -Times 1 -ParameterFilter {
            $Method -eq 'Get' -and $Uri -eq 'https://token.actions.example/request?api-version=2.0&audience=api%3A%2F%2FAzureADTokenExchange' -and
            $Headers.Authorization -eq 'Bearer request-token'
        }
        Should -Invoke Invoke-RestMethod -Times 1 -ParameterFilter {
            $Method -eq 'Post' -and $Uri -eq 'https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token' -and
            $Body.client_id -eq 'ci-client-id' -and $Body.grant_type -eq 'client_credentials' -and
            $Body.scope -eq 'https://graph.microsoft.com/.default' -and $Body.client_assertion -eq 'github-oidc-token' -and
            $Body.client_assertion_type -eq 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
        }
    }

    It 'uses the token given in JIM_SSO_ENTRA_GRAPH_TOKEN outside GitHub Actions' {
        $env:JIM_SSO_ENTRA_GRAPH_TOKEN = 'local-token'
        Mock Invoke-RestMethod { throw 'not expected' }

        $token = Get-EntraIdLabGraphToken -TenantId 'tenant-id' -ClientId 'ci-client-id'

        [System.Net.NetworkCredential]::new('', $token).Password | Should -Be 'local-token'
        Should -Invoke Invoke-RestMethod -Times 0
    }

    It 'says how to get one when there is neither' {
        { Get-EntraIdLabGraphToken -TenantId 'tenant-id' -ClientId 'ci-client-id' } | Should -Throw '*id-token: write*JIM_SSO_ENTRA_GRAPH_TOKEN*'
    }
}

Describe 'Set-EntraIdLabSsoEnvironment' {

    It 'puts the registration where docker-compose.ad-lab-entra.yml reads it, in the process environment' {
        $registration = [pscustomobject]@{
            Authority = 'https://login.microsoftonline.com/tenant-id/v2.0'; ClientId = 'app-id'; PublicClientId = 'app-id'
            ClientSecret = (ConvertTo-SecureString 'portal-secret' -AsPlainText -Force); ApiScope = 'api://app-id/access_as_user'
            ClaimType = 'oid'; MvAttribute = 'Object Identifier'
        }

        Set-EntraIdLabSsoEnvironment -Registration $registration -InitialAdmin 'admin-object-id'

        $env:JIM_AD_LAB_ENTRA_AUTHORITY | Should -Be 'https://login.microsoftonline.com/tenant-id/v2.0'
        $env:JIM_AD_LAB_ENTRA_CLIENT_ID | Should -Be 'app-id'
        $env:JIM_AD_LAB_ENTRA_CLIENT_SECRET | Should -Be 'portal-secret'
        $env:JIM_AD_LAB_ENTRA_API_SCOPE | Should -Be 'api://app-id/access_as_user'
        $env:JIM_AD_LAB_ENTRA_PUBLIC_CLIENT_ID | Should -Be 'app-id'
        $env:JIM_AD_LAB_ENTRA_CLAIM_TYPE | Should -Be 'oid'
        $env:JIM_AD_LAB_ENTRA_MV_ATTRIBUTE | Should -Be 'Object Identifier'
        $env:JIM_AD_LAB_ENTRA_INITIAL_ADMIN | Should -Be 'admin-object-id'

        # Every variable set here is one the overlay reads.
        $overlay = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' 'docker' 'docker-compose.ad-lab-entra.yml') -Raw
        foreach ($name in Get-ChildItem Env: | Where-Object Name -like 'JIM_AD_LAB_ENTRA_*' | ForEach-Object Name) {
            $overlay | Should -Match ([regex]::Escape("`${$name")) -Because "$name must reach jim.web"
        }
    }
}

Describe 'Register-EntraIdLabIdentityProvider' {

    BeforeAll {
        function Register-JIMIdentityProvider { param($Provider, $Url, $TenantId, $AccessToken, [switch]$ResetClientSecret, $Confirm) }
        function Protect-ActiveDirectoryLabSecret { param($Secret) }
    }

    It 'registers both of JIM''s lab addresses, with a fresh secret it keeps out of the log and the artefacts' {
        Mock Register-JIMIdentityProvider {
            [pscustomobject]@{ ClientId = 'app-id'; ClientSecret = (ConvertTo-SecureString 'new-secret' -AsPlainText -Force); Changes = @() }
        }
        Mock Protect-ActiveDirectoryLabSecret {}
        $token = ConvertTo-SecureString 'graph-token' -AsPlainText -Force

        $registration = Register-EntraIdLabIdentityProvider -TenantId 'tenant-id' -AccessToken $token

        $registration.ClientId | Should -Be 'app-id'
        Should -Invoke Register-JIMIdentityProvider -Times 1 -ParameterFilter {
            $Provider -eq 'EntraId' -and $TenantId -eq 'tenant-id' -and $ResetClientSecret -and
            (@($Url) -join ',') -eq 'https://jim.web:8443,http://localhost:5200'
        }
        Should -Invoke Protect-ActiveDirectoryLabSecret -Times 1 -ParameterFilter { $Secret -eq 'new-secret' }
    }
}
