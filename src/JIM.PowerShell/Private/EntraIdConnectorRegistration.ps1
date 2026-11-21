# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Setting up the Microsoft Entra ID Connector (#361), for Register-JIMEntraIdConnector.
#
# The app registration the Connector authenticates as is made through Microsoft Graph, as Register-JIMIdentityProvider
# makes JIM's sign-in registration: what the tenant and JIM hold is read once, the plan is worked out here (a pure
# function, tested without Graph or JIM), and each planned change is then applied on its own, so -WhatIf and -Confirm
# see each change separately.
#
# The Connector authenticates app-only, so its permissions are Microsoft Graph application permissions (app roles),
# granted to the application's service principal; that grant is admin consent. Its credential is a certificate made
# here, in memory, whose private key goes only to JIM, or a federated credential trusting a managed identity.

$script:JIMEntraIdConnectorSignInScopes = @(
    'https://graph.microsoft.com/Application.ReadWrite.All'
    'https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All'
    'https://graph.microsoft.com/User.Read'
)

# Only asked for with -PasswordManagement: assigning a directory role is a privileged act of its own.
$script:JIMEntraIdConnectorRoleScope = 'https://graph.microsoft.com/RoleManagement.ReadWrite.Directory'

$script:JIMEntraIdConnectorDefinitionName = 'JIM Microsoft Entra ID Connector'
$script:JIMEntraIdUserAdministratorRoleId = 'fe930be7-5e62-47db-91af-98c3a49a38b1'
$script:JIMEntraIdFederatedCredentialName = 'jim-managed-identity'
$script:JIMEntraIdTokenExchangeAudience = 'api://AzureADTokenExchange'

# A certificate is renewed when it has less than this long to run, which is also when the Connector starts warning.
$script:JIMEntraIdCertificateRenewalDays = 30

$script:JIMEntraIdTrackSourceOfAuthoritySetting = 'Track Source of Authority Changes'

function Resolve-JIMEntraIdConnectorOption {
    <#
    .SYNOPSIS
        Works out what the Connector is to be able to do: what the switches given ask for, and for each switch not
        given, what the existing setup already does, so running the command again without a switch never takes away
        what an earlier run, or an administrator in the portal, turned on.

    .PARAMETER Current
        What the tenant and JIM hold, as Get-JIMEntraIdConnectorState reads it.

    .PARAMETER Bound
        The capability switches and values the command was given, as $PSBoundParameters holds them.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)][object]$Current,
        [Parameter(Mandatory)][hashtable]$Bound
    )

    $roles = $Current.GraphServicePrincipal.Roles
    $existing = @($Current.Applications).Count -eq 1 -and $Current.ServicePrincipal
    $granted = @(if ($existing) { @($Current.GrantedRoles) | ForEach-Object AppRoleId })
    $holds = { param($permission) $roles.ContainsKey($permission) -and $roles[$permission] -in $granted }
    $settings = if ($Current.ConnectedSystem) { $Current.ConnectedSystem.Settings } else { @{} }

    $kept = @{
        ReadOnly               = [bool]($existing -and -not (& $holds 'User.ReadWrite.All') -and (& $holds 'User.Read.All'))
        SkipHiddenMembership   = [bool]($existing -and -not (& $holds 'Member.Read.Hidden'))
        DirectoryExtensions    = [bool](& $holds 'Directory.Read.All')
        PasswordManagement     = [bool](& $holds 'User-PasswordProfile.ReadWrite.All')
        TrackSourceOfAuthority = [bool]((& $holds 'User-OnPremisesSyncBehavior.ReadWrite.All') -or $settings[$script:JIMEntraIdTrackSourceOfAuthoritySetting] -eq $true)
    }
    $options = @{}
    foreach ($name in $kept.Keys) {
        $options[$name] = if ($Bound.ContainsKey($name)) { [bool]$Bound[$name] } else { $kept[$name] }
    }
    $options.ResetCertificate = [bool]$Bound['ResetCertificate']
    if ($Bound['AdministrativeUnitId']) { $options.AdministrativeUnitId = [string]$Bound['AdministrativeUnitId'] }

    # A managed identity given is used; otherwise the one the Connected System already authenticates with, unless a
    # certificate was asked for.
    if ($Bound['ManagedIdentityClientId']) {
        $options.ManagedIdentityClientId = [string]$Bound['ManagedIdentityClientId']
    }
    elseif (-not $options.ResetCertificate -and $settings['Authentication Method'] -eq 'Federated Credential' -and $settings['Managed Identity Client ID']) {
        $options.ManagedIdentityClientId = [string]$settings['Managed Identity Client ID']
    }
    return $options
}

