# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Registering JIM with Keycloak (#2364), for Register-JIMIdentityProvider -Provider Keycloak.
#
# Keycloak is configured through its admin REST API with Invoke-RestMethod, so nothing beyond PowerShell is needed.
# As for AD FS, what the realm holds is read once, the plan is worked out here (a pure function, tested without
# Keycloak), and each planned change is then applied on its own, so -WhatIf and -Confirm see each change separately.

# The client scope that puts JIM's audience in an access token: JIM_SSO_API_SCOPE.
$script:JIMKeycloakApiScope = 'jim-api'
$script:JIMKeycloakAudienceMapper = 'jim-api-audience'

# Keycloak's own scopes JIM signs people in with, as default scopes of both clients. basic carries sub from Keycloak
# 25; a realm from an earlier version has no basic scope, and puts sub in every token anyway.
$script:JIMKeycloakDefaultScopes = @('basic', 'profile', 'email')

$script:JIMKeycloakScopeKindText = @{ Default = 'a default scope'; Optional = 'an optional scope' }

# What each SetClient property means, for -WhatIf and the Changes JIM returns.
$script:JIMKeycloakClientPropertyText = [ordered]@{
    enabled                   = { 'enable it' }
    publicClient              = { param($v) if ($v) { 'make it a public client' } else { 'make it a confidential client, with a client secret' } }
    standardFlowEnabled       = { 'turn on the standard flow' }
    implicitFlowEnabled       = { 'turn off the implicit flow' }
    directAccessGrantsEnabled = { 'turn off direct access grants' }
    serviceAccountsEnabled    = { 'turn off service accounts' }
    redirectUris              = { param($v) "set the redirect URIs to $($v -join ', ')" }
    PostLogoutRedirectUris    = { param($v) "set the post-logout redirect URIs to $($v -join ', ')" }
    Pkce                      = { 'require PKCE with S256' }
}

function ConvertTo-JIMKeycloakAuthority {
    <#
    .SYNOPSIS
        Splits a Keycloak authority, https://server/realms/realm, into the server's address and the realm.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Authority)

    $uri = $null
    if (-not [uri]::TryCreate($Authority, [UriKind]::Absolute, [ref]$uri) -or
        -not ($uri.Scheme -eq 'https' -or ($uri.Scheme -eq 'http' -and $uri.IsLoopback))) {
        throw "The Keycloak authority must be an absolute https address, such as https://sso.example.com/realms/jim, not '$Authority'."
    }
    $trimmed = $Authority.TrimEnd('/')
    $at = $trimmed.LastIndexOf('/realms/', [StringComparison]::Ordinal)
    $realm = if ($at -ge 0) { $trimmed.Substring($at + '/realms/'.Length) } else { '' }
    if (-not $realm -or $realm.Contains('/')) {
        throw "The Keycloak authority must name the realm JIM signs people in from, as https://sso.example.com/realms/jim does, not '$Authority'."
    }

    [PSCustomObject]@{
        Authority = $trimmed
        ServerUrl = $trimmed.Substring(0, $at)
        Realm     = $realm
    }
}

function Get-JIMKeycloakAdminToken {
    <#
    .SYNOPSIS
        Signs in to Keycloak to use its admin REST API: as an administrator through admin-cli, or as a client's
        service account.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ServerUrl,
        [Parameter(Mandatory)][string]$AdminRealm,
        [pscredential]$Credential,
        [pscredential]$ClientCredential
    )

    $body = if ($ClientCredential) {
        @{
            grant_type = 'client_credentials'; client_id = $ClientCredential.UserName
            client_secret = $ClientCredential.GetNetworkCredential().Password
        }
    }
    else {
        @{
            grant_type = 'password'; client_id = 'admin-cli'
            username = $Credential.UserName; password = $Credential.GetNetworkCredential().Password
        }
    }

    $uri = "$ServerUrl/realms/$([uri]::EscapeDataString($AdminRealm))/protocol/openid-connect/token"
    try {
        $response = Invoke-RestMethod -Method Post -Uri $uri -Body $body -ContentType 'application/x-www-form-urlencoded' -ErrorAction Stop
    }
    catch {
        $who = if ($ClientCredential) { "the client '$($ClientCredential.UserName)'" } else { "'$($Credential.UserName)'" }
        throw "Keycloak did not sign in $who in the realm '$AdminRealm': $($_.Exception.Message)"
    }

    [PSCustomObject]@{
        AccessToken = [string]$response.access_token
        ExpiresAt   = [datetime]::UtcNow.AddSeconds([int]$response.expires_in)
    }
}

