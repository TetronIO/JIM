# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

<#
.SYNOPSIS
    How the Microsoft Entra ID Connector scenarios (036 and 037, #361) reach the JIM test tenant: the harness's own
    Microsoft Graph access, its run prefix, its waits, the snapshot of accounts it must never change, and its cleanup.

.DESCRIPTION
    Dot-source this file. The plan is engineering/plans/ENTRA_ID_CONNECTOR_INTEGRATION_SCENARIOS.md.

    The Connector signs in as a permanent app registration in the tenant, "JIM Entra ID Connector (CI)", which an
    administrator created once with Register-JIMEntraIdConnector, and which the CI app owns. No credential for it is
    stored anywhere. Each run, as the CI app (Get-EntraIdLabGraphToken, Application.ReadWrite.OwnedBy only):

      1. Register-JIMEntraIdConnector -ResetCertificate gives the app a new certificate, hands its private key to JIM,
         and removes the previous one;
      2. the harness adds a short-lived certificate of its own to the same app (Add-EntraIdConnectorLabCredential),
         signs in with it (Get-EntraIdConnectorLabToken), and seeds and checks data through its own Graph requests,
         never through JIM, so a mistake mirrored in JIM's import and export cannot cancel itself out;
      3. at the end the harness removes its certificate. One a failed run strands expires within two days, and the
         next run removes it.

    Everything a run creates is named with its prefix, jimit-<run id>- (Get-EntraIdConnectorLabRunPrefix), so runs
    never collide and cleanup never reaches anything else. Everything else in the tenant (its administrators and
    Scenario 035's test people) is snapshotted before the run and compared after it.
#>

# The display name of the harness's own certificates on the Connector app. Register-JIMEntraIdConnector manages only
# certificates named after the app, so it never removes the harness's, and the harness never removes JIM's.
$script:EntraIdConnectorLabCredentialName = 'JIM integration harness'

# Every object any run creates starts with this, and nothing else in the tenant may.
$script:EntraIdConnectorLabPrefixRoot = 'jimit-'

$script:EntraIdConnectorLabGraph = 'https://graph.microsoft.com/v1.0'

# What the snapshot records of each user and group the harness did not create.
$script:EntraIdConnectorLabUserProperties = @(
    'id', 'displayName', 'userPrincipalName', 'accountEnabled', 'givenName', 'surname', 'jobTitle', 'department',
    'companyName', 'employeeId', 'officeLocation', 'mail', 'mailNickname', 'mobilePhone', 'businessPhones',
    'usageLocation', 'userType')
$script:EntraIdConnectorLabGroupProperties = @(
    'id', 'displayName', 'description', 'mailNickname', 'mailEnabled', 'securityEnabled', 'visibility', 'groupTypes')

function Get-EntraIdConnectorLabValue {
    <#
    .SYNOPSIS
        A property of an object Graph returned, or $null when Graph left it out. Graph omits what does not apply (the
        last page's @odata.nextLink, a group's userPrincipalName, an error body on a gateway failure), and the scenarios
        run under strict mode, where reading a missing property throws.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    if ($Object -is [System.Collections.IDictionary] -and $Object.Contains($Name)) { return $Object[$Name] }
    return $null
}

function Get-EntraIdConnectorLabRunPrefix {
    <#
    .SYNOPSIS
        The prefix of everything this run creates: jimit-<run id>-. The run id is GitHub's run id and attempt in
        Actions, otherwise six random hexadecimal characters.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [string]$RunId
    )

    if (-not $RunId) {
        $RunId = if ($env:GITHUB_RUN_ID) { "$($env:GITHUB_RUN_ID)$($env:GITHUB_RUN_ATTEMPT)" }
        else { [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(3)) }
    }
    $RunId = $RunId.ToLowerInvariant()
    if ($RunId -notmatch '^[a-z0-9]{1,24}$') { throw "A run id is letters and digits only, at most 24: '$RunId'." }
    return "$script:EntraIdConnectorLabPrefixRoot$RunId-"
}

function Assert-EntraIdConnectorLabRunPrefix {
    <#
    .SYNOPSIS
        Refuses anything but a whole run prefix, so a deletion can never be widened to the tenant.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Prefix
    )

    if ($Prefix -cnotmatch "^$([regex]::Escape($script:EntraIdConnectorLabPrefixRoot))[a-z0-9]{1,24}-$") {
        throw "'$Prefix' is not a run prefix (jimit-<run id>-); the harness deletes nothing else."
    }
}