function Get-JIMEntraIdConnectorPermission {
    <#
    .SYNOPSIS
        The Microsoft Graph application permissions the Connector needs for what it is asked to do, least privilege.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [switch]$ReadOnly,
        [switch]$SkipHiddenMembership,
        [switch]$DirectoryExtensions,
        [switch]$PasswordManagement,
        [switch]$TrackSourceOfAuthority
    )

    $permissions = [System.Collections.Generic.List[string]]::new()
    $permissions.Add($(if ($ReadOnly) { 'User.Read.All' } else { 'User.ReadWrite.All' }))
    $permissions.Add($(if ($ReadOnly) { 'Group.Read.All' } else { 'Group.ReadWrite.All' }))
    # Microsoft offers no read-only form of these, and they also allow switching an object's Source of Authority, so
    # they are asked for only where tracking it is wanted.
    if ($TrackSourceOfAuthority) {
        $permissions.Add('User-OnPremisesSyncBehavior.ReadWrite.All')
        $permissions.Add('Group-OnPremisesSyncBehavior.ReadWrite.All')
    }
    if (-not $SkipHiddenMembership) { $permissions.Add('Member.Read.Hidden') }
    if ($DirectoryExtensions) { $permissions.Add('Directory.Read.All') }
    if ($PasswordManagement) { $permissions.Add('User-PasswordProfile.ReadWrite.All') }
    return $permissions.ToArray()
}

