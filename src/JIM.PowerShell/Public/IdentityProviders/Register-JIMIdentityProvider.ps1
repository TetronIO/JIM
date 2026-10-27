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
        PowerShell module (a Native application, with loopback redirect URIs for ports 8400 to 8409) and JIM's API (a
        Web API open to everyone, whose one claim rule issues each person's objectGUID as oid with their name and
        email). Only JIM's two clients may ask for the API.

        AD FS is configured through the ADFS PowerShell module, which only an AD FS server has. Run this on the
        federation server, or pass -Session with a PowerShell session to it.

        For Keycloak it creates, in the realm -Authority names, the portal's confidential client and the PowerShell
        module's public client (with loopback redirect URIs for ports 8400 to 8409), both using PKCE, and the jim-api
        client scope, which puts the portal's client in an access token's audience. Only JIM's two clients may ask for
        jim-api. Keycloak is configured through its admin REST API, from any computer that can reach it, with an
        account that administers the realm.

        This command does not connect to JIM, so Connect-JIM is not needed. The client secret is returned, as a secure
        string, only when this run created or replaced it; if it is lost, run again with -ResetClientSecret.

    .PARAMETER Provider
        The identity provider to register JIM with.

    .PARAMETER Url
        JIM's address as people reach it, such as https://jim.example.com, with the port if it is not 443. The portal's
        sign-in and sign-out redirect URIs are made from it. For Keycloak, an http address is accepted for JIM on this
        computer only, such as http://localhost:5200.

    .PARAMETER Name
        The name of the AD FS Application Group, which is also its identifier. The applications in it are named after
        it, with " - Portal", " - PowerShell" and " - API".

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
        unattended. The client is in the realm -AdminRealm names.

    .PARAMETER AdminRealm
        The Keycloak realm -Credential or -ClientCredential signs in to.

    .PARAMETER ClientId
        The client ID of the portal's Keycloak client, the value for JIM_SSO_CLIENT_ID.

    .PARAMETER PublicClientId
        The client ID of the PowerShell module's Keycloak client, the value for JIM_SSO_PUBLIC_CLIENT_ID.

    .PARAMETER ResetClientSecret
        Replaces the portal's client secret, and returns the new one. JIM cannot sign anyone in until JIM_SSO_SECRET
        is given the new secret.

    .OUTPUTS
        JIM.IdentityProviderRegistration
        Provider (string): The identity provider, ADFS or Keycloak.
        Authority (string): The value for JIM_SSO_AUTHORITY.
        ClientId (string): The portal's client identifier, the value for JIM_SSO_CLIENT_ID.
        ClientSecret (securestring): The portal's client secret, the value for JIM_SSO_SECRET. Empty unless this run created or replaced it.
        PublicClientId (string): The PowerShell module's client identifier, the value for JIM_SSO_PUBLIC_CLIENT_ID.
        ApiScope (string): The API's identifier, the value for JIM_SSO_API_SCOPE.
        ClaimType (string): The claim JIM identifies people by, the value for JIM_SSO_CLAIM_TYPE.
        MvAttribute (string): The Metaverse attribute that claim is matched with, the value for JIM_SSO_MV_ATTRIBUTE.
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
        [ValidateSet('ADFS', 'Keycloak')]
        [string]$Provider,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Url,

        [Parameter(ParameterSetName = 'ADFS')]
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

        [switch]$ResetClientSecret
    )

    $keycloakParameters = $PSCmdlet.ParameterSetName -ne 'ADFS'
    if ($Provider -eq 'Keycloak' -and -not $keycloakParameters) {
        throw 'Keycloak needs -Authority, the realm JIM signs people in from, and -Credential or -ClientCredential to configure it with.'
    }
    if ($Provider -eq 'ADFS' -and $keycloakParameters) {
        throw '-Authority, -Credential, -ClientCredential, -AdminRealm, -ClientId and -PublicClientId are for Keycloak. AD FS takes -Session, or runs on the federation server.'
    }

    # JIM on plain HTTP is only for trying it out on this computer, and AD FS refuses even that.
    $uri = $null
    $allowsLoopbackHttp = $Provider -eq 'Keycloak'
    if (-not [uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -or
        -not ($uri.Scheme -eq 'https' -or ($allowsLoopbackHttp -and $uri.Scheme -eq 'http' -and $uri.IsLoopback))) {
        throw "The JIM URL must be an absolute https address, such as https://jim.example.com, not '$Url'."
    }

    if ($Provider -eq 'Keycloak') {
        $keycloak = ConvertTo-JIMKeycloakAuthority -Authority $Authority
        $connection = @{
            ServerUrl = $keycloak.ServerUrl; Realm = $keycloak.Realm; AdminRealm = $AdminRealm
            Credential = $Credential; ClientCredential = $ClientCredential
        }
        $connection.Token = Get-JIMKeycloakAdminToken -ServerUrl $keycloak.ServerUrl -AdminRealm $AdminRealm `
            -Credential $Credential -ClientCredential $ClientCredential
        $current = Get-JIMKeycloakRegistrationState -Connection $connection
        $plan = Get-JIMKeycloakRegistrationPlan -Url $Url -ClientId $ClientId -PublicClientId $PublicClientId -Current $current `
            -ResetClientSecret:$ResetClientSecret

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
            Changes        = $made.Changes
        }
        $result.PSObject.TypeNames.Insert(0, 'JIM.IdentityProviderRegistration')
        $result
    }
    finally {
        if ($ownSession -and $activeSession) { Remove-PSSession -Session $activeSession }
    }
}