function New-EntraIdConnectorLabCertificate {
    <#
    .SYNOPSIS
        A self-signed certificate with its private key, made in memory for the harness to sign in with. It is valid for
        two days by default, so one a failed run leaves on the app soon stops working.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Makes an object in memory only.')]
    [CmdletBinding()]
    [OutputType([System.Security.Cryptography.X509Certificates.X509Certificate2])]
    param(
        [ValidateRange(1, 168)][int]$ValidityHours = 48
    )

    # Whole seconds: Microsoft Graph refuses a key credential whose end is later than the certificate's, and a
    # certificate's dates hold no fractions of a second.
    $now = [DateTimeOffset]::FromUnixTimeSeconds([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=$script:EntraIdConnectorLabCredentialName", $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        return $request.CreateSelfSigned($now.AddMinutes(-5), $now.AddHours($ValidityHours))
    }
    finally {
        $rsa.Dispose()
    }
}

function Invoke-EntraIdConnectorLabHttp {
    <#
    .SYNOPSIS
        One HTTP request, returning its status, headers and parsed body whatever the status, for the callers to decide.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [hashtable]$Headers = @{},
        [object]$Body,
        [string]$ContentType
    )

    $request = @{ Method = $Method; Uri = $Uri; Headers = $Headers; SkipHttpErrorCheck = $true; ErrorAction = 'Stop' }
    if ($PSBoundParameters.ContainsKey('Body')) { $request.Body = $Body }
    if ($ContentType) { $request.ContentType = $ContentType }
    $response = Invoke-WebRequest @request
    $content = [string]$response.Content
    $parsed = if ($content) { try { $content | ConvertFrom-Json } catch [System.ArgumentException] { $content } } else { $null }
    return [PSCustomObject]@{ StatusCode = [int]$response.StatusCode; Headers = $response.Headers; Body = $parsed; Content = $content }
}

