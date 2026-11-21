# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Register-JIMEntraIdConnector {
    <#
    .SYNOPSIS
        Sets up the Microsoft Entra ID Connector: the app registration it authenticates as, its permissions and
        credential, and the Connected System that uses it. Run it again to check and repair them.

    .DESCRIPTION
        Creates, in the tenant -TenantId names, a single-tenant application for JIM's Microsoft Entra ID Connector. It
        asks Microsoft Graph for least-privilege application permissions: to read and write users and groups, and to
        read hidden group membership. It grants them with admin consent. It then gives the application a credential and creates the Connected System in JIM with the
        tenant, the application and the credential already set.

        The credential is a certificate this command makes in memory: its private key goes only to JIM, which stores it
        encrypted, and the application holds only the public part. With -ManagedIdentityClientId it is instead a
        federated credential trusting the user-assigned managed identity of the Azure virtual machine JIM runs on, so
        no secret or key exists anywhere. A certificate is renewed when it has less than 30 days to run.

        Running it again changes nothing when everything is already right, so it is also the way to check and repair
        the setup: -WhatIf lists each change it would make, and makes none. A capability switch left out of a later
        run keeps what the setup already does, so a run with only -TrackSourceOfAuthority adds that and takes nothing
        away. To turn a capability off, give its switch as false, such as -TrackSourceOfAuthority:$false; the
        Microsoft Graph permissions no longer needed are then revoked.

        Microsoft Graph is reached from any computer that can reach it: sign in through the browser this command opens,
        as a person who may grant admin consent for application permissions (Privileged Role Administrator or Global
        Administrator), or pass -AccessToken with a Graph token for running this unattended or on a computer with no
        browser. Connect to JIM with Connect-JIM first.

    .PARAMETER TenantId
        The tenant to set the Connector up in: its tenant ID or one of its verified domain names, such as
        contoso.onmicrosoft.com.

    .PARAMETER UseDeviceCode
        Signs in to Microsoft Graph with a device code instead of a browser, for a console with no browser. Microsoft
        recommends that Conditional Access blocks device code sign-in, so it fails in many tenants.

    .PARAMETER AccessToken
        A Microsoft Graph access token to use instead of signing in, holding Application.ReadWrite.All and
        AppRoleAssignment.ReadWrite.All, and RoleManagement.ReadWrite.Directory with -PasswordManagement.

    .PARAMETER Name
        The application's display name, by which it is found.

    .PARAMETER ConnectedSystemName
        The name of the Connected System in JIM, by which it is found.

    .PARAMETER ReadOnly
        Asks only to read users and groups, for a Connected System that imports and never exports.

    .PARAMETER SkipHiddenMembership
        Leaves out Member.Read.Hidden. Without it, the members of groups whose membership is hidden are never read, and
        JIM leaves them as it holds them; with it, anyone who can view those groups in JIM sees their members.

    .PARAMETER DirectoryExtensions
        Adds Directory.Read.All, so a schema refresh can offer the tenant's directory extensions.

    .PARAMETER PasswordManagement
        Lets JIM set passwords in Entra ID: its Initial Password, Set Password and Password Synchronisation. Adds
        User-PasswordProfile.ReadWrite.All and assigns the application the User Administrator role over the
        Administrative Unit -AdministrativeUnitId names, which Microsoft Graph also requires.

    .PARAMETER TrackSourceOfAuthority
        Turns on the Connected System's Track Source of Authority Changes, and adds the
        User-OnPremisesSyncBehavior.ReadWrite.All and Group-OnPremisesSyncBehavior.ReadWrite.All permissions it needs,
        so an object whose Source of Authority was switched between Active Directory and Entra ID reads as managed where
        it now is. Microsoft offers no read-only form of these permissions: they also allow switching an object's
        Source of Authority, which JIM never does.

    .PARAMETER AdministrativeUnitId
        With -PasswordManagement, the object ID of the Administrative Unit holding the users JIM manages. The User
        Administrator role is assigned over it only, never across the tenant. A later run that leaves -PasswordManagement
        out keeps the permission and leaves the role assignment as it is.

    .PARAMETER ManagedIdentityClientId
        The client ID of the user-assigned managed identity attached to the Azure virtual machine JIM runs on. The
        application trusts it with a federated credential, and no certificate is made. A later run that leaves it out
        keeps the managed identity the Connected System already uses.

    .PARAMETER CertificateValidityDays
        How long a certificate this command makes is valid, in days.

    .PARAMETER ResetCertificate
        Makes a new certificate even though the current one is still valid, and removes the old one once JIM holds
        the new. On a Connected System that authenticates with a managed identity, it moves it back to a certificate.

    .OUTPUTS
        JIM.EntraIdConnectorRegistration
        TenantId (string): The tenant's ID.
        ClientId (string): The application's client ID.
        ConnectedSystemId (int): The Connected System's ID in JIM.
        AuthenticationMethod (string): Certificate or Federated Credential.
        CertificateThumbprint (string): The thumbprint of a certificate this run made. Empty when it made none.
        CertificateExpires (datetime): When a certificate this run made expires. Empty when it made none.
        Permissions (string[]): The Microsoft Graph application permissions the application holds.
        Changes (string[]): The changes this run made. Empty when everything was already right.

        With -WhatIf, nothing is returned.

    .EXAMPLE
        Connect-JIM -Url 'https://jim.example.com'
        Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com'

        Signs in through the browser, creates the app registration with a certificate, and creates the Connected System.

    .EXAMPLE
        Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -WhatIf

        Lists what is missing or has drifted, and changes nothing.

    .EXAMPLE
        Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -PasswordManagement -AdministrativeUnitId '9d6f4c2e-1b3a-4e5f-8a7b-6c5d4e3f2a1b'

        Also lets JIM set passwords, assigning User Administrator over that Administrative Unit only.

    .EXAMPLE
        Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -TrackSourceOfAuthority

        Turns on tracking of objects whose Source of Authority is switched, keeping everything else the setup does.

    .EXAMPLE
        Register-JIMEntraIdConnector -TenantId 'contoso.onmicrosoft.com' -ManagedIdentityClientId '3b9f6a1e-2c4d-4e8f-9a0b-1c2d3e4f5a6b'

        Trusts the virtual machine's managed identity with a federated credential, so JIM holds no secret or key.

    .LINK
        Connect-JIM
        Get-JIMConnectedSystem
        Register-JIMIdentityProvider
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'SignIn')]
    [OutputType('JIM.EntraIdConnectorRegistration')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'SignIn')]
        [ValidateNotNullOrEmpty()]
        [string]$TenantId,

        [Parameter(ParameterSetName = 'SignIn')]
        [switch]$UseDeviceCode,

        [Parameter(Mandatory, ParameterSetName = 'Token')]
        [securestring]$AccessToken,

        [ValidateNotNullOrEmpty()]
        [string]$Name = 'JIM Entra ID Connector',

        [ValidateNotNullOrEmpty()]
        [string]$ConnectedSystemName = 'Microsoft Entra ID',

        [switch]$ReadOnly,

        [switch]$SkipHiddenMembership,

        [switch]$DirectoryExtensions,

        [switch]$PasswordManagement,

        [switch]$TrackSourceOfAuthority,

        [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
        [string]$AdministrativeUnitId,

        [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
        [string]$ManagedIdentityClientId,

        [ValidateRange(30, 730)]
        [int]$CertificateValidityDays = 365,

        [switch]$ResetCertificate
    )

    if (-not $script:JIMConnection) {
        throw 'You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again.'
    }
    # Refused before anyone is asked to sign in. Assigning the role needs the Administrative Unit; keeping password
    # management from an earlier run, with the switch left out, does not.
    if ($PasswordManagement -and -not $AdministrativeUnitId) {
        throw ('-PasswordManagement needs -AdministrativeUnitId: Microsoft Graph lets an application set passwords only while it holds the User Administrator role, ' +
            'which this command assigns over an Administrative Unit of the users JIM manages rather than across the tenant.')
    }

    $assignsRole = $PasswordManagement -and $AdministrativeUnitId
    $scopes = @($script:JIMEntraIdConnectorSignInScopes) + @(if ($assignsRole) { $script:JIMEntraIdConnectorRoleScope })
    $token = if ($AccessToken) { $AccessToken } else { Get-JIMEntraIdAdminToken -TenantId $TenantId -UseDeviceCode:$UseDeviceCode -Scopes $scopes }
    $connection = @{ Token = $token; TenantId = Get-JIMEntraIdTokenTenantId -AccessToken $token; CertificateValidityDays = $CertificateValidityDays }

    $current = Get-JIMEntraIdConnectorState -Connection $connection -Name $Name -ConnectedSystemName $ConnectedSystemName `
        -ManagedIdentityClientId $ManagedIdentityClientId -ResetCertificate:$ResetCertificate -PasswordManagement:$assignsRole
    # Each capability switch left out keeps what the setup already does.
    $bound = @{}
    foreach ($key in 'ReadOnly', 'SkipHiddenMembership', 'DirectoryExtensions', 'PasswordManagement', 'TrackSourceOfAuthority',
        'AdministrativeUnitId', 'ManagedIdentityClientId', 'ResetCertificate') {
        if ($PSBoundParameters.ContainsKey($key)) { $bound[$key] = $PSBoundParameters[$key] }
    }
    $options = Resolve-JIMEntraIdConnectorOption -Current $current -Bound $bound
    $plan = Get-JIMEntraIdConnectorPlan -Name $Name -ConnectedSystemName $ConnectedSystemName -Current $current -Now ([datetime]::UtcNow) @options

    $existing = @($current.Applications) | Select-Object -First 1
    if ($existing) { $connection.ApplicationId = $existing.Id; $connection.AppId = $existing.AppId }
    if ($current.ServicePrincipal) { $connection.ServicePrincipalId = $current.ServicePrincipal.Id }
    $connection.GraphServicePrincipalId = $current.GraphServicePrincipal.Id
    $connection.SettingIds = $current.ConnectorDefinition.SettingIds
    if ($current.ConnectedSystem) { $connection.ConnectedSystemId = $current.ConnectedSystem.Id }

    try {
        $made = Invoke-JIMRegistrationPlan -Plan $plan -Cmdlet $PSCmdlet -Apply { param($change) Invoke-JIMEntraIdConnectorChange -Connection $connection -Change $change }
    }
    catch {
        # The certificate's private key exists only in this run until JIM holds it.
        if ($connection.Certificate -and -not $connection.CertificateStored) {
            Write-Warning "The application was given a new certificate that JIM never received, so its private key is lost. Once the cause is fixed, run this again with -ResetCertificate."
        }
        throw
    }
    if (-not $made) { return }

    $result = [PSCustomObject]@{
        TenantId              = $connection.TenantId
        ClientId              = $connection.AppId
        ConnectedSystemId     = $connection.ConnectedSystemId
        AuthenticationMethod  = if ($options.ManagedIdentityClientId) { 'Federated Credential' } else { 'Certificate' }
        CertificateThumbprint = if ($connection.Certificate) { $connection.Certificate.Thumbprint }
        CertificateExpires    = if ($connection.Certificate) { $connection.Certificate.NotAfter }
        Permissions           = $plan.Permissions
        Changes               = $made.Changes
    }
    $result.PSObject.TypeNames.Insert(0, 'JIM.EntraIdConnectorRegistration')
    return $result
}
