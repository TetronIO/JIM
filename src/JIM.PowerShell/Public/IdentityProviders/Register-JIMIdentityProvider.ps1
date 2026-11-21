# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Register-JIMIdentityProvider {
    <#
    .SYNOPSIS
        Registers JIM with its identity provider, or repairs the registration.

    .DESCRIPTION
        Creates everything JIM needs on its identity provider, exactly as the SSO setup guide describes, and corrects
        whatever has drifted from it. Running it again changes nothing when the registration is already right, so it
        is also the way to check and repair it: -WhatIf lists each change it would make, and makes none. The identity
        provider decides who someone is; what they may do is decided by their roles in JIM.

        For AD FS it creates an Application Group holding the portal (a Server application, with a client secret), the
        PowerShell module (a Native application, with loopback redirect URIs for ports 8400 to 8409, and each -Url's
        /api/reference/, for the API reference's Try It) and JIM's API (a Web API open to everyone, whose one claim rule
        issues each person's objectGUID as oid with their name and email). Only JIM's two clients may ask for the API.
        Try It also needs CORS on for JIM's address, a farm-wide setting this command leaves to you; the AD FS SSO guide
        gives the commands.

        AD FS is configured through the ADFS PowerShell module, which only an AD FS server has. Run this on the
        federation server, or pass -Session with a PowerShell session to it.

        For Keycloak it creates, in the realm -Authority names, the portal's confidential client and the PowerShell
        module's public client (with loopback redirect URIs for ports 8400 to 8409, and each -Url's /api/reference/ with
        its origin as a web origin, for the API reference's Try It), both using PKCE, and the jim-api
        client scope, which puts the portal's client in an access token's audience. Only JIM's two clients may ask for
        jim-api. Keycloak is configured through its admin REST API, from any computer that can reach it, with an
        account that administers the realm.

        For Microsoft Entra ID it creates, in the tenant -TenantId names, one application for the portal (a Web
        platform, with a client secret), the PowerShell module (Mobile and desktop, with loopback redirect URIs for ports
        8400 to 8409) and the API reference's Try It (a single-page application redirected to each -Url's
        /api/reference/), which is JIM's API too: api://{client ID}, offering the access_as_user scope, issuing version 2
        access tokens. It asks Microsoft Graph for openid, profile, email and offline_access, puts each person's email
        address in their tokens, and, when you may, consents to all of that for everyone, so nobody signing in is asked.
        Entra ID is configured through Microsoft Graph, from any computer that can reach it: sign in through the browser
        it opens, as a person who administers the tenant's applications, or pass -AccessToken with a Graph token for
        running this unattended or on a computer with no browser. A token holding only Application.ReadWrite.OwnedBy maintains a registration its application owns,
        but cannot give consent, which this command then says an administrator must give.

        This command does not connect to JIM, so Connect-JIM is not needed. The client secret is returned, as a secure
        string, only when this run created, replaced or set it; if it is lost, run again with -ResetClientSecret.

    .PARAMETER Provider
        The identity provider to register JIM with.

    .PARAMETER Url
        JIM's address as people reach it, such as https://jim.example.com, with the port if it is not 443. The portal's
        sign-in and sign-out redirect URIs are made from it. Give more than one when people reach JIM at more than one
        address. For Keycloak, an http address is accepted for JIM on this computer only, such as
        http://localhost:5200.

    .PARAMETER Name
        For AD FS, the name of the Application Group, which is also its identifier; the applications in it are named
        after it, with " - Portal", " - PowerShell" and " - API". For Entra ID, the application's display name, by which
        it is found.

    .PARAMETER Session
        A PowerShell session to the AD FS server, from New-PSSession. When omitted, the command runs on this computer,
        which must be an AD FS server. The session is left open.

    .PARAMETER Authority
        The Keycloak realm JIM signs people in from, such as https://sso.example.com/realms/jim. It is also the value
        for JIM_SSO_AUTHORITY. The realm must exist.

    .PARAMETER Credential
        The username and password of a Keycloak administrator, who signs in through Keycloak's admin-cli client in the
        realm -AdminRealm names. The account needs the realm-admin role in JIM's realm, or the master realm's admin
        role.

    .PARAMETER ClientCredential
        The client ID and secret of a Keycloak client whose service account administers JIM's realm, for running this
        unattended. The client is in the realm -AdminRealm names. On Keycloak 26.8.2 or later, the client also needs the
        kc-admin-api client scope as a default scope.

    .PARAMETER AdminRealm
        The Keycloak realm -Credential or -ClientCredential signs in to.

    .PARAMETER ClientId
        The client ID of the portal's Keycloak client, the value for JIM_SSO_CLIENT_ID.

    .PARAMETER PublicClientId
        The client ID of the PowerShell module's Keycloak client, the value for JIM_SSO_PUBLIC_CLIENT_ID.

    .PARAMETER TenantId
        The Entra ID tenant to register JIM in, by its ID or a domain name, such as contoso.onmicrosoft.com.

    .PARAMETER AccessToken
        A Microsoft Graph access token for the tenant, for running this unattended, such as from a workload identity.
        It needs Application.ReadWrite.All, or Application.ReadWrite.OwnedBy once its application owns JIM's; giving
        consent also needs DelegatedPermissionGrant.ReadWrite.All. Without it, you sign in through a browser.

    .PARAMETER UseDeviceCode
        Signs in with a device code instead of a browser, for a console with no browser. Microsoft recommends that
        Conditional Access blocks device code sign-in, so many tenants refuse it; pass -AccessToken there instead.

    .PARAMETER ResetClientSecret
        Replaces the portal's client secret, and returns the new one. JIM cannot sign anyone in until JIM_SSO_SECRET
        is given the new secret.

    .PARAMETER ClientSecret
        For Keycloak, the portal's client secret, in place of one Keycloak makes: the client is created with it, or
        given it if its secret differs. Meant for test and development realms, whose secret is known in advance;
        elsewhere, let Keycloak make one.

    .OUTPUTS
        JIM.IdentityProviderRegistration
        Provider (string): The identity provider, ADFS, Keycloak or EntraId.
        Authority (string): The value for JIM_SSO_AUTHORITY.
        ClientId (string): The portal's client identifier, the value for JIM_SSO_CLIENT_ID.
        ClientSecret (securestring): The portal's client secret, the value for JIM_SSO_SECRET. Empty unless this run created, replaced or set it.
        PublicClientId (string): The PowerShell module's client identifier, the value for JIM_SSO_PUBLIC_CLIENT_ID.
        ApiScope (string): The API's identifier, the value for JIM_SSO_API_SCOPE.
        ClaimType (string): The claim JIM identifies people by, the value for JIM_SSO_CLAIM_TYPE.
        MvAttribute (string): The Metaverse attribute that claim is matched with, the value for JIM_SSO_MV_ATTRIBUTE.
        InitialAdmin (string): For Entra ID, the object ID of the person who ran this, the value for JIM_SSO_INITIAL_ADMIN when they are to be JIM's first administrator. Empty for the other providers, and when run with a token that is not a person's.
        Changes (string[]): The changes this run made. Empty when the registration was already right.

        With -WhatIf, nothing is returned.

    .EXAMPLE
        $registration = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com'

        Run on the AD FS server: registers JIM, or repairs its registration, and keeps what JIM needs.

    .EXAMPLE
        $session = New-PSSession -ComputerName 'adfs1.example.com'
        Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -Session $session -WhatIf

        Lists what is missing or has drifted on a remote AD FS server, and changes nothing.

    .EXAMPLE
        $registration = Register-JIMIdentityProvider -Provider Keycloak -Url 'https://jim.example.com' -Authority 'https://sso.example.com/realms/jim' -Credential (Get-Credential)

        Registers JIM in Keycloak's jim realm, or repairs its registration, signing in as a Keycloak administrator.

    .EXAMPLE
        $registration = Register-JIMIdentityProvider -Provider EntraId -Url 'https://jim.example.com' -TenantId 'contoso.onmicrosoft.com'
        Registers JIM in the contoso tenant, or repairs its registration, after you sign in through the browser it opens.

    .EXAMPLE
        $registration = Register-JIMIdentityProvider -Provider ADFS -Url 'https://jim.example.com' -ResetClientSecret
        [System.Net.NetworkCredential]::new('', $registration.ClientSecret).Password | Set-Clipboard

        Replaces the portal's client secret and copies the new one to the clipboard, for JIM_SSO_SECRET.

    .LINK
        Get-JIMIdentityProviderTrust
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ADFS')]
    [OutputType('JIM.IdentityProviderRegistration')]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('ADFS', 'Keycloak', 'EntraId')]
        [string]$Provider,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string[]]$Url,

        [Parameter(ParameterSetName = 'ADFS')]
        [Parameter(ParameterSetName = 'EntraId')]
        [Parameter(ParameterSetName = 'EntraIdToken')]
        [ValidateNotNullOrEmpty()]
        [string]$Name = 'JIM Identity Management',

        [Parameter(ParameterSetName = 'ADFS')]
        [System.Management.Automation.Runspaces.PSSession]$Session,

        [Parameter(Mandatory, ParameterSetName = 'Keycloak')]
        [Parameter(Mandatory, ParameterSetName = 'KeycloakServiceAccount')]
        [ValidateNotNullOrEmpty()]
        [string]$Authority,

        [Parameter(Mandatory, ParameterSetName = 'Keycloak')]
        [pscredential]$Credential,

        [Parameter(Mandatory, ParameterSetName = 'KeycloakServiceAccount')]
        [pscredential]$ClientCredential,

        [Parameter(ParameterSetName = 'Keycloak')]
        [Parameter(ParameterSetName = 'KeycloakServiceAccount')]
        [ValidateNotNullOrEmpty()]
        [string]$AdminRealm = 'master',

        [Parameter(ParameterSetName = 'Keycloak')]
        [Parameter(ParameterSetName = 'KeycloakServiceAccount')]
        [ValidateNotNullOrEmpty()]
        [string]$ClientId = 'jim',

        [Parameter(ParameterSetName = 'Keycloak')]
        [Parameter(ParameterSetName = 'KeycloakServiceAccount')]
        [ValidateNotNullOrEmpty()]
        [string]$PublicClientId = 'jim-powershell',

        [Parameter(Mandatory, ParameterSetName = 'EntraId')]
        [Parameter(Mandatory, ParameterSetName = 'EntraIdToken')]
        [ValidateNotNullOrEmpty()]
        [string]$TenantId,

        [Parameter(Mandatory, ParameterSetName = 'EntraIdToken')]
        [securestring]$AccessToken,

        [Parameter(ParameterSetName = 'EntraId')]
        [switch]$UseDeviceCode,

        [switch]$ResetClientSecret,

        [Parameter(ParameterSetName = 'Keycloak')]
        [Parameter(ParameterSetName = 'KeycloakServiceAccount')]
        [securestring]$ClientSecret
    )

    # Which provider the parameters given are for, from the parameter set they bound.
    $parametersFor = switch ($PSCmdlet.ParameterSetName) {
        'ADFS' { 'ADFS' }
        { $_ -in 'Keycloak', 'KeycloakServiceAccount' } { 'Keycloak' }
        default { 'EntraId' }
    }
    if ($Provider -eq 'Keycloak' -and $parametersFor -ne 'Keycloak') {
        throw 'Keycloak needs -Authority, the realm JIM signs people in from, and -Credential or -ClientCredential to configure it with.'
    }
    if ($Provider -eq 'EntraId' -and $parametersFor -ne 'EntraId') {
        throw 'Entra ID needs -TenantId, the tenant to register JIM in, and takes -AccessToken to run unattended.'
    }
    if ($Provider -eq 'ADFS' -and $parametersFor -eq 'Keycloak') {
        throw '-Authority, -Credential, -ClientCredential, -AdminRealm, -ClientId, -PublicClientId and -ClientSecret are for Keycloak. AD FS takes -Session, or runs on the federation server.'
    }
    if ($Provider -ne 'EntraId' -and $parametersFor -eq 'EntraId') {
        throw '-TenantId and -AccessToken are for Entra ID. AD FS takes -Session, or runs on the federation server; Keycloak takes -Authority and a credential.'
    }
    if ($ClientSecret -and $ResetClientSecret) {
        throw '-ClientSecret gives the portal the secret it names, and -ResetClientSecret has Keycloak make a new one; use one of them.'
    }

    # JIM on plain HTTP is only for trying it out on this computer, and AD FS refuses even that.
    $allowsLoopbackHttp = $Provider -in 'Keycloak', 'EntraId'
    foreach ($address in $Url) {
        $uri = $null
        if (-not [uri]::TryCreate($address, [UriKind]::Absolute, [ref]$uri) -or
            -not ($uri.Scheme -eq 'https' -or ($allowsLoopbackHttp -and $uri.Scheme -eq 'http' -and $uri.IsLoopback))) {
            throw "The JIM URL must be an absolute https address, such as https://jim.example.com, not '$address'."
        }
    }

    if ($Provider -eq 'EntraId') {
        $token = if ($AccessToken) { $AccessToken } else { Get-JIMEntraIdAdminToken -TenantId $TenantId -UseDeviceCode:$UseDeviceCode }
        $connection = @{ Token = $token; TenantId = Get-JIMEntraIdTokenTenantId -AccessToken $token }
        $current = Get-JIMEntraIdRegistrationState -Connection $connection -Name $Name
        $plan = Get-JIMEntraIdRegistrationPlan -Url $Url -Name $Name -Current $current -ResetClientSecret:$ResetClientSecret
        $existing = @($current.Applications) | Select-Object -First 1
        if ($existing) { $connection.ApplicationId = $existing.Id; $connection.AppId = $existing.AppId }
        if ($current.ServicePrincipal) { $connection.ServicePrincipalId = $current.ServicePrincipal.Id }
        $made = Invoke-JIMRegistrationPlan -Plan $plan -Cmdlet $PSCmdlet -Apply { param($change) Invoke-JIMEntraIdRegistrationChange -Connection $connection -Change $change }
        if (-not $made) { return }
        if ($plan.ConsentUnknown) {
            Write-Warning "This run could not see or give consent for JIM's application, so people signing in may be asked for it, or refused. An administrator gives it once: run this again signed in as one, or grant admin consent on the application's API permissions page in the Entra admin centre."
        }
        $result = [PSCustomObject]@{
            Provider       = $Provider
            Authority      = "https://login.microsoftonline.com/$($connection.TenantId)/v2.0"
            ClientId       = $connection.AppId
            ClientSecret   = $made.Secret
            PublicClientId = $connection.AppId
            ApiScope       = "api://$($connection.AppId)/access_as_user"
            ClaimType      = 'oid'
            MvAttribute    = 'Object Identifier'
            InitialAdmin   = $current.SignedInObjectId
            Changes        = $made.Changes
        }
        $result.PSObject.TypeNames.Insert(0, 'JIM.IdentityProviderRegistration')
        return $result
    }

    if ($Provider -eq 'Keycloak') {
        $keycloak = ConvertTo-JIMKeycloakAuthority -Authority $Authority
        $connection = @{
            ServerUrl = $keycloak.ServerUrl; Realm = $keycloak.Realm; AdminRealm = $AdminRealm
            Credential = $Credential; ClientCredential = $ClientCredential
        }
        $connection.Token = Get-JIMKeycloakAdminToken -ServerUrl $keycloak.ServerUrl -AdminRealm $AdminRealm `
            -Credential $Credential -ClientCredential $ClientCredential
        # The portal's secret is read only to compare it with one given.
        $given = if ($ClientSecret) { @{ ClientSecret = $ClientSecret } } else { @{} }
        $current = Get-JIMKeycloakRegistrationState -Connection $connection -SecretOf $(if ($ClientSecret) { $ClientId })
        $plan = Get-JIMKeycloakRegistrationPlan -Url $Url -ClientId $ClientId -PublicClientId $PublicClientId -Current $current `
            -ResetClientSecret:$ResetClientSecret @given

        $made = Invoke-JIMRegistrationPlan -Plan $plan -Cmdlet $PSCmdlet -Apply { param($change) Invoke-JIMKeycloakRegistrationChange -Connection $connection -Change $change }
        if (-not $made) { return }

        $result = [PSCustomObject]@{
            Provider       = $Provider
            Authority      = $keycloak.Authority
            ClientId       = $plan.ClientId
            ClientSecret   = $made.Secret
            PublicClientId = $plan.PublicClientId
            ApiScope       = $plan.ApiScope
            ClaimType      = 'sub'
            MvAttribute    = 'Subject Identifier'
            InitialAdmin   = $null
            Changes        = $made.Changes
        }
        $result.PSObject.TypeNames.Insert(0, 'JIM.IdentityProviderRegistration')
        return $result
    }

    $ownSession = -not $Session
    $activeSession = if ($ownSession) { New-JIMAdfsSession } else { $Session }
    try {
        $current = Get-JIMAdfsRegistrationState -Session $activeSession -Name $Name
        $plan = Get-JIMAdfsRegistrationPlan -Url $Url -Name $Name -Current $current -ResetClientSecret:$ResetClientSecret

        $made = Invoke-JIMRegistrationPlan -Plan $plan -Cmdlet $PSCmdlet -Apply { param($change) Invoke-JIMAdfsRegistrationChange -Session $activeSession -Change $change }
        if (-not $made) { return }

        $authority = "https://$($current.HostName)/adfs"
        $result = [PSCustomObject]@{
            Provider       = $Provider
            Authority      = $authority
            ClientId       = $plan.ClientId
            ClientSecret   = $made.Secret
            PublicClientId = $plan.PublicClientId
            ApiScope       = $plan.ApiIdentifier
            ClaimType      = 'oid'
            MvAttribute    = 'Object GUID'
            InitialAdmin   = $null
            Changes        = $made.Changes
        }
        $result.PSObject.TypeNames.Insert(0, 'JIM.IdentityProviderRegistration')
        $result
    }
    finally {
        if ($ownSession -and $activeSession) { Remove-PSSession -Session $activeSession }
    }
}
