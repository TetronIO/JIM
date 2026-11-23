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
    $granted = @(if ($existing) { @($Current.GrantedRoles) | ForEach-Object { $_.AppRoleId } })
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
    # The User Administrator role is read, and assigned or removed, only when -PasswordManagement is given either way:
    # reading role assignments needs a sign-in that may manage them, which a run that only keeps it does not ask for.
    $options.CheckRole = $Bound.ContainsKey('PasswordManagement')

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
        [switch]$CheckRole,
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
        $retire = @($ours | ForEach-Object { $_.KeyId })
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
            $retire = @($ours | ForEach-Object { $_.KeyId })
        }
    }

    # The User Administrator role, across the tenant: Microsoft Graph sets a password for an application only while it
    # holds the role. Checked only when -PasswordManagement is given either way (CheckRole), since reading role
    # assignments needs a sign-in that may manage them; with it off, the role is removed with the permission.
    if ($CheckRole) {
        $assignments = @($Current.UserAdministratorAssignments)
        if ($PasswordManagement -and -not ($assignments | Where-Object { $_.DirectoryScopeId -eq '/' })) {
            $changes.Add((New-JIMEntraIdChange -Action 'AssignUserAdministrator' -Target $Name `
                -Description 'Assign it the User Administrator role across the tenant, which Microsoft Graph requires to set passwords' `
                -Arguments @{ DirectoryScopeId = '/' }))
        }
        if (-not $PasswordManagement) {
            foreach ($assignment in $assignments) {
                $changes.Add((New-JIMEntraIdChange -Action 'RemoveUserAdministrator' -Target $Name `
                    -Description 'Remove its User Administrator role, which the Connector no longer needs' `
                    -Arguments @{ AssignmentId = $assignment.Id }))
            }
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
        # Whole seconds: a certificate keeps its dates to the second, and Microsoft Graph refuses a keyCredential whose
        # end date is later than the certificate's, which fractional seconds would make it.
        $now = [DateTimeOffset]::FromUnixTimeSeconds([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
        $notBefore = $now.AddMinutes(-5)
        $notAfter = $now.AddDays($ValidityDays)
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
        [switch]$CheckRole
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
            if ($CheckRole) {
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
            # request for one application returns their keys. Each goes back exactly as Graph returned it: one with any
            # property changed or missing (customKeyIdentifier, say) is refused as an edit, KeyNotUpdatable.
            $keep = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/applications/$($Connection.ApplicationId)?`$select=keyCredentials").keyCredentials)
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
        'RemoveUserAdministrator' {
            $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Delete -Path "/roleManagement/directory/roleAssignments/$($arguments.AssignmentId)"
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
            # Entra ID applies a new certificate unevenly, accepting it and then refusing it again for minutes, so JIM is
            # given it only once Entra ID has accepted it consistently; until then JIM keeps the credential it had.
            if ($arguments.SetsCertificate) { $null = Wait-JIMEntraIdCertificateApplied -Connection $Connection }
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
            # The rest go back exactly as Graph returned them, as for a new certificate.
            $existing = @((Invoke-JIMEntraIdGraphApi -Connection $Connection -Path "/applications/$($Connection.ApplicationId)?`$select=keyCredentials").keyCredentials)
            $keep = @($existing | Where-Object { $_.keyId -notin $arguments.KeyIds })
            $null = Invoke-JIMEntraIdGraphApi -Connection $Connection -Method Patch -Path "/applications/$($Connection.ApplicationId)" -Body @{ keyCredentials = @($keep) }
        }
        default { throw "Unknown change: $($Change.Action)" }
    }
}

function Wait-JIMEntraIdCertificateApplied {
    <#
    .SYNOPSIS
        Waits until Microsoft Entra ID has accepted the application's new certificate on every request for a settling
        time, and says whether it did.

    .DESCRIPTION
        Entra ID applies a newly uploaded certificate unevenly: it has been seen accepting one, then refusing it on every
        request from three to six minutes after the upload (#3053). A run that asks for a token in that window waits up to
        ten minutes itself, but a JIM that starts working with a certificate Entra ID has settled on is better. Each check
        signs in as the application from here, not from JIM, so it cannot prove that the Entra ID servers JIM reaches
        have the certificate; it shortens the wait JIM's own runs may still meet. A refusal waiting will not cure ends the
        wait at once, leaving JIM's connection test, which follows, to report it.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Reads only: it signs in and changes nothing.')]
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][hashtable]$Connection,
        [int]$SettleSeconds = 180,
        [int]$IntervalSeconds = 15,
        [int]$TimeoutSeconds = 900
    )

    # Counted in requests rather than read off the clock: the requests are what Entra ID answers.
    $needed = [int][Math]::Ceiling($SettleSeconds / $IntervalSeconds) + 1
    $attempts = [int][Math]::Ceiling($TimeoutSeconds / $IntervalSeconds) + 1
    $activity = 'Waiting for Microsoft Entra ID to apply the new certificate'
    $streak = 0
    $warned = $false
    try {
        for ($attempt = 1; $attempt -le $attempts; $attempt++) {
            if ($attempt -gt 1) { Start-Sleep -Seconds $IntervalSeconds }
            $answer = Request-JIMEntraIdCertificateToken -TenantId $Connection.TenantId -ClientId $Connection.AppId -Pfx $Connection.Certificate.Pfx
            if ($answer.Accepted) {
                $streak++
                if ($streak -ge $needed) { return $true }
            }
            elseif ($answer.StillApplying) {
                $streak = 0
                if (-not $warned) {
                    Write-Warning "Microsoft Entra ID is still applying the new certificate. Waiting until it accepts it on every request for $SettleSeconds seconds, for up to $([Math]::Round($TimeoutSeconds / 60)) minutes."
                    $warned = $true
                }
            }
            else {
                Write-Verbose "Microsoft Entra ID refused the new certificate in a way waiting will not cure; JIM's connection test will report it: $($answer.Reason)"
                return $false
            }
            Write-Progress -Activity $activity -Status "Accepted on $streak of the $needed requests in a row it needs" -PercentComplete ([Math]::Min(100, [int](100 * $streak / $needed)))
        }
    }
    finally {
        Write-Progress -Activity $activity -Completed
    }

    Write-Warning "Microsoft Entra ID was still refusing the new certificate at times after $([Math]::Round($TimeoutSeconds / 60)) minutes. JIM is given it now; a run that is refused it keeps asking for up to ten minutes, and if Entra ID still refuses it then, run this again with -ResetCertificate."
    return $false
}