function New-EntraIdConnectorLabClientAssertion {
    <#
    .SYNOPSIS
        A client assertion: a JSON Web Token signed with the certificate's private key, which proves to Entra ID that
        the caller holds a certificate on the application. Valid for ten minutes.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$ClientId,
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $encode = { param([byte[]]$Bytes) [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
    $thumbprint = [System.Security.Cryptography.SHA256]::HashData($Certificate.RawData)
    $header = @{ alg = 'RS256'; typ = 'JWT'; 'x5t#S256' = (& $encode $thumbprint) } | ConvertTo-Json -Compress
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $payload = @{
        aud = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"; iss = $ClientId; sub = $ClientId
        jti = [Guid]::NewGuid().ToString(); nbf = $now; iat = $now; exp = $now + 600
    } | ConvertTo-Json -Compress
    $unsigned = "$(& $encode ([System.Text.Encoding]::UTF8.GetBytes($header))).$(& $encode ([System.Text.Encoding]::UTF8.GetBytes($payload)))"
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    try {
        $signature = $rsa.SignData([System.Text.Encoding]::ASCII.GetBytes($unsigned), [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    }
    finally {
        $rsa.Dispose()
    }
    return "$unsigned.$(& $encode $signature)"
}

function Get-EntraIdConnectorLabToken {
    <#
    .SYNOPSIS
        A Microsoft Graph token for the Connector app, signed in with the harness's certificate, as a secure string.
        Entra ID takes a minute or two to honour a certificate added moments ago, so a refusal that says only that is
        tried again until the timeout.
    #>
    [CmdletBinding()]
    [OutputType([securestring])]
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$ClientId,
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [int]$TimeoutSeconds = 300,
        [int]$IntervalSeconds = 15
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $response = Invoke-EntraIdConnectorLabHttp -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
            -ContentType 'application/x-www-form-urlencoded' -Body @{
                client_id = $ClientId; grant_type = 'client_credentials'; scope = 'https://graph.microsoft.com/.default'
                client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
                client_assertion = New-EntraIdConnectorLabClientAssertion -TenantId $TenantId -ClientId $ClientId -Certificate $Certificate
            }
        $accessToken = Get-EntraIdConnectorLabValue -Object $response.Body -Name 'access_token'
        if ($response.StatusCode -eq 200 -and $accessToken) {
            return ConvertTo-SecureString -String ([string]$accessToken) -AsPlainText -Force
        }
        $description = Get-EntraIdConnectorLabValue -Object $response.Body -Name 'error_description'
        $reason = if ($description) { [string]$description } else { "status $($response.StatusCode)" }
        # AADSTS700027: the certificate is not yet known; AADSTS7000215 and AADSTS700016: nor is the app, on a new one.
        $propagating = $reason -match 'AADSTS700027|AADSTS7000215|AADSTS700016'
        if (-not $propagating -or [DateTime]::UtcNow -ge $deadline) {
            throw "Entra ID refused the harness's sign-in as the Connector app: $($reason.Split([char]10)[0])"
        }
        Start-Sleep -Seconds $IntervalSeconds
    }
}

function Invoke-EntraIdConnectorLabGraph {
    <#
    .SYNOPSIS
        Calls Microsoft Graph with a token, waiting out throttling (429) and a busy service (503, 504) as Graph's
        Retry-After asks, and throwing on any other failure with Graph's own error.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][securestring]$Token,
        [ValidateSet('Get', 'Post', 'Patch', 'Delete')][string]$Method = 'Get',
        [Parameter(Mandatory)][string]$Path,
        [object]$Body,
        [hashtable]$Headers = @{},
        [int]$MaximumAttempts = 6
    )

    $uri = if ($Path -like 'https://*') { $Path } else { "$script:EntraIdConnectorLabGraph$Path" }
    $request = @{ Method = $Method; Uri = $uri; Headers = @{ Authorization = "Bearer $([System.Net.NetworkCredential]::new('', $Token).Password)" } + $Headers }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 10 -Compress
        $request.ContentType = 'application/json'
    }
    for ($attempt = 1; ; $attempt++) {
        $response = Invoke-EntraIdConnectorLabHttp @request
        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) { return $response.Body }
        if ($response.StatusCode -notin 429, 503, 504 -or $attempt -ge $MaximumAttempts) {
            $errorBody = Get-EntraIdConnectorLabValue -Object (Get-EntraIdConnectorLabValue -Object $response -Name 'Body') -Name 'error'
            $graphError = if ($errorBody) { "$(Get-EntraIdConnectorLabValue $errorBody 'code'): $(Get-EntraIdConnectorLabValue $errorBody 'message')" }
            else { Get-EntraIdConnectorLabValue -Object $response -Name 'Content' }
            throw "Microsoft Graph refused $($Method.ToUpperInvariant()) $Path ($($response.StatusCode)): $graphError"
        }
        $retryAfter = 0
        $headers = Get-EntraIdConnectorLabValue -Object $response -Name 'Headers'
        $value = if ($headers) { @($headers['Retry-After'])[0] } else { $null }
        if (-not [int]::TryParse([string]$value, [ref]$retryAfter)) { $retryAfter = [Math]::Min(60, [Math]::Pow(2, $attempt)) }
        Start-Sleep -Seconds ([Math]::Max(1, $retryAfter))
    }
}