function Get-JIMEntraIdConnectorPlan {
    <#
    .SYNOPSIS
        Works out what has to change in the tenant and in JIM for the Connector's registration and Connected System to
        be as they should be.

    .PARAMETER Current
        What the tenant and JIM hold, as Get-JIMEntraIdConnectorState reads it.

    .PARAMETER Now
        The current time, against which a certificate's remaining life is judged.

    .OUTPUTS
        The permissions asked for, by name, and the changes in the order they must be made.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ConnectedSystemName,
        [Parameter(Mandatory)][object]$Current,
        [Parameter(Mandatory)][datetime]$Now,
        [switch]$ReadOnly,
        [switch]$SkipHiddenMembership,
        [switch]$DirectoryExtensions,
        [switch]$PasswordManagement,
        [switch]$TrackSourceOfAuthority,
        [string]$AdministrativeUnitId,
        [string]$ManagedIdentityClientId,
        [switch]$ResetCertificate
    )

    if (-not $Current.ConnectorDefinition) {
        throw "JIM does not offer the $script:JIMEntraIdConnectorDefinitionName. Upgrade JIM to a version that includes it, then run this again."
    }
    $useManagedIdentity = [bool]$ManagedIdentityClientId
    if ($useManagedIdentity -and (-not $Current.ManagedIdentity -or $Current.ManagedIdentity.ClientId -ne $ManagedIdentityClientId)) {
        throw "The tenant holds no managed identity with the client ID $ManagedIdentityClientId. Check the -ManagedIdentityClientId is that of a user-assigned managed identity in this tenant."
    }

    $applications = @($Current.Applications)
    if ($applications.Count -gt 1) {
        throw "The tenant holds more than one application called '$Name' ($(($applications.AppId) -join ', ')). Rename or remove the ones that are not JIM's, or give -Name the one that is, and run this again."
    }
    $app = $applications | Select-Object -First 1
    $system = $Current.ConnectedSystem
    if ($system -and $system.ConnectorDefinitionId -ne $Current.ConnectorDefinition.Id) {
        throw "JIM already has a Connected System called '$ConnectedSystemName' that uses another Connector. Give -ConnectedSystemName a different name, and run this again."
    }

    $permissions = Get-JIMEntraIdConnectorPermission -ReadOnly:$ReadOnly -SkipHiddenMembership:$SkipHiddenMembership `
        -DirectoryExtensions:$DirectoryExtensions -PasswordManagement:$PasswordManagement -TrackSourceOfAuthority:$TrackSourceOfAuthority
    $roles = $Current.GraphServicePrincipal.Roles
    $roleIds = @($permissions | ForEach-Object {
        if (-not $roles.ContainsKey($_)) { throw "Microsoft Graph in this tenant offers no application permission called $_." }
        $roles[$_]
    })
    $graphAccess = @{ resourceAppId = $script:JIMEntraIdGraphAppId; resourceAccess = @($roleIds | ForEach-Object { @{ id = $_; type = 'Role' } }) }
    $changes = [System.Collections.Generic.List[object]]::new()

    # The application and its service principal.
    if (-not $app) {
        $changes.Add((New-JIMEntraIdChange -Action 'NewApplication' -Target $Name `
            -Description "Create the application, asking Microsoft Graph for $($permissions -join ', ')" `
            -Arguments @{ Application = @{ displayName = $Name; signInAudience = 'AzureADMyOrg'; requiredResourceAccess = @($graphAccess) } }))
        $changes.Add((New-JIMEntraIdChange -Action 'NewServicePrincipal' -Target $Name -Description 'Create its service principal, which the permissions are granted to'))
    }
    else {
        if (-not (Test-JIMSameSet @($app.GraphRoleIds) $roleIds)) {
            $changes.Add((New-JIMEntraIdChange -Action 'SetApplicationPermissions' -Target $Name `
                -Description "Ask Microsoft Graph for $($permissions -join ', ')" `
                -Arguments @{ RequiredResourceAccess = @($graphAccess) + @($app.OtherResourceAccess) }))
        }
        if (-not $Current.ServicePrincipal) {
            $changes.Add((New-JIMEntraIdChange -Action 'NewServicePrincipal' -Target $Name -Description 'Create its service principal, which the permissions are granted to'))
        }
    }

    # Admin consent: each application permission granted to the service principal, and none it no longer needs.
    $granted = @($Current.GrantedRoles)
    $nameOf = @{}
    foreach ($entry in $roles.GetEnumerator()) { $nameOf[$entry.Value] = $entry.Key }
    foreach ($roleId in $roleIds | Where-Object { $_ -notin @($granted.AppRoleId) }) {
        $changes.Add((New-JIMEntraIdChange -Action 'GrantPermission' -Target $Name `
            -Description "Grant $($nameOf[$roleId]) from Microsoft Graph, with admin consent" -Arguments @{ AppRoleId = $roleId }))
    }
    foreach ($grant in $granted | Where-Object { $_.AppRoleId -notin $roleIds }) {
        $label = if ($nameOf.ContainsKey($grant.AppRoleId)) { $nameOf[$grant.AppRoleId] } else { "the Microsoft Graph permission $($grant.AppRoleId)" }
        $changes.Add((New-JIMEntraIdChange -Action 'RevokePermission' -Target $Name `
            -Description "Revoke $label, which the Connector no longer needs" -Arguments @{ AssignmentId = $grant.Id }))
    }

    # The credential: a certificate made here, or a federated credential trusting the managed identity.
    $ours = @(if ($app) { @($app.Certificates) | Where-Object DisplayName -eq $Name })
    $newCertificate = $false
    $retire = @()
    if ($useManagedIdentity) {
        $issuer = "https://login.microsoftonline.com/$($Current.TenantId)/v2.0"
        $trusted = @(if ($app) { @($app.FederatedCredentials) }) |
            Where-Object { $_.Issuer -eq $issuer -and $_.Subject -eq $Current.ManagedIdentity.PrincipalId }
        if (-not $trusted) {
            $changes.Add((New-JIMEntraIdChange -Action 'NewFederatedCredential' -Target $Name `
                -Description "Trust the managed identity $ManagedIdentityClientId with a federated credential" `
                -Arguments @{ Credential = @{
                    name = $script:JIMEntraIdFederatedCredentialName; issuer = $issuer; subject = $Current.ManagedIdentity.PrincipalId
                    audiences = @($script:JIMEntraIdTokenExchangeAudience)
                    description = 'JIM''s Microsoft Entra ID Connector, signing in as the managed identity of the virtual machine it runs on.' } }))
        }
        $retire = @($ours | ForEach-Object KeyId)
    }
    else {
        $lasting = @($ours | Where-Object { $_.EndDateTime -gt $Now.AddDays($script:JIMEntraIdCertificateRenewalDays) })
        # The private key of a certificate the application already has cannot be read back, so a new Connected System
        # needs a new certificate.
        $systemHasCertificate = $system -and $(if ($system.PSObject.Properties['HasCertificate']) { $system.HasCertificate } else { $true })
        if ($ResetCertificate -or -not $system -or -not $systemHasCertificate -or $lasting.Count -eq 0) {
            $why = if ($ResetCertificate) { 'as asked' } elseif (-not $system) { 'for the new Connected System' }
                elseif (-not $systemHasCertificate) { 'since the Connected System holds none' }
                elseif ($ours.Count -eq 0) { 'since the application has none' } else { 'since the current one expires within 30 days' }
            $changes.Add((New-JIMEntraIdChange -Action 'NewCertificate' -Target $Name `
                -Description "Make a certificate and add it to the application, $why"))
            $newCertificate = $true
            $retire = @($ours | ForEach-Object KeyId)
        }
    }

    # The role is checked, and assigned, only when an Administrative Unit is named: reading role assignments needs a
    # sign-in that may manage them, which a re-run that only keeps password management does not ask for.
    if ($PasswordManagement -and $AdministrativeUnitId) {
        $scope = "/administrativeUnits/$AdministrativeUnitId"
        if (-not (@($Current.UserAdministratorAssignments) | Where-Object { $_.DirectoryScopeId -in '/', $scope })) {
            $changes.Add((New-JIMEntraIdChange -Action 'AssignUserAdministrator' -Target $Name `
                -Description "Assign it the User Administrator role over the Administrative Unit $AdministrativeUnitId, which Microsoft Graph requires to set passwords" `
                -Arguments @{ DirectoryScopeId = $scope }))
        }
    }

    # JIM's Connected System.
    $method = if ($useManagedIdentity) { 'Federated Credential' } else { 'Certificate' }
    $wanted = [ordered]@{ 'Tenant ID' = $Current.TenantId; 'Authentication Method' = $method }
    if ($useManagedIdentity) { $wanted['Managed Identity Client ID'] = $ManagedIdentityClientId }
    $wanted[$script:JIMEntraIdTrackSourceOfAuthoritySetting] = [bool]$TrackSourceOfAuthority
    if (-not $system) {
        $changes.Add((New-JIMEntraIdChange -Action 'NewConnectedSystem' -Target $ConnectedSystemName `
            -Description "Create the Connected System, authenticating with a $($method.ToLowerInvariant())" `
            -Arguments @{ ConnectorDefinitionId = $Current.ConnectorDefinition.Id; Settings = [hashtable]$wanted; SetsClientId = $true; SetsCertificate = $newCertificate }))
    }
    else {
        $settings = @{}
        # A tick box left unset reads as unticked.
        $differs = { param($entry) if ($entry.Value -is [bool]) { [bool]$system.Settings[$entry.Key] -ne $entry.Value } else { [string]$system.Settings[$entry.Key] -ne [string]$entry.Value } }
        foreach ($entry in $wanted.GetEnumerator() | Where-Object { & $differs $_ }) {
            $settings[$entry.Key] = $entry.Value
        }
        $setsClientId = -not $app -or [string]$system.Settings['Client ID'] -ne [string]$app.AppId
        if ($settings.Count -gt 0 -or $setsClientId -or $newCertificate) {
            $named = @($settings.Keys) + @(if ($setsClientId) { 'Client ID' }) + @(if ($newCertificate) { 'Certificate' })
            $changes.Add((New-JIMEntraIdChange -Action 'SetConnectedSystem' -Target $ConnectedSystemName `
                -Description "Set its $($named -join ', ')" `
                -Arguments @{ ConnectedSystemId = $system.Id; Settings = $settings; SetsClientId = $setsClientId; SetsCertificate = $newCertificate }))
        }
    }

    # Old certificates go only once JIM holds the credential that replaces them, so the Connector never loses access.
    if ($retire.Count -gt 0) {
        $changes.Add((New-JIMEntraIdChange -Action 'RetireCertificates' -Target $Name `
            -Description "Remove the certificate$(if ($retire.Count -gt 1) { 's' }) it no longer uses" -Arguments @{ KeyIds = $retire }))
    }

    [PSCustomObject]@{
        Permissions = $permissions
        Changes     = $changes.ToArray()
    }
}

function New-JIMEntraIdConnectorCertificate {
    <#
    .SYNOPSIS
        Makes the self-signed certificate the Connector signs its client assertions with, in memory.

    .OUTPUTS
        The PFX, Base64-encoded, as a secure string, for JIM only; the public certificate as a Graph keyCredential, for
        the application; and its thumbprint and expiry. The PFX has no password: JIM encrypts it at rest, and nothing else
        ever holds it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$ValidityDays
    )

    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=$Name", $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
        $notAfter = [DateTimeOffset]::UtcNow.AddDays($ValidityDays)
        $certificate = $request.CreateSelfSigned($notBefore, $notAfter)
        try {
            $pfxBytes = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx)
            try {
                $pfx = ConvertTo-SecureString -String ([Convert]::ToBase64String($pfxBytes)) -AsPlainText -Force
            }
            finally {
                [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)
            }

            [PSCustomObject]@{
                Pfx           = $pfx
                Thumbprint    = $certificate.Thumbprint
                NotAfter      = $notAfter.UtcDateTime
                KeyCredential = @{
                    type = 'AsymmetricX509Cert'; usage = 'Verify'; displayName = $Name
                    key = [Convert]::ToBase64String($certificate.RawData)
                    startDateTime = $notBefore.UtcDateTime.ToString('o'); endDateTime = $notAfter.UtcDateTime.ToString('o')
                }
            }
        }
        finally {
            $certificate.Dispose()
        }
    }
    finally {
        $rsa.Dispose()
    }
}

function Get-JIMEntraIdConnectorState {
    <#
    .SYNOPSIS
        Reads what the tenant and JIM hold that the Connector's registration depends on.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Connection,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ConnectedSystemName,
        [string]$ManagedIdentityClientId,
        [switch]$ResetCertificate,
        [switch]$PasswordManagement
    )

    # JIM first: the Connected System says which managed identity it already authenticates with, if any.
    $definition = @(Invoke-JIMApi -Endpoint '/api/v1/synchronisation/connector-definitions') | Where-Object name -eq $script:JIMEntraIdConnectorDefinitionName | Select-Object -First 1
    $connectorDefinition = $null
    if ($definition) {
        $detail = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connector-definitions/$($definition.id)"
        $settingIds = @{}
        foreach ($setting in @($detail.settings)) { $settingIds[[string]$setting.name] = [int]$setting.id }
        $connectorDefinition = [PSCustomObject]@{ Id = [int]$definition.id; SettingIds = $settingIds }
    }

    $system = $null
    $found = @(Invoke-JIMApi -Endpoint '/api/v1/synchronisation/connected-systems') | ForEach-Object { if ($_.PSObject.Properties['items']) { $_.items } else { $_ } } |
        Where-Object name -eq $ConnectedSystemName | Select-Object -First 1
    if ($found) {
        $detail = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$($found.id)"
        # A tick box comes back as checkboxValue; an encrypted setting only as whether a value is held.
        $settings = @{}
        $held = @{}
        foreach ($value in @($detail.settingValues)) {
            $settings[[string]$value.name] = if ($null -ne $value.checkboxValue) { [bool]$value.checkboxValue } else { [string]$value.stringValue }
            $held[[string]$value.name] = [bool]$value.hasValue
        }
        $system = [PSCustomObject]@{
            Id = [int]$found.id; ConnectorDefinitionId = [int]$detail.connector.id; Settings = $settings
            HasCertificate = [bool]$held['Certificate'] }
    }
    if (-not $ManagedIdentityClientId -and -not $ResetCertificate -and $system -and $system.Settings['Authentication Method'] -eq 'Federated Credential') {
        $ManagedIdentityClientId = [string]$system.Settings['Managed Identity Client ID']
    }

    $graph = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/servicePrincipals?`$filter=appId eq '$script:JIMEntraIdGraphAppId'&`$select=id,appRoles").value) | Select-Object -First 1
    if (-not $graph) { throw 'The tenant has no service principal for Microsoft Graph, which every tenant has; check the token is for the right tenant.' }
    $roles = @{}
    foreach ($role in @($graph.appRoles) | Where-Object { $_.allowedMemberTypes -contains 'Application' }) { $roles[[string]$role.value] = [string]$role.id }

    $filter = [uri]::EscapeDataString("displayName eq '$($Name.Replace("'", "''"))'")
    $applications = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/applications?`$filter=$filter&`$select=id,appId,displayName,signInAudience,requiredResourceAccess").value | ForEach-Object {
        $graphAccess = @($_.requiredResourceAccess | Where-Object resourceAppId -eq $script:JIMEntraIdGraphAppId)
        # A key credential's dates come with it in a list; its key is read only when one is to be replaced.
        $keys = (Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/applications/$($_.id)?`$select=keyCredentials").keyCredentials
        $federated = (Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/applications/$($_.id)/federatedIdentityCredentials").value
        [PSCustomObject]@{
            Id                   = [string]$_.id
            AppId                = [string]$_.appId
            DisplayName          = [string]$_.displayName
            SignInAudience       = [string]$_.signInAudience
            GraphRoleIds         = @($graphAccess.resourceAccess | Where-Object type -eq 'Role' | ForEach-Object { [string]$_.id })
            OtherResourceAccess  = @($_.requiredResourceAccess | Where-Object resourceAppId -ne $script:JIMEntraIdGraphAppId)
            Certificates         = @($keys | ForEach-Object { [PSCustomObject]@{ KeyId = [string]$_.keyId; DisplayName = [string]$_.displayName; EndDateTime = [datetime]$_.endDateTime } })
            FederatedCredentials = @($federated | ForEach-Object { [PSCustomObject]@{ Id = [string]$_.id; Issuer = [string]$_.issuer; Subject = [string]$_.subject } })
        }
    })

    $servicePrincipal = $null
    $granted = @()
    $assignments = @()
    if ($applications.Count -eq 1) {
        $sp = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/servicePrincipals?`$filter=appId eq '$($applications[0].AppId)'&`$select=id").value) | Select-Object -First 1
        if ($sp) {
            $servicePrincipal = [PSCustomObject]@{ Id = [string]$sp.id }
            $granted = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/servicePrincipals/$($sp.id)/appRoleAssignments").value |
                Where-Object resourceId -eq $graph.id | ForEach-Object { [PSCustomObject]@{ Id = [string]$_.id; AppRoleId = [string]$_.appRoleId } })
            if ($PasswordManagement) {
                $assignments = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/roleManagement/directory/roleAssignments?`$filter=principalId eq '$($sp.id)' and roleDefinitionId eq '$script:JIMEntraIdUserAdministratorRoleId'").value |
                    ForEach-Object { [PSCustomObject]@{ Id = [string]$_.id; DirectoryScopeId = [string]$_.directoryScopeId } })
            }
        }
    }

    $managedIdentity = $null
    if ($ManagedIdentityClientId) {
        $mi = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/servicePrincipals?`$filter=appId eq '$ManagedIdentityClientId'&`$select=id,servicePrincipalType").value) | Select-Object -First 1
        if ($mi) { $managedIdentity = [PSCustomObject]@{ ClientId = $ManagedIdentityClientId; PrincipalId = [string]$mi.id } }
    }


    [PSCustomObject]@{
        TenantId                     = $Connection.TenantId
        Applications                 = $applications
        ServicePrincipal             = $servicePrincipal
        GrantedRoles                 = $granted
        GraphServicePrincipal        = [PSCustomObject]@{ Id = [string]$graph.id; Roles = $roles }
        ManagedIdentity              = $managedIdentity
        UserAdministratorAssignments = $assignments
        ConnectorDefinition          = $connectorDefinition
        ConnectedSystem              = $system
    }
}

function Invoke-JIMEntraIdConnectorChange {
    <#
    .SYNOPSIS
        Makes one planned change, through Microsoft Graph or JIM.

    .DESCRIPTION
        The connection carries what one change learns for the next: the application's object id and client ID, its
        service principal id, Graph's service principal id, the Connected System's id, and the certificate just made,
        whose PFX goes only to JIM and is dropped from the connection once JIM holds it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Connection,
        [Parameter(Mandatory)][object]$Change
    )

    $arguments = $Change.Arguments
    switch ($Change.Action) {
        'NewApplication' {
            $app = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path '/applications' -Body $arguments.Application
            $Connection.ApplicationId = [string]$app.id
            $Connection.AppId = [string]$app.appId
        }
        'SetApplicationPermissions' {
            $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Patch -Path "/applications/$($Connection.ApplicationId)" -Body @{ requiredResourceAccess = $arguments.RequiredResourceAccess }
        }
        'NewServicePrincipal' {
            $sp = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path '/servicePrincipals' -Body @{ appId = $Connection.AppId }
            $Connection.ServicePrincipalId = [string]$sp.id
        }
        'GrantPermission' {
            # A service principal just made can take a few seconds to replicate before Graph will grant it anything.
            Invoke-JIMEntraIdWithRetry {
                $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path "/servicePrincipals/$($Connection.ServicePrincipalId)/appRoleAssignments" -Body @{
                    principalId = $Connection.ServicePrincipalId; resourceId = $Connection.GraphServicePrincipalId; appRoleId = $arguments.AppRoleId }
            }
        }
        'RevokePermission' {
            $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Delete -Path "/servicePrincipals/$($Connection.ServicePrincipalId)/appRoleAssignments/$($arguments.AssignmentId)"
        }
        'NewCertificate' {
            $made = New-JIMEntraIdConnectorCertificate -Name $Change.Target -ValidityDays $Connection.CertificateValidityDays
            # Graph replaces the whole list, so the certificates the application has go back with the new one; only a
            # request for one application returns their keys.
            $existing = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/applications/$($Connection.ApplicationId)?`$select=keyCredentials").keyCredentials)
            $keep = @($existing | ForEach-Object {
                @{ keyId = $_.keyId; type = $_.type; usage = $_.usage; key = $_.key; displayName = $_.displayName; startDateTime = $_.startDateTime; endDateTime = $_.endDateTime } })
            $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Patch -Path "/applications/$($Connection.ApplicationId)" -Body @{ keyCredentials = @($keep) + @($made.KeyCredential) }
            $Connection.Certificate = $made
        }
        'NewFederatedCredential' {
            $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path "/applications/$($Connection.ApplicationId)/federatedIdentityCredentials" -Body $arguments.Credential
        }
        'AssignUserAdministrator' {
            Invoke-JIMEntraIdWithRetry {
                $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Post -Path '/roleManagement/directory/roleAssignments' -Body @{
                    principalId = $Connection.ServicePrincipalId; roleDefinitionId = $script:JIMEntraIdUserAdministratorRoleId; directoryScopeId = $arguments.DirectoryScopeId }
            }
        }
        { $_ -in 'NewConnectedSystem', 'SetConnectedSystem' } {
            $settingIds = $Connection.SettingIds
            if ($Change.Action -eq 'NewConnectedSystem') {
                $created = Invoke-JIMApi -Endpoint '/api/v1/synchronisation/connected-systems' -Method 'POST' -Body @{
                    name = $Change.Target; connectorDefinitionId = $arguments.ConnectorDefinitionId
                    description = 'Microsoft Entra ID users and groups, through Microsoft Graph.'; changeReason = 'Set up by Register-JIMEntraIdConnector' }
                $Connection.ConnectedSystemId = [int]$created.id
            }
            $values = @{}
            foreach ($entry in $arguments.Settings.GetEnumerator()) {
                $values[[string]$settingIds[$entry.Key]] = if ($entry.Value -is [bool]) { @{ checkboxValue = $entry.Value } } else { @{ stringValue = [string]$entry.Value } }
            }
            if ($arguments.SetsClientId) { $values[[string]$settingIds['Client ID']] = @{ stringValue = $Connection.AppId } }
            if ($arguments.SetsCertificate) {
                $values[[string]$settingIds['Certificate']] = @{ stringValue = [System.Net.NetworkCredential]::new('', $Connection.Certificate.Pfx).Password }
            }
            # JIM tests the connection as it saves the credential, and Entra ID can take a minute or two to honour a
            # credential and permissions granted moments ago, so a refusal that only says so is tried again.
            $body = @{ settingValues = $values; changeReason = 'Set up by Register-JIMEntraIdConnector' }
            for ($attempt = 1; ; $attempt++) {
                try {
                    $null = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$($Connection.ConnectedSystemId)" -Method 'PUT' -Body $body
                    break
                }
                catch {
                    if ($attempt -ge 9 -or -not (Test-JIMEntraIdPropagationRefusal -Message "$($_.Exception.Message) $($_.ErrorDetails.Message)")) { throw }
                    if ($attempt -eq 1) { Write-Warning 'Microsoft Entra ID is still applying the new credential and permissions; JIM will try the connection again for up to two minutes.' }
                    Start-Sleep -Seconds 15
                }
            }
            if ($arguments.SetsCertificate) {
                $Connection.Certificate.Pfx = $null
                $Connection.CertificateStored = $true
            }
        }
        'RetireCertificates' {
            $existing = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/applications/$($Connection.ApplicationId)?`$select=keyCredentials").keyCredentials)
            $keep = @($existing | Where-Object { $_.keyId -notin $arguments.KeyIds } | ForEach-Object {
                @{ keyId = $_.keyId; type = $_.type; usage = $_.usage; key = $_.key; displayName = $_.displayName; startDateTime = $_.startDateTime; endDateTime = $_.endDateTime } })
            $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Patch -Path "/applications/$($Connection.ApplicationId)" -Body @{ keyCredentials = @($keep) }
        }
        default { throw "Unknown change: $($Change.Action)" }
    }
}

function Test-JIMEntraIdPropagationRefusal {
    <#
    .SYNOPSIS
        Whether JIM refused the Connected System's settings only because Entra ID has not yet applied a credential or
        permissions granted moments ago: a token with no permissions yet, or a certificate or federated credential the
        token service does not know yet.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Message)

    return $Message -match 'carries no Microsoft Graph application permissions|cannot read users or groups|AADSTS700027|AADSTS700213|AADSTS70021\b|AADSTS7000215|AADSTS700016'
}

function Invoke-JIMEntraIdWithRetry {
    # Graph is eventually consistent: an object made a moment ago can be refused as unknown for a few seconds.
    param([Parameter(Mandatory)][scriptblock]$Action)

    for ($attempt = 1; ; $attempt++) {
        try {
            return & $Action
        }
        catch {
            if ($attempt -ge 6) { throw }
            Write-Verbose "Microsoft Graph refused the change, possibly while replicating; trying again: $($_.Exception.Message)"
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}