function Request-JIMEntraIdCertificateToken {
    <#
    .SYNOPSIS
        Signs in to Microsoft Entra ID as the application with its certificate, as JIM does, and says whether Entra ID
        accepted it, and if not, whether the refusal is one Entra ID gives while still applying a new certificate.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$ClientId,
        [Parameter(Mandatory)][securestring]$Pfx
    )

    $tokenEndpoint = "https://login.microsoftonline.com/$([uri]::EscapeDataString($TenantId))/oauth2/v2.0/token"
    $body = @{
        client_id = $ClientId; grant_type = 'client_credentials'; scope = 'https://graph.microsoft.com/.default'
        client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
        client_assertion = New-JIMEntraIdClientAssertion -TokenEndpoint $tokenEndpoint -ClientId $ClientId -Pfx $Pfx
    }
    try {
        $response = Invoke-WebRequest -Method Post -Uri $tokenEndpoint -Body $body -ContentType 'application/x-www-form-urlencoded' -SkipHttpErrorCheck -ErrorAction Stop
    }
    catch [System.Net.Http.HttpRequestException] {
        return [PSCustomObject]@{ Accepted = $false; StillApplying = $false; Reason = "Could not reach Microsoft Entra ID: $($_.Exception.Message)" }
    }

    $content = $null
    try { $content = $response.Content | ConvertFrom-Json -ErrorAction Stop } catch [System.ArgumentException] { $content = $null }
    if ([int]$response.StatusCode -eq 200 -and $content -and $content.PSObject.Properties['access_token']) {
        return [PSCustomObject]@{ Accepted = $true; StillApplying = $false; Reason = $null }
    }
    # The description only: Entra ID's error bodies echo nothing secret, but the assertion stays out of any message.
    $reason = if ($content -and $content.PSObject.Properties['error_description']) { ([string]$content.error_description).Split([char]10)[0].Trim() } else { "HTTP $([int]$response.StatusCode)" }
    return [PSCustomObject]@{ Accepted = $false; StillApplying = ($reason -match 'AADSTS700027\b'); Reason = $reason }
}

function New-JIMEntraIdClientAssertion {
    <#
    .SYNOPSIS
        A client assertion: a JSON Web Token signed with the certificate's private key, which proves to Entra ID that
        the caller holds a certificate on the application. Valid for ten minutes.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds a value in memory; changes nothing.')]
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$TokenEndpoint,
        [Parameter(Mandatory)][string]$ClientId,
        [Parameter(Mandatory)][securestring]$Pfx
    )

    $encode = { param([byte[]]$Bytes) [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
    $pfxBytes = [Convert]::FromBase64String([System.Net.NetworkCredential]::new('', $Pfx).Password)
    try {
        # An ephemeral key never reaches a key store; macOS cannot load one, so there the default is used.
        $flags = if ($IsMacOS) { [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet }
            else { [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet }
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxBytes, [string]$null, $flags)
    }
    finally {
        [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)
    }
    try {
        $header = [ordered]@{ alg = 'RS256'; typ = 'JWT'; 'x5t#S256' = (& $encode ([System.Security.Cryptography.SHA256]::HashData($certificate.RawData))) } | ConvertTo-Json -Compress
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $payload = [ordered]@{
            aud = $TokenEndpoint; iss = $ClientId; sub = $ClientId
            jti = [Guid]::NewGuid().ToString(); nbf = $now; iat = $now; exp = $now + 600
        } | ConvertTo-Json -Compress
        $unsigned = "$(& $encode ([System.Text.Encoding]::UTF8.GetBytes($header))).$(& $encode ([System.Text.Encoding]::UTF8.GetBytes($payload)))"
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        try {
            $signature = $rsa.SignData([System.Text.Encoding]::ASCII.GetBytes($unsigned), [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        }
        finally {
            $rsa.Dispose()
        }
        return "$unsigned.$(& $encode $signature)"
    }
    finally {
        $certificate.Dispose()
    }
}

function Test-JIMEntraIdPropagationRefusal {
    <#
    .SYNOPSIS
        Whether JIM refused the Connected System's settings only because Entra ID has not yet applied a credential or
        permissions granted moments ago: a token with no permissions yet, or a certificate or federated credential the
        token service does not know yet, or a
        service principal Microsoft Graph does not know yet (Authorization_IdentityNotFound).
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Message)

    return $Message -match 'carries no Microsoft Graph application permissions|cannot read users or groups|AADSTS700027|AADSTS700213|AADSTS70021\b|AADSTS7000215|AADSTS700016|Authorization_IdentityNotFound'
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