function Invoke-JIMKeycloakAdminApi {
    <#
    .SYNOPSIS
        Calls the admin REST API for the connection's realm, signing in again when the token is about to expire.

    .PARAMETER Path
        The path under /admin/realms/{realm}, such as /clients.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Connection,
        [ValidateSet('Get', 'Post', 'Put', 'Delete')][string]$Method = 'Get',
        [Parameter(Mandatory)][string]$Path,
        [object]$Body
    )

    # admin-cli tokens in the master realm last a minute, which a run with -Confirm can outlast.
    if ($Connection.Token.ExpiresAt -lt [datetime]::UtcNow.AddSeconds(15)) {
        $Connection.Token = Get-JIMKeycloakAdminToken -ServerUrl $Connection.ServerUrl -AdminRealm $Connection.AdminRealm `
            -Credential $Connection.Credential -ClientCredential $Connection.ClientCredential
    }

    $request = @{
        Method      = $Method
        Uri         = "$($Connection.ServerUrl)/admin/realms/$([uri]::EscapeDataString($Connection.Realm))$Path"
        Headers     = @{ Authorization = "Bearer $($Connection.Token.AccessToken)" }
        ErrorAction = 'Stop'
    }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 10 -Compress
        $request.ContentType = 'application/json'
    }

    try {
        # Invoke-RestMethod returns a JSON array as one object; hand back its items, as callers filter them.
        $response = Invoke-RestMethod @request
        $response
    }
    catch {
        $status = [int]$_.Exception.Response.StatusCode
        if ($status -eq 403) {
            throw "Keycloak refused $Method $Path in the realm '$($Connection.Realm)'. The account needs the realm-admin role of that realm's realm-management client, or the master realm's admin role."
        }
        if ($status -eq 404 -and $Path -eq '/clients') {
            throw "Keycloak has no realm '$($Connection.Realm)'. Create it, or give the authority of the realm JIM signs people in from."
        }
        throw
    }
}

function Get-JIMKeycloakRegistrationState {
    <#
    .SYNOPSIS
        Reads what the realm holds that JIM's registration depends on: its clients, client scopes and default scopes.

    .PARAMETER SecretOf
        The client whose secret to read too, for comparing with one given. No other client's secret is read.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Connection,
        [string]$SecretOf
    )

    $clients = [System.Collections.Generic.List[object]]::new()
    # Keycloak returns every client when asked without first and max.
    foreach ($client in @(Invoke-JIMKeycloakAdminApi -Connection $Connection -Path '/clients')) {
        $defaults = $client.defaultClientScopes
        $optionals = $client.optionalClientScopes
        if ($null -eq $defaults -or $null -eq $optionals) {
            $defaults = @(Invoke-JIMKeycloakAdminApi -Connection $Connection -Path "/clients/$($client.id)/default-client-scopes").name
            $optionals = @(Invoke-JIMKeycloakAdminApi -Connection $Connection -Path "/clients/$($client.id)/optional-client-scopes").name
        }
        $attributes = $client.attributes
        $postLogout = [string]$attributes.'post.logout.redirect.uris'
        $clients.Add([PSCustomObject]@{
            ClientId                  = [string]$client.clientId
            PublicClient              = [bool]$client.publicClient
            Enabled                   = [bool]$client.enabled
            StandardFlowEnabled       = [bool]$client.standardFlowEnabled
            ImplicitFlowEnabled       = [bool]$client.implicitFlowEnabled
            DirectAccessGrantsEnabled = [bool]$client.directAccessGrantsEnabled
            ServiceAccountsEnabled    = [bool]$client.serviceAccountsEnabled
            RedirectUris              = @($client.redirectUris)
            PostLogoutRedirectUris    = @(if ($postLogout) { $postLogout -split '##' })
            Pkce                      = [string]$attributes.'pkce.code.challenge.method'
            DefaultClientScopes       = @($defaults)
            OptionalClientScopes      = @($optionals)
            Secret                    = if ($SecretOf -and $client.clientId -ceq $SecretOf) {
                [string](Invoke-JIMKeycloakAdminApi -Connection $Connection -Path "/clients/$($client.id)/client-secret").value
            }
        })
    }

    $scopes = @(Invoke-JIMKeycloakAdminApi -Connection $Connection -Path '/client-scopes' | ForEach-Object {
        [PSCustomObject]@{
            Name            = [string]$_.name
            Protocol        = [string]$_.protocol
            AudienceMappers = @($_.protocolMappers | Where-Object { $_.protocolMapper -eq 'oidc-audience-mapper' } | ForEach-Object {
                [PSCustomObject]@{
                    Name                   = [string]$_.name
                    IncludedClientAudience = [string]$_.config.'included.client.audience'
                    AccessTokenClaim       = [string]$_.config.'access.token.claim'
                    IdTokenClaim           = [string]$_.config.'id.token.claim'
                }
            })
        }
    })

    [PSCustomObject]@{
        Clients             = $clients.ToArray()
        ClientScopes        = $scopes
        RealmDefaultScopes  = @(@(Invoke-JIMKeycloakAdminApi -Connection $Connection -Path '/default-default-client-scopes').name)
        RealmOptionalScopes = @(@(Invoke-JIMKeycloakAdminApi -Connection $Connection -Path '/default-optional-client-scopes').name)
    }
}

function New-JIMKeycloakChange {
    param([string]$Action, [string]$Target, [string]$Description, [hashtable]$Arguments)

    [PSCustomObject]@{ Action = $Action; Target = $Target; Description = $Description; Arguments = $Arguments }
}

function Get-JIMKeycloakRegistrationPlan {
    <#
    .SYNOPSIS
        Works out what has to change in the realm for JIM's registration to be as it should be.

    .PARAMETER Url
        JIM's addresses, as people reach it.

    .PARAMETER ClientId
        The portal's client, JIM_SSO_CLIENT_ID.

    .PARAMETER PublicClientId
        The PowerShell module's client, JIM_SSO_PUBLIC_CLIENT_ID.

    .PARAMETER Current
        What the realm holds, as Get-JIMKeycloakRegistrationState reads it, with the portal's secret when -ClientSecret
        is given.

    .PARAMETER ResetClientSecret
        Plan a new client secret for the portal even if it has one.

    .PARAMETER ClientSecret
        The portal's client secret, in place of one Keycloak makes.

    .OUTPUTS
        The client identifiers and API scope, and the changes in the order they must be made.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Url,
        [Parameter(Mandatory)][string]$ClientId,
        [Parameter(Mandatory)][string]$PublicClientId,
        [Parameter(Mandatory)][object]$Current,
        [switch]$ResetClientSecret,
        [securestring]$ClientSecret
    )

    $baseUrls = @($Url | ForEach-Object { $_.TrimEnd('/') })
    $apiScope = $script:JIMKeycloakApiScope
    $changes = [System.Collections.Generic.List[object]]::new()
    $available = @($Current.ClientScopes.Name)

    # The API scope: its audience is the portal's client, which JIM requires in every access token's aud.
    $scope = $Current.ClientScopes | Where-Object Name -eq $apiScope | Select-Object -First 1
    if (-not $scope) {
        $changes.Add((New-JIMKeycloakChange -Action 'NewClientScope' -Target $apiScope `
            -Description "Create the client scope, putting $ClientId in the access token's audience" `
            -Arguments @{ Name = $apiScope; AudienceClientId = $ClientId }))
    }
    else {
        if ($scope.Protocol -ne 'openid-connect') {
            throw "The realm has a client scope called '$apiScope' for $($scope.Protocol), not OpenID Connect. Rename or remove it, and run this again."
        }
        $mappers = @($scope.AudienceMappers)
        $mapper = @($mappers | Where-Object Name -eq $script:JIMKeycloakAudienceMapper) + $mappers | Select-Object -First 1
        if (-not $mapper) {
            $changes.Add((New-JIMKeycloakChange -Action 'NewAudienceMapper' -Target $apiScope `
                -Description "Put $ClientId in the access token's audience" `
                -Arguments @{ Scope = $apiScope; AudienceClientId = $ClientId }))
        }
        elseif ($mapper.IncludedClientAudience -cne $ClientId -or $mapper.AccessTokenClaim -ne 'true') {
            $changes.Add((New-JIMKeycloakChange -Action 'SetAudienceMapper' -Target $apiScope `
                -Description "Put $ClientId in the access token's audience, in place of '$($mapper.IncludedClientAudience)'" `
                -Arguments @{ Scope = $apiScope; MapperName = $mapper.Name; AudienceClientId = $ClientId }))
        }
    }

    # A realm default scope is added to every new client, which would let any of them ask for JIM's audience (#2097).
    foreach ($kind in 'Default', 'Optional') {
        $realmScopes = if ($kind -eq 'Default') { $Current.RealmDefaultScopes } else { $Current.RealmOptionalScopes }
        if ($apiScope -in @($realmScopes)) {
            $changes.Add((New-JIMKeycloakChange -Action 'RemoveRealmDefaultScope' -Target $apiScope `
                -Description "Stop adding it to every new client as a realm $($kind.ToLowerInvariant()) scope" `
                -Arguments @{ Scope = $apiScope; Kind = $kind }))
        }
    }

    $wanted = @(
        @{
            ClientId = $ClientId; Label = 'the portal'; Name = 'JIM'; Optional = @($apiScope)
            Properties = [ordered]@{
                enabled = $true; publicClient = $false; standardFlowEnabled = $true; implicitFlowEnabled = $false
                directAccessGrantsEnabled = $false; serviceAccountsEnabled = $false
                redirectUris = @($baseUrls | ForEach-Object { "$_/signin-oidc" })
                PostLogoutRedirectUris = @($baseUrls | ForEach-Object { "$_/signout-callback-oidc" }); Pkce = 'S256'
            }
        },
        @{
            # offline_access gives the module a refresh token, to renew its sign-in without the browser.
            ClientId = $PublicClientId; Label = 'the PowerShell module'; Name = 'JIM PowerShell module'
            Optional = @($apiScope, 'offline_access')
            Properties = [ordered]@{
                enabled = $true; publicClient = $true; standardFlowEnabled = $true; implicitFlowEnabled = $false
                directAccessGrantsEnabled = $false; serviceAccountsEnabled = $false
                redirectUris = @(Get-JIMLoopbackRedirectUri); Pkce = 'S256'
            }
        }
    )

    foreach ($entry in $wanted) {
        $client = $Current.Clients | Where-Object ClientId -ceq $entry.ClientId | Select-Object -First 1
        $properties = $entry.Properties
        if (-not $client) {
            $description = if ($properties.publicClient) { "Add $($entry.Label) as a public client" }
                elseif ($ClientSecret) { "Add $($entry.Label) as a confidential client, with the client secret given" }
                else { "Add $($entry.Label) as a confidential client, with a new client secret" }
            $arguments = @{ ClientId = $entry.ClientId; Name = $entry.Name; Properties = $properties }
            if ($ClientSecret -and -not $properties.publicClient) { $arguments.Secret = $ClientSecret }
            $changes.Add((New-JIMKeycloakChange -Action 'NewClient' -Target $entry.ClientId -Description $description -Arguments $arguments))
            $currentDefaults = @()
            $currentOptionals = @()
        }
        else {
            $drift = [ordered]@{}
            foreach ($key in $properties.Keys) {
                $have = $client.$key
                $want = $properties[$key]
                $same = if ($want -is [array]) { Test-JIMSameSet $have $want } else { "$have" -eq "$want" }
                if (-not $same) { $drift[$key] = $want }
            }
            if ($drift.Count -gt 0) {
                $description = ($drift.Keys | ForEach-Object { & $script:JIMKeycloakClientPropertyText[$_] $drift[$_] }) -join ', '
                $changes.Add((New-JIMKeycloakChange -Action 'SetClient' -Target $entry.ClientId `
                    -Description ($description.Substring(0, 1).ToUpperInvariant() + $description.Substring(1)) `
                    -Arguments @{ ClientId = $entry.ClientId; Properties = [hashtable]$drift }))
            }
            if ($ResetClientSecret -and -not $properties.publicClient) {
                $changes.Add((New-JIMKeycloakChange -Action 'ResetClientSecret' -Target $entry.ClientId -Description 'Replace the client secret' `
                    -Arguments @{ ClientId = $entry.ClientId }))
            }
            if ($ClientSecret -and -not $properties.publicClient -and
                $client.Secret -cne [System.Net.NetworkCredential]::new('', $ClientSecret).Password) {
                $changes.Add((New-JIMKeycloakChange -Action 'SetClientSecret' -Target $entry.ClientId -Description 'Set the client secret to the one given' `
                    -Arguments @{ ClientId = $entry.ClientId; Secret = $ClientSecret }))
            }
            $currentDefaults = @($client.DefaultClientScopes)
            $currentOptionals = @($client.OptionalClientScopes)
        }

        $links = @(
            @($script:JIMKeycloakDefaultScopes | Where-Object { $_ -in $available } | ForEach-Object { @{ Scope = $_; Kind = 'Default' } })
            @($entry.Optional | Where-Object { $_ -eq $apiScope -or $_ -in $available } | ForEach-Object { @{ Scope = $_; Kind = 'Optional' } })
        )
        foreach ($link in $links) {
            $have = if ($link.Kind -eq 'Default') { $currentDefaults } else { $currentOptionals }
            $other = if ($link.Kind -eq 'Default') { $currentOptionals } else { $currentDefaults }
            if ($link.Scope -in $have) { continue }
            if ($link.Scope -in $other) {
                $otherKind = if ($link.Kind -eq 'Default') { 'Optional' } else { 'Default' }
                $changes.Add((New-JIMKeycloakChange -Action 'RemoveClientScope' -Target $entry.ClientId `
                    -Description "Remove $($link.Scope) as $($script:JIMKeycloakScopeKindText[$otherKind])" `
                    -Arguments @{ ClientId = $entry.ClientId; Scope = $link.Scope; Kind = $otherKind }))
            }
            $changes.Add((New-JIMKeycloakChange -Action 'AddClientScope' -Target $entry.ClientId `
                -Description "Add $($link.Scope) as $($script:JIMKeycloakScopeKindText[$link.Kind])" `
                -Arguments @{ ClientId = $entry.ClientId; Scope = $link.Scope; Kind = $link.Kind }))
        }
    }

    # Who may ask for JIM's audience: JIM's two clients, and nobody else (#2097).
    foreach ($client in @($Current.Clients | Where-Object { $_.ClientId -cnotin $ClientId, $PublicClientId })) {
        foreach ($kind in 'Default', 'Optional') {
            $linked = if ($kind -eq 'Default') { $client.DefaultClientScopes } else { $client.OptionalClientScopes }
            if ($apiScope -in @($linked)) {
                $changes.Add((New-JIMKeycloakChange -Action 'RemoveClientScope' -Target $client.ClientId `
                    -Description "Remove $apiScope, which only JIM's clients may ask for" `
                    -Arguments @{ ClientId = $client.ClientId; Scope = $apiScope; Kind = $kind }))
            }
        }
    }

    [PSCustomObject]@{
        ClientId       = $ClientId
        PublicClientId = $PublicClientId
        ApiScope       = $apiScope
        Changes        = $changes.ToArray()
    }
}

function Get-JIMKeycloakClientUuid {
    # Keycloak addresses a client by its internal id, not its client ID.
    param([hashtable]$Connection, [string]$ClientId)

    $client = @(Invoke-JIMKeycloakAdminApi -Connection $Connection -Path "/clients?clientId=$([uri]::EscapeDataString($ClientId))") |
        Where-Object clientId -ceq $ClientId | Select-Object -First 1
    if (-not $client) { throw "Keycloak has no client '$ClientId'." }
    [string]$client.id
}

function Get-JIMKeycloakClientScope {
    param([hashtable]$Connection, [string]$Name)

    $scope = @(Invoke-JIMKeycloakAdminApi -Connection $Connection -Path '/client-scopes') | Where-Object name -eq $Name | Select-Object -First 1
    if (-not $scope) { throw "Keycloak has no client scope '$Name'." }
    $scope
}

function New-JIMKeycloakAudienceMapper {
    param([string]$AudienceClientId)

    @{
        name = $script:JIMKeycloakAudienceMapper; protocol = 'openid-connect'; protocolMapper = 'oidc-audience-mapper'
        config = @{
            'included.client.audience' = $AudienceClientId; 'access.token.claim' = 'true'; 'id.token.claim' = 'false'
            'introspection.token.claim' = 'true'
        }
    }
}

function Set-JIMKeycloakClientProperty {
    # Applies planned properties to a client representation: most are its own fields, two are attributes.
    param([object]$Representation, [System.Collections.IDictionary]$Properties)

    if (-not $Representation.attributes) {
        $Representation | Add-Member -NotePropertyName attributes -NotePropertyValue ([PSCustomObject]@{}) -Force
    }
    foreach ($key in $Properties.Keys) {
        $value = $Properties[$key]
        switch ($key) {
            'PostLogoutRedirectUris' { $Representation.attributes | Add-Member -NotePropertyName 'post.logout.redirect.uris' -NotePropertyValue ($value -join '##') -Force }
            'Pkce' { $Representation.attributes | Add-Member -NotePropertyName 'pkce.code.challenge.method' -NotePropertyValue $value -Force }
            default { $Representation | Add-Member -NotePropertyName $key -NotePropertyValue $value -Force }
        }
    }
}

function Invoke-JIMKeycloakRegistrationChange {
    <#
    .SYNOPSIS
        Makes one planned change in the realm. Returns the client secret when the change made one.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][hashtable]$Connection,
        [Parameter(Mandatory)][object]$Change
    )

    $arguments = $Change.Arguments
    switch ($Change.Action) {
        'NewClientScope' {
            # Created through the API, a scope is no realm default: the Type None the guide sets in the console.
            $null = Invoke-JIMKeycloakAdminApi -Connection $Connection -Method Post -Path '/client-scopes' -Body @{
                name = $arguments.Name; protocol = 'openid-connect'; description = "JIM's API"
                attributes = @{ 'include.in.token.scope' = 'true'; 'display.on.consent.screen' = 'false' }
                protocolMappers = @(New-JIMKeycloakAudienceMapper -AudienceClientId $arguments.AudienceClientId)
            }
        }
        'NewAudienceMapper' {
            $scope = Get-JIMKeycloakClientScope -Connection $Connection -Name $arguments.Scope
            $null = Invoke-JIMKeycloakAdminApi -Connection $Connection -Method Post -Path "/client-scopes/$($scope.id)/protocol-mappers/models" `
                -Body (New-JIMKeycloakAudienceMapper -AudienceClientId $arguments.AudienceClientId)
        }
        'SetAudienceMapper' {
            $scope = Get-JIMKeycloakClientScope -Connection $Connection -Name $arguments.Scope
            $mapper = $scope.protocolMappers | Where-Object name -eq $arguments.MapperName | Select-Object -First 1
            $mapper.config | Add-Member -NotePropertyName 'included.client.audience' -NotePropertyValue $arguments.AudienceClientId -Force
            $mapper.config | Add-Member -NotePropertyName 'access.token.claim' -NotePropertyValue 'true' -Force
            $null = Invoke-JIMKeycloakAdminApi -Connection $Connection -Method Put -Path "/client-scopes/$($scope.id)/protocol-mappers/models/$($mapper.id)" -Body $mapper
        }
        'RemoveRealmDefaultScope' {
            $scope = Get-JIMKeycloakClientScope -Connection $Connection -Name $arguments.Scope
            $list = if ($arguments.Kind -eq 'Default') { 'default-default-client-scopes' } else { 'default-optional-client-scopes' }
            $null = Invoke-JIMKeycloakAdminApi -Connection $Connection -Method Delete -Path "/$list/$($scope.id)"
        }
        'NewClient' {
            $representation = [PSCustomObject]@{
                clientId = $arguments.ClientId; name = $arguments.Name; protocol = 'openid-connect'
                clientAuthenticatorType = 'client-secret'; attributes = [PSCustomObject]@{}
            }
            Set-JIMKeycloakClientProperty -Representation $representation -Properties $arguments.Properties
            if ($arguments.Secret) {
                $representation | Add-Member -NotePropertyName secret -NotePropertyValue ([System.Net.NetworkCredential]::new('', $arguments.Secret).Password)
            }
            $null = Invoke-JIMKeycloakAdminApi -Connection $Connection -Method Post -Path '/clients' -Body $representation
            # Keycloak makes a confidential client's secret itself, unless it is given one.
            if (-not $arguments.Properties.publicClient) {
                $uuid = Get-JIMKeycloakClientUuid -Connection $Connection -ClientId $arguments.ClientId
                [string](Invoke-JIMKeycloakAdminApi -Connection $Connection -Path "/clients/$uuid/client-secret").value
            }
        }
        'SetClient' {
            # Keycloak replaces a client with what it is sent, so send everything it holds with the changes made.
            $uuid = Get-JIMKeycloakClientUuid -Connection $Connection -ClientId $arguments.ClientId
            $representation = Invoke-JIMKeycloakAdminApi -Connection $Connection -Path "/clients/$uuid"
            Set-JIMKeycloakClientProperty -Representation $representation -Properties $arguments.Properties
            $null = Invoke-JIMKeycloakAdminApi -Connection $Connection -Method Put -Path "/clients/$uuid" -Body $representation
        }
        'ResetClientSecret' {
            $uuid = Get-JIMKeycloakClientUuid -Connection $Connection -ClientId $arguments.ClientId
            [string](Invoke-JIMKeycloakAdminApi -Connection $Connection -Method Post -Path "/clients/$uuid/client-secret").value
        }
        'SetClientSecret' {
            $uuid = Get-JIMKeycloakClientUuid -Connection $Connection -ClientId $arguments.ClientId
            $representation = Invoke-JIMKeycloakAdminApi -Connection $Connection -Path "/clients/$uuid"
            $secret = [System.Net.NetworkCredential]::new('', $arguments.Secret).Password
            $representation | Add-Member -NotePropertyName secret -NotePropertyValue $secret -Force
            $null = Invoke-JIMKeycloakAdminApi -Connection $Connection -Method Put -Path "/clients/$uuid" -Body $representation
            $secret
        }
        { $_ -in 'AddClientScope', 'RemoveClientScope' } {
            $uuid = Get-JIMKeycloakClientUuid -Connection $Connection -ClientId $arguments.ClientId
            $scope = Get-JIMKeycloakClientScope -Connection $Connection -Name $arguments.Scope
            $list = if ($arguments.Kind -eq 'Default') { 'default-client-scopes' } else { 'optional-client-scopes' }
            $method = if ($Change.Action -eq 'AddClientScope') { 'Put' } else { 'Delete' }
            $null = Invoke-JIMKeycloakAdminApi -Connection $Connection -Method $method -Path "/clients/$uuid/$list/$($scope.id)"
        }
        default { throw "Unknown Keycloak change '$($Change.Action)'." }
    }
}