function Invoke-EntraIdConnectorLabGraphBatch {
    <#
    .SYNOPSIS
        Sends Graph requests twenty at a time through $batch, Graph's limit, and returns one answer per request, in the
        order given: each with its Status and Body. A request Graph throttled or found busy is sent again, in a later
        batch, after the longest Retry-After it asked for; any other answer, refusals included, is returned for the
        caller to judge.

    .DESCRIPTION
        A scale run creates and deletes tens of thousands of objects; one request each would take hours.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][securestring]$Token,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Requests,
        [int]$MaximumAttempts = 6
    )

    $answers = [object[]]::new($Requests.Count)
    $pending = [System.Collections.Generic.List[int]]::new()
    for ($index = 0; $index -lt $Requests.Count; $index++) { $pending.Add($index) }
    for ($attempt = 1; $pending.Count -gt 0; $attempt++) {
        $again = [System.Collections.Generic.List[int]]::new()
        $wait = 0
        for ($start = 0; $start -lt $pending.Count; $start += 20) {
            $chunk = $pending.GetRange($start, [Math]::Min(20, $pending.Count - $start))
            $batch = @{ requests = @($chunk | ForEach-Object {
                $request = $Requests[$_]
                $sent = @{ id = "$_"; method = $request.method; url = $request.url }
                $body = Get-EntraIdConnectorLabValue -Object $request -Name 'body'
                if ($null -ne $body) { $sent.body = $body; $sent.headers = @{ 'Content-Type' = 'application/json' } }
                $sent
            }) }
            $answer = Invoke-EntraIdConnectorLabGraph -Token $Token -Method Post -Path '/$batch' -Body $batch
            foreach ($response in @($answer.responses)) {
                $index = [int]$response.id
                $status = [int]$response.status
                if ($status -in 429, 503, 504 -and $attempt -lt $MaximumAttempts) {
                    $again.Add($index)
                    $retryAfter = 0
                    $value = Get-EntraIdConnectorLabValue -Object (Get-EntraIdConnectorLabValue -Object $response -Name 'headers') -Name 'Retry-After'
                    if (-not [int]::TryParse([string]$value, [ref]$retryAfter)) { $retryAfter = [Math]::Min(60, [Math]::Pow(2, $attempt)) }
                    $wait = [Math]::Max($wait, $retryAfter)
                    continue
                }
                $answers[$index] = [PSCustomObject]@{ Status = $status; Body = (Get-EntraIdConnectorLabValue -Object $response -Name 'body') }
            }
        }
        if ($again.Count -gt 0) { Start-Sleep -Seconds ([Math]::Max(1, $wait)) }
        $pending = $again
    }
    return , $answers
}

# Graph's error in a $batch answer, for a refusal the caller reports.
function Get-EntraIdConnectorLabBatchError {
    param([object]$Answer)
    $graphError = Get-EntraIdConnectorLabValue -Object $Answer.Body -Name 'error'
    if ($graphError) { return "$(Get-EntraIdConnectorLabValue $graphError 'code'): $(Get-EntraIdConnectorLabValue $graphError 'message')" }
    return ''
}

function Get-EntraIdConnectorLabGraphCollection {
    <#
    .SYNOPSIS
        Every item of a Graph collection, following its next links.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][securestring]$Token,
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Headers = @{}
    )

    $items = [System.Collections.Generic.List[object]]::new()
    $next = $Path
    while ($next) {
        $page = Invoke-EntraIdConnectorLabGraph -Token $Token -Path $next -Headers $Headers
        foreach ($item in @(Get-EntraIdConnectorLabValue -Object $page -Name 'value')) { if ($null -ne $item) { $items.Add($item) } }
        $next = Get-EntraIdConnectorLabValue -Object $page -Name '@odata.nextLink'
    }
    # One item at a time, so an empty collection is nothing at all; callers wrap the call in @() for an array.
    return $items.ToArray()
}

