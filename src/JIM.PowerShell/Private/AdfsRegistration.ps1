# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Registering JIM with AD FS (#2364), for Register-JIMIdentityProvider -Provider ADFS.
#
# The ADFS module is Windows PowerShell only and ships with the federation server, so everything that touches AD FS
# runs in a Windows PowerShell session: on the federation server itself, or one the caller opened to it. What AD FS
# holds is read once, the plan is worked out here (a pure function, tested without AD FS), and each planned change is
# then applied on its own, so -WhatIf and -Confirm see each change separately.

# JIM's loopback redirect URIs: Invoke-OAuthBrowserFlow listens on the first free port from 8400, trying ten.
$script:JIMLoopbackPorts = 8400..8409

# What each client may ask the API for. The portal reads the person from its ID token, so it asks for allatclaims to
# have the API's claim rules (the identifier, name and email) put there too; the PowerShell module reads the access
# token, which carries them anyway, and needs offline_access for a refresh token (#2355).
$script:JIMAdfsPortalScopes = @('openid', 'profile', 'email', 'allatclaims')
$script:JIMAdfsModuleScopes = @('openid', 'profile', 'offline_access')

# What each planned action runs on the federation server. A change's Arguments are that cmdlet's parameters.
$script:JIMAdfsActionCommands = @{
    NewApplicationGroup  = 'New-AdfsApplicationGroup'
    NewServerApplication = 'Add-AdfsServerApplication'
    SetServerApplication = 'Set-AdfsServerApplication'
    ResetClientSecret    = 'Set-AdfsServerApplication'
    NewNativeApplication = 'Add-AdfsNativeClientApplication'
    SetNativeApplication = 'Set-AdfsNativeClientApplication'
    NewWebApiApplication = 'Add-AdfsWebApiApplication'
    SetWebApiApplication = 'Set-AdfsWebApiApplication'
    GrantPermission      = 'Grant-AdfsApplicationPermission'
    SetPermission        = 'Set-AdfsApplicationPermission'
    RevokePermission     = 'Revoke-AdfsApplicationPermission'
}

function Get-JIMAdfsIssuanceTransformRule {
    <#
    .SYNOPSIS
        The API's one claim rule: the person's objectGUID as oid, and their profile, in a single directory lookup.

    .DESCRIPTION
        JIM keys on objectGUID because it survives renames and is never reused (#2355). The Active Directory claims
        provider sends none of these on its own, so one LDAP attribute store rule fetches them all.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    @'
@RuleName = "JIM: identifier and profile from Active Directory"
c:[Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/windowsaccountname", Issuer == "AD AUTHORITY"]
 => issue(store = "Active Directory", types = ("oid", "name", "given_name", "family_name", "email", "preferred_username"), query = ";objectGUID,displayName,givenName,sn,mail,userPrincipalName;{0}", param = c.Value);
'@
}

function Test-JIMAdfsSameSet {
    # Whether two lists hold the same values, in any order.
    param([AllowNull()][object[]]$Left, [AllowNull()][object[]]$Right)

    $l = @($Left | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ } | Sort-Object -Unique -CaseSensitive)
    $r = @($Right | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ } | Sort-Object -Unique -CaseSensitive)
    if ($l.Count -ne $r.Count) { return $false }
    for ($i = 0; $i -lt $l.Count; $i++) {
        if ($l[$i] -cne $r[$i]) { return $false }
    }
    return $true
}

function Select-JIMAdfsApplication {
    # The group's application of one kind: the one with the expected name, or the only one there is, so a group made
    # by hand from the guide is adopted rather than duplicated.
    param([object[]]$Applications, [string]$ExpectedName, [string]$Kind, [string]$GroupName)

    $applications = @($Applications | Where-Object { $null -ne $_ })
    $named = @($applications | Where-Object Name -eq $ExpectedName)
    if ($named.Count -gt 0) { return $named[0] }
    if ($applications.Count -eq 1) { return $applications[0] }
    if ($applications.Count -gt 1) {
        $names = ($applications | ForEach-Object { "'$($_.Name)'" }) -join ', '
        throw "The Application Group '$GroupName' holds more than one $Kind ($names) and none is called '$ExpectedName'. Rename the one JIM uses to '$ExpectedName', or remove the others, and run this again."
    }
    return $null
}

