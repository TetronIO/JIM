# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    How the Entra ID sign-in checks (Scenario 035, #2367) reach the JIM test tenant: a Microsoft Graph token, JIM's
    registration there, and JIM pointed at it.

.DESCRIPTION
    Dot-source this file. The tenant (tetronjim.onmicrosoft.com) is a dedicated, isolated test tenant: its test people
    sign in with a password and no multi-factor authentication, which is acceptable only because it holds nothing else.

    In GitHub Actions (ad-lab.yml, in the jim-ad-lab environment), the Graph token comes from the CI app's federated
    credential: GitHub's OIDC token for the job is exchanged for one, so no client secret or certificate is stored
    anywhere. The CI app holds only Application.ReadWrite.OwnedBy and owns JIM's registration, which an administrator
    created once and consented to; each run repairs it and replaces its client secret. Outside Actions, set
    JIM_SSO_ENTRA_GRAPH_TOKEN to a Graph token that may manage the registration.

    The settings come from the jim-ad-lab environment: JIM_SSO_ENTRA_TENANT_ID, JIM_SSO_ENTRA_CI_CLIENT_ID and
    JIM_SSO_ENTRA_ADMIN_OBJECT_ID (variables), JIM_SSO_ENTRA_ADMIN_PASSWORD and JIM_SSO_ENTRA_USER_PASSWORD (secrets).
#>

# JIM's addresses in the registration: jim.web on the lab's Docker network, which the checks reach from the LDAP
# toolbox, and this computer's own, for running the checks from a development machine. Both are always registered, so
# a run from one never removes the other.
$script:EntraIdLabJimUrls = @('https://jim.web:8443', 'http://localhost:5200')

function Get-EntraIdLabJimUrl {
    <#
    .SYNOPSIS
        JIM's addresses in its Entra ID registration.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    return [string[]]$script:EntraIdLabJimUrls
}

function Get-EntraIdLabGraphToken {
    <#
    .SYNOPSIS
        A Microsoft Graph token for the test tenant, as a secure string: from the CI app's federated credential in GitHub
        Actions, otherwise from JIM_SSO_ENTRA_GRAPH_TOKEN.
    #>
    [CmdletBinding()]
    [OutputType([securestring])]
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$ClientId
    )

    if ($env:ACTIONS_ID_TOKEN_REQUEST_URL -and $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN) {
        $audience = [uri]::EscapeDataString('api://AzureADTokenExchange')
        $github = Invoke-RestMethod -Method Get -Uri "$($env:ACTIONS_ID_TOKEN_REQUEST_URL)&audience=$audience" -ErrorAction Stop `
            -Headers @{ Authorization = "Bearer $($env:ACTIONS_ID_TOKEN_REQUEST_TOKEN)" }
        $graph = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -ErrorAction Stop -Body @{
            client_id = $ClientId; grant_type = 'client_credentials'; scope = 'https://graph.microsoft.com/.default'
            client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'; client_assertion = [string]$github.value
        }
        return ConvertTo-SecureString -String ([string]$graph.access_token) -AsPlainText -Force
    }
    if ($env:JIM_SSO_ENTRA_GRAPH_TOKEN) {
        return ConvertTo-SecureString -String $env:JIM_SSO_ENTRA_GRAPH_TOKEN -AsPlainText -Force
    }
    throw 'No Microsoft Graph token for the Entra ID test tenant: in GitHub Actions the job needs the id-token: write permission (for the CI app''s federated credential), and elsewhere set JIM_SSO_ENTRA_GRAPH_TOKEN.'
}

function Register-EntraIdLabIdentityProvider {
    <#
    .SYNOPSIS
        Registers JIM in the test tenant, or repairs its registration, with Register-JIMIdentityProvider, as a customer
        does, and returns what it returned. The client secret is replaced each run, and kept out of the log and every
        artefact (Protect-ActiveDirectoryLabSecret).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][securestring]$AccessToken
    )

    $registration = Register-JIMIdentityProvider -Provider EntraId -Url (Get-EntraIdLabJimUrl) -TenantId $TenantId -AccessToken $AccessToken `
        -ResetClientSecret -Confirm:$false
    if (-not $registration -or -not $registration.ClientSecret) {
        throw 'Register-JIMIdentityProvider returned no client secret, so JIM cannot sign anyone in.'
    }
    Protect-ActiveDirectoryLabSecret -Secret ([System.Net.NetworkCredential]::new('', $registration.ClientSecret).Password)
    return $registration
}

function Set-EntraIdLabSsoEnvironment {
    <#
    .SYNOPSIS
        Puts a registration where docker-compose.ad-lab-entra.yml reads it: the JIM_AD_LAB_ENTRA_* variables of this
        process, which compose interpolates into jim.web's JIM_SSO_* settings. Nothing is written to a file.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Sets this process''s environment only.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Registration,
        [Parameter(Mandatory)][string]$InitialAdmin
    )

    $env:JIM_AD_LAB_ENTRA_AUTHORITY = $Registration.Authority
    $env:JIM_AD_LAB_ENTRA_CLIENT_ID = $Registration.ClientId
    $env:JIM_AD_LAB_ENTRA_CLIENT_SECRET = [System.Net.NetworkCredential]::new('', $Registration.ClientSecret).Password
    $env:JIM_AD_LAB_ENTRA_API_SCOPE = $Registration.ApiScope
    $env:JIM_AD_LAB_ENTRA_PUBLIC_CLIENT_ID = $Registration.PublicClientId
    $env:JIM_AD_LAB_ENTRA_CLAIM_TYPE = $Registration.ClaimType
    $env:JIM_AD_LAB_ENTRA_MV_ATTRIBUTE = $Registration.MvAttribute
    $env:JIM_AD_LAB_ENTRA_INITIAL_ADMIN = $InitialAdmin
}