function Add-EntraIdConnectorLabCredential {
    <#
    .SYNOPSIS
        Adds the harness's certificate to the Connector app, as the CI app that owns it, and returns its key id. Harness
        certificates that have expired, left by failed runs, are removed at the same time. Every other certificate goes
        back exactly as Graph returned it, which Graph requires.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test harness; changes only the harness''s own credentials.')]
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][securestring]$AccessToken,
        [Parameter(Mandatory)][string]$ApplicationObjectId,
        [Parameter(Mandatory)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $existing = @((Invoke-EntraIdConnectorLabGraph -Token $AccessToken -Path "/applications/$ApplicationObjectId`?`$select=keyCredentials").keyCredentials)
    $now = [DateTime]::UtcNow
    $keep = @($existing | Where-Object { -not ($_.displayName -eq $script:EntraIdConnectorLabCredentialName -and ([DateTime]$_.endDateTime).ToUniversalTime() -lt $now) })
    $keyId = [Guid]::NewGuid().ToString()
    $new = @{
        keyId = $keyId; type = 'AsymmetricX509Cert'; usage = 'Verify'; displayName = $script:EntraIdConnectorLabCredentialName
        key = [Convert]::ToBase64String($Certificate.RawData)
        startDateTime = $Certificate.NotBefore.ToUniversalTime().ToString('o'); endDateTime = $Certificate.NotAfter.ToUniversalTime().ToString('o')
    }
    $null = Invoke-EntraIdConnectorLabGraph -Token $AccessToken -Method Patch -Path "/applications/$ApplicationObjectId" -Body @{ keyCredentials = @($keep) + @($new) }
    return $keyId
}

function Remove-EntraIdConnectorLabCredential {
    <#
    .SYNOPSIS
        Removes the harness's certificate from the Connector app, leaving every other one exactly as it was.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test harness; changes only the harness''s own credentials.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][securestring]$AccessToken,
        [Parameter(Mandatory)][string]$ApplicationObjectId,
        [Parameter(Mandatory)][string]$KeyId
    )

    $existing = @((Invoke-EntraIdConnectorLabGraph -Token $AccessToken -Path "/applications/$ApplicationObjectId`?`$select=keyCredentials").keyCredentials)
    if (-not ($existing | Where-Object { $_.keyId -eq $KeyId })) { return }
    $keep = @($existing | Where-Object { $_.keyId -ne $KeyId })
    $null = Invoke-EntraIdConnectorLabGraph -Token $AccessToken -Method Patch -Path "/applications/$ApplicationObjectId" -Body @{ keyCredentials = @($keep) }
}

function Wait-EntraIdConnectorLabCondition {
    <#
    .SYNOPSIS
        Polls until the condition returns something truthy, and returns it; Graph is eventually consistent, so a read
        straight after a write can miss it. Throws, naming what it waited for, when the timeout passes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Condition,
        [int]$TimeoutSeconds = 120,
        [int]$IntervalSeconds = 5
    )

    $started = [DateTime]::UtcNow
    while ($true) {
        $result = & $Condition
        if ($result) {
            $waited = ([DateTime]::UtcNow - $started).TotalSeconds
            if ($waited -ge 1) { Write-Verbose ("Waited {0:N0}s for {1}" -f $waited, $Description) }
            return $result
        }
        if (([DateTime]::UtcNow - $started).TotalSeconds -ge $TimeoutSeconds) {
            throw "Timed out after ${TimeoutSeconds}s waiting for $Description."
        }
        Start-Sleep -Seconds $IntervalSeconds
    }
}