function New-JIMAdfsChange {
    param([string]$Action, [string]$Target, [string]$Description, [hashtable]$Arguments)

    [PSCustomObject]@{ Action = $Action; Target = $Target; Description = $Description; Arguments = $Arguments }
}

function Get-JIMAdfsRegistrationPlan {
    <#
    .SYNOPSIS
        Works out what has to change on AD FS for JIM's registration to be as it should be.

    .PARAMETER Url
        JIM's address, as people reach it.

    .PARAMETER Name
        The Application Group's name, which is also its identifier.

    .PARAMETER Current
        What AD FS holds, as Get-JIMAdfsRegistrationState reads it.

    .PARAMETER ResetClientSecret
        Plan a new client secret for the portal even if it has one.

    .OUTPUTS
        The client identifiers and API identifier the registration has, or will have, and the changes in the order
        they must be made.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][object]$Current,
        [switch]$ResetClientSecret
    )

    $baseUrl = $Url.TrimEnd('/')
    $serverName = "$Name - Portal"
    $nativeName = "$Name - PowerShell"
    $apiName = "$Name - API"
    $serverRedirects = @("$baseUrl/signin-oidc", "$baseUrl/signout-callback-oidc")
    $nativeRedirects = @($script:JIMLoopbackPorts | ForEach-Object { "http://localhost:$_/callback/" })
    $rules = Get-JIMAdfsIssuanceTransformRule
    $changes = [System.Collections.Generic.List[object]]::new()

    if (-not $Current.GroupExists) {
        $changes.Add((New-JIMAdfsChange -Action 'NewApplicationGroup' -Target $Name -Description 'Create the Application Group' `
            -Arguments @{ Name = $Name; ApplicationGroupIdentifier = $Name }))
    }

    # The portal: a confidential client, whose identifier is JIM_SSO_CLIENT_ID.
    $server = Select-JIMAdfsApplication -Applications $Current.ServerApplications -ExpectedName $serverName -Kind 'Server application' -GroupName $Name
    if ($server) {
        $clientId = [string]$server.Identifier
        if (-not (Test-JIMAdfsSameSet $server.RedirectUri $serverRedirects)) {
            $changes.Add((New-JIMAdfsChange -Action 'SetServerApplication' -Target $server.Name `
                -Description "Set the redirect URIs to $($serverRedirects -join ', ')" `
                -Arguments @{ TargetIdentifier = $clientId; RedirectUri = $serverRedirects }))
        }
        if ($ResetClientSecret) {
            $changes.Add((New-JIMAdfsChange -Action 'ResetClientSecret' -Target $server.Name -Description 'Replace the client secret' `
                -Arguments @{ TargetIdentifier = $clientId; ResetClientSecret = $true; PassThru = $true }))
        }
    }
    else {
        $clientId = [guid]::NewGuid().ToString()
        $changes.Add((New-JIMAdfsChange -Action 'NewServerApplication' -Target $serverName `
            -Description 'Add the portal as a Server application, with a new client secret' `
            -Arguments @{
                ApplicationGroupIdentifier = $Name; Name = $serverName; Identifier = $clientId; RedirectUri = $serverRedirects
                GenerateClientSecret = $true; PassThru = $true
            }))
    }

    # The PowerShell module: a public client, whose identifier is JIM_SSO_PUBLIC_CLIENT_ID.
    $native = Select-JIMAdfsApplication -Applications $Current.NativeApplications -ExpectedName $nativeName -Kind 'Native application' -GroupName $Name
    if ($native) {
        $publicClientId = [string]$native.Identifier
        if (-not (Test-JIMAdfsSameSet $native.RedirectUri $nativeRedirects)) {
            $changes.Add((New-JIMAdfsChange -Action 'SetNativeApplication' -Target $native.Name `
                -Description "Set the redirect URIs to the loopback ports $($script:JIMLoopbackPorts[0]) to $($script:JIMLoopbackPorts[-1])" `
                -Arguments @{ TargetIdentifier = $publicClientId; RedirectUri = $nativeRedirects }))
        }
    }
    else {
        $publicClientId = [guid]::NewGuid().ToString()
        $changes.Add((New-JIMAdfsChange -Action 'NewNativeApplication' -Target $nativeName `
            -Description 'Add the PowerShell module as a Native application' `
            -Arguments @{ ApplicationGroupIdentifier = $Name; Name = $nativeName; Identifier = $publicClientId; RedirectUri = $nativeRedirects }))
    }

    # The API, whose identifier is JIM_SSO_API_SCOPE and the audience of JIM's bearer tokens.
    $apiIdentifier = "api://$clientId"
    $api = Select-JIMAdfsApplication -Applications $Current.WebApiApplications -ExpectedName $apiName -Kind 'Web API' -GroupName $Name
    if ($api) {
        $apiArguments = @{}
        $apiDrift = [System.Collections.Generic.List[string]]::new()
        if (-not (Test-JIMAdfsSameSet $api.Identifier @($apiIdentifier))) {
            $apiArguments.Identifier = @($apiIdentifier)
            $apiDrift.Add("set the identifier to $apiIdentifier")
        }
        if ($api.AccessControlPolicyName -ne 'Permit everyone') {
            $apiArguments.AccessControlPolicyName = 'Permit everyone'
            $apiDrift.Add("set the access control policy to Permit everyone")
        }
        if (([string]$api.IssuanceTransformRules -replace '\s', '') -cne ($rules -replace '\s', '')) {
            $apiArguments.IssuanceTransformRules = $rules
            $apiDrift.Add('replace the issuance transform rules')
        }
        if ($apiArguments.Count -gt 0) {
            $apiArguments.TargetName = $api.Name
            $description = $apiDrift -join ', '
            $changes.Add((New-JIMAdfsChange -Action 'SetWebApiApplication' -Target $api.Name `
                -Description ($description.Substring(0, 1).ToUpperInvariant() + $description.Substring(1)) -Arguments $apiArguments))
        }
        $currentApiIdentifiers = @($api.Identifier)
    }
    else {
        $changes.Add((New-JIMAdfsChange -Action 'NewWebApiApplication' -Target $apiName `
            -Description "Add the API as a Web API, $apiIdentifier, open to everyone, issuing the person's objectGUID and profile" `
            -Arguments @{
                ApplicationGroupIdentifier = $Name; Name = $apiName; Identifier = @($apiIdentifier)
                AccessControlPolicyName = 'Permit everyone'; IssuanceTransformRules = $rules
            }))
        $currentApiIdentifiers = @()
    }

    # Who may ask for the API: JIM's two clients, each for its own scopes, and nobody else (#2097).
    $permissions = @($Current.Permissions | Where-Object { $null -ne $_ -and $_.ServerRoleIdentifier -in $currentApiIdentifiers })
    $wanted = @(
        @{ ClientId = $clientId; Scopes = $script:JIMAdfsPortalScopes; Label = 'the portal' },
        @{ ClientId = $publicClientId; Scopes = $script:JIMAdfsModuleScopes; Label = 'the PowerShell module' }
    )
    foreach ($permission in $permissions) {
        $keep = $permission.ServerRoleIdentifier -eq $apiIdentifier -and $permission.ClientRoleIdentifier -in $wanted.ClientId
        if (-not $keep) {
            $changes.Add((New-JIMAdfsChange -Action 'RevokePermission' -Target $permission.ServerRoleIdentifier `
                -Description "Revoke the permission of client $($permission.ClientRoleIdentifier)" `
                -Arguments @{ TargetIdentifier = [string]$permission.ObjectIdentifier }))
        }
    }
    foreach ($entry in $wanted) {
        $existing = $permissions | Where-Object {
            $_.ServerRoleIdentifier -eq $apiIdentifier -and $_.ClientRoleIdentifier -eq $entry.ClientId
        } | Select-Object -First 1
        if (-not $existing) {
            $changes.Add((New-JIMAdfsChange -Action 'GrantPermission' -Target $apiIdentifier `
                -Description "Permit $($entry.Label) the scopes $($entry.Scopes -join ', ')" `
                -Arguments @{ ClientRoleIdentifier = $entry.ClientId; ServerRoleIdentifier = $apiIdentifier; ScopeNames = $entry.Scopes }))
        }
        elseif (-not (Test-JIMAdfsSameSet $existing.ScopeNames $entry.Scopes)) {
            $changes.Add((New-JIMAdfsChange -Action 'SetPermission' -Target $apiIdentifier `
                -Description "Set the scopes of $($entry.Label) to $($entry.Scopes -join ', ')" `
                -Arguments @{ TargetIdentifier = [string]$existing.ObjectIdentifier; ScopeNames = $entry.Scopes }))
        }
    }

    [PSCustomObject]@{
        ClientId       = $clientId
        PublicClientId = $publicClientId
        ApiIdentifier  = $apiIdentifier
        Changes        = $changes.ToArray()
    }
}

function New-JIMAdfsSession {
    # A local Windows PowerShell session, where the ADFS module loads, for when the caller gave no session.
    [CmdletBinding()]
    param()

    if (-not $IsWindows) {
        throw 'AD FS can only be configured from Windows. Run this on the federation server, or pass -Session with a session to it.'
    }
    New-PSSession -UseWindowsPowerShell -ErrorAction Stop
}

function Get-JIMAdfsRegistrationState {
    <#
    .SYNOPSIS
        Reads what AD FS holds for JIM's Application Group, in the session given.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Session,
        [Parameter(Mandatory)][string]$Name
    )

    Invoke-Command -Session $Session -ArgumentList $Name -ErrorAction Stop -ScriptBlock {
        param($GroupName)
        $ErrorActionPreference = 'Stop'
        if (-not (Get-Module -ListAvailable -Name ADFS)) {
            throw "The ADFS PowerShell module is not available on $env:COMPUTERNAME. Run this on an AD FS server, or pass -Session with a session to one."
        }
        Import-Module ADFS

        $properties = Get-AdfsProperties
        $inGroup = { param($items) @($items | Where-Object { $_.ApplicationGroupIdentifier -eq $GroupName }) }

        [PSCustomObject]@{
            HostName           = [string]$properties.HostName
            Identifier         = [string]$properties.Identifier
            GroupExists        = $null -ne (Get-AdfsApplicationGroup | Where-Object { $_.ApplicationGroupIdentifier -eq $GroupName })
            ServerApplications = @(& $inGroup (Get-AdfsServerApplication) | ForEach-Object {
                [PSCustomObject]@{ Name = $_.Name; Identifier = [string]$_.Identifier; RedirectUri = @($_.RedirectUri) }
            })
            NativeApplications = @(& $inGroup (Get-AdfsNativeClientApplication) | ForEach-Object {
                [PSCustomObject]@{ Name = $_.Name; Identifier = [string]$_.Identifier; RedirectUri = @($_.RedirectUri) }
            })
            WebApiApplications = @(& $inGroup (Get-AdfsWebApiApplication) | ForEach-Object {
                [PSCustomObject]@{
                    Name = $_.Name; Identifier = @($_.Identifier); AccessControlPolicyName = [string]$_.AccessControlPolicyName
                    IssuanceTransformRules = [string]$_.IssuanceTransformRules
                }
            })
            Permissions        = @(Get-AdfsApplicationPermission | ForEach-Object {
                [PSCustomObject]@{
                    ObjectIdentifier = [string]$_.ObjectIdentifier; ClientRoleIdentifier = [string]$_.ClientRoleIdentifier
                    ServerRoleIdentifier = [string]$_.ServerRoleIdentifier; ScopeNames = @($_.ScopeNames)
                }
            })
        }
    }
}

function Invoke-JIMAdfsRegistrationChange {
    <#
    .SYNOPSIS
        Makes one planned change on AD FS, in the session given. Returns the client secret when the change made one.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Session,
        [Parameter(Mandatory)][object]$Change
    )

    $command = $script:JIMAdfsActionCommands[$Change.Action]
    if (-not $command) { throw "Unknown AD FS change '$($Change.Action)'." }

    Invoke-Command -Session $Session -ArgumentList $command, $Change.Arguments -ErrorAction Stop -ScriptBlock {
        param($Command, $Arguments)
        $ErrorActionPreference = 'Stop'
        Import-Module ADFS
        $result = & $Command @Arguments
        # Only a new or reset secret comes back; nothing else these commands return is needed.
        if ($Arguments.PassThru -and $result.ClientSecret) { [string]$result.ClientSecret }
    }
}