function Get-EntraIdConnectorLabSnapshot {
    <#
    .SYNOPSIS
        Records every user and group in the tenant that no run created (the tenant's administrators, Scenario 035's
        test people and anything else), with their properties and each group's members, so Compare-EntraIdConnectorLabSnapshot
        can prove a run changed none of them. Keyed by object id; each value is a canonical JSON string.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)][securestring]$Token
    )

    $snapshot = @{}
    $users = Get-EntraIdConnectorLabGraphCollection -Token $Token -Path "/users?`$select=$($script:EntraIdConnectorLabUserProperties -join ',')&`$top=999"
    foreach ($user in @($users | Where-Object { -not (Test-EntraIdConnectorLabRunObject -Object $_) })) {
        $snapshot["user:$($user.id)"] = ConvertTo-EntraIdConnectorLabCanonicalJson -Object $user -Properties $script:EntraIdConnectorLabUserProperties
    }
    $groups = Get-EntraIdConnectorLabGraphCollection -Token $Token -Path "/groups?`$select=$($script:EntraIdConnectorLabGroupProperties -join ',')&`$top=999"
    foreach ($group in @($groups | Where-Object { -not (Test-EntraIdConnectorLabRunObject -Object $_) })) {
        $members = Get-EntraIdConnectorLabGraphCollection -Token $Token -Path "/groups/$($group.id)/members?`$select=id&`$top=999"
        $record = [ordered]@{}
        foreach ($name in $script:EntraIdConnectorLabGroupProperties) { $record[$name] = Get-EntraIdConnectorLabValue -Object $group -Name $name }
        $record['members'] = @($members | ForEach-Object { [string]$_.id } | Sort-Object)
        $snapshot["group:$($group.id)"] = ConvertTo-EntraIdConnectorLabCanonicalJson -Object ([PSCustomObject]$record) -Properties (@($script:EntraIdConnectorLabGroupProperties) + 'members')
    }
    return $snapshot
}

function Test-EntraIdConnectorLabRunObject {
    <#
    .SYNOPSIS
        Whether a user or group was made by a run: its display name, user principal name or mail nickname carries the
        run prefix root.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][object]$Object
    )

    foreach ($value in @('displayName', 'userPrincipalName', 'mailNickname' | ForEach-Object { Get-EntraIdConnectorLabValue -Object $Object -Name $_ })) {
        if ($value -and ([string]$value).StartsWith($script:EntraIdConnectorLabPrefixRoot, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function ConvertTo-EntraIdConnectorLabCanonicalJson {
    <#
    .SYNOPSIS
        The named properties of an object as JSON in a fixed order, so two reads of an unchanged object compare equal.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][object]$Object,
        [Parameter(Mandatory)][string[]]$Properties
    )

    $record = [ordered]@{}
    foreach ($name in $Properties) {
        $value = Get-EntraIdConnectorLabValue -Object $Object -Name $name
        $record[$name] = if ($value -is [array] -or $value -is [System.Collections.IList]) { @($value | ForEach-Object { [string]$_ } | Sort-Object) } else { $value }
    }
    return ConvertTo-Json -InputObject $record -Depth 5 -Compress
}

function Compare-EntraIdConnectorLabSnapshot {
    <#
    .SYNOPSIS
        What a run changed among the objects it must not touch: one line per object removed or changed. New objects
        are not listed, since the snapshot after a run is taken after its cleanup and another run may be under way.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][hashtable]$Before,
        [Parameter(Mandatory)][hashtable]$After
    )

    $differences = [System.Collections.Generic.List[string]]::new()
    foreach ($key in @($Before.Keys | Sort-Object)) {
        if (-not $After.ContainsKey($key)) { $differences.Add("$key was deleted: $($Before[$key])"); continue }
        if ($After[$key] -cne $Before[$key]) { $differences.Add("$key changed: was $($Before[$key]); now $($After[$key])") }
    }
    return , [string[]]$differences.ToArray()
}

function Remove-EntraIdConnectorLabObject {
    <#
    .SYNOPSIS
        Deletes every user and group whose display name starts with the prefix: this run's (a run prefix), or, with
        -OlderThanHours, any run's (jimit-) created before then, left by a run that failed. Returns how many it
        deleted. Deleted objects stay in Entra ID's deleted items for 30 days, under names no later run reuses.

    .DESCRIPTION
        Graph's lists lag its writes, so a list read just after a run can miss what it made, and can still show what was
        just deleted. A run's cleanup therefore lists again, ten seconds apart, until three lists in a row find nothing
        it has not already deleted (up to eighteen lists). Leftovers are old enough to be listed, so one pass does.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test harness; deletes only what runs created.')]
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Run')][string]$Prefix,
        [Parameter(Mandatory, ParameterSetName = 'Leftovers')][ValidateRange(1, 720)][int]$OlderThanHours,
        [Parameter(Mandatory)][securestring]$Token
    )

    if ($PSCmdlet.ParameterSetName -eq 'Run') {
        Assert-EntraIdConnectorLabRunPrefix -Prefix $Prefix
        $match = $Prefix
        $cutoff = [DateTime]::MaxValue
        $quietListingsNeeded = 3
    }
    else {
        $match = $script:EntraIdConnectorLabPrefixRoot
        $cutoff = [DateTime]::UtcNow.AddHours(-$OlderThanHours)
        $quietListingsNeeded = 1
    }
    $deleted = 0
    foreach ($kind in 'users', 'groups') {
        $handled = [System.Collections.Generic.HashSet[string]]::new()
        $quiet = 0
        for ($listing = 1; $listing -le 18 -and $quiet -lt $quietListingsNeeded; $listing++) {
            if ($listing -gt 1) { Start-Sleep -Seconds 10 }
            $found = @(Get-EntraIdConnectorLabGraphCollection -Token $Token -Path "/$kind`?`$filter=startswith(displayName,'$match')&`$select=id,displayName,createdDateTime&`$top=999")
            # The filter is Graph's; the prefix is checked again here, case and all, before anything is deleted.
            $doomed = @($found | Where-Object {
                $created = Get-EntraIdConnectorLabValue -Object $_ -Name 'createdDateTime'
                -not $handled.Contains([string]$_.id) -and
                ([string]$_.displayName).StartsWith($match, [StringComparison]::Ordinal) -and
                (-not $created -or ([DateTime]$created).ToUniversalTime() -lt $cutoff) })
            $quiet = if ($doomed.Count -eq 0) { $quiet + 1 } else { 0 }
            if ($doomed.Count -eq 0) { continue }
            foreach ($object in $doomed) { $null = $handled.Add([string]$object.id) }
            $requests = @($doomed | ForEach-Object { @{ method = 'DELETE'; url = "/$kind/$($_.id)" } })
            $answers = Invoke-EntraIdConnectorLabGraphBatch -Token $Token -Requests $requests
            for ($index = 0; $index -lt $requests.Count; $index++) {
                $status = $answers[$index].Status
                if ($status -ge 200 -and $status -lt 300) { $deleted++ }
                # Already gone (JIM deleted it, or another run's sweep did) is what was wanted.
                elseif ($status -ne 404) {
                    throw "Microsoft Graph refused DELETE $($requests[$index].url) ($status): $(Get-EntraIdConnectorLabBatchError $answers[$index])"
                }
            }
        }
    }
    return $deleted
}

function Remove-EntraIdConnectorLabDeletedUser {
    <#
    .SYNOPSIS
        Purges users from Entra ID's deleted items, by id, and returns how many it purged. One already purged, or never
        deleted, counts as nothing to do.

    .DESCRIPTION
        A deleted user counts against the tenant's object limit for 30 days. Purged, it counts for a quarter, still for
        30 days (measured on the JIM test tenant), so a scale run purges what it made rather than waiting. The CI app does
        this: it holds User.DeleteRestore.All. Groups cannot be purged with an application permission, so a scale run
        keeps its groups few.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test harness; purges only what runs created.')]
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][securestring]$Token,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Id
    )

    if ($Id.Count -eq 0) { return 0 }
    $requests = @($Id | ForEach-Object { @{ method = 'DELETE'; url = "/directory/deletedItems/$_" } })
    $answers = Invoke-EntraIdConnectorLabGraphBatch -Token $Token -Requests $requests
    $purged = 0
    for ($index = 0; $index -lt $requests.Count; $index++) {
        $status = $answers[$index].Status
        if ($status -ge 200 -and $status -lt 300) { $purged++ }
        elseif ($status -ne 404) {
            throw "Microsoft Graph refused DELETE $($requests[$index].url) ($status): $(Get-EntraIdConnectorLabBatchError $answers[$index])"
        }
    }
    return $purged
}
