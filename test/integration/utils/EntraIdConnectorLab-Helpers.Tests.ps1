# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for EntraIdConnectorLab-Helpers.ps1: how the Entra ID Connector scenarios (036 and 037, #361) get the
    harness's own Graph access, name and clean up what they create, wait for Graph, and prove they changed nothing
    else. Neither Entra ID nor Graph is called.
#>

BeforeAll {
    # The scenarios run under strict mode, where reading a property Graph left out (the last page's @odata.nextLink, a
    # group's userPrincipalName) throws; the helpers must hold up under it, so the tests run under it too.
    Set-StrictMode -Version Latest
    . (Join-Path $PSScriptRoot 'EntraIdConnectorLab-Helpers.ps1')
    $script:Token = ConvertTo-SecureString -String 'graph-token' -AsPlainText -Force
    $script:SavedRunId = $env:GITHUB_RUN_ID
    $script:SavedRunAttempt = $env:GITHUB_RUN_ATTEMPT
}

AfterAll {
    $env:GITHUB_RUN_ID = $script:SavedRunId
    $env:GITHUB_RUN_ATTEMPT = $script:SavedRunAttempt
}

Describe 'Get-EntraIdConnectorLabRunPrefix' {

    It 'names a run by GitHub''s run id and attempt in Actions' {
        $env:GITHUB_RUN_ID = '9876'; $env:GITHUB_RUN_ATTEMPT = '2'
        Get-EntraIdConnectorLabRunPrefix | Should -BeExactly 'jimit-98762-'
    }

    It 'names a run outside Actions with six random characters, different each time' {
        $env:GITHUB_RUN_ID = $null
        $first = Get-EntraIdConnectorLabRunPrefix
        $first | Should -Match '^jimit-[0-9a-f]{6}-$'
        Get-EntraIdConnectorLabRunPrefix | Should -Not -Be $first
    }

    It 'refuses a run id that is not letters and digits' {
        { Get-EntraIdConnectorLabRunPrefix -RunId 'a b' } | Should -Throw '*letters and digits*'
    }
}

Describe 'Assert-EntraIdConnectorLabRunPrefix' {

    It 'refuses <Prefix>, which is not a whole run prefix' -ForEach @(
        @{ Prefix = '' }, @{ Prefix = 'jimit-' }, @{ Prefix = 'jimit-abc' }, @{ Prefix = 'jim' }, @{ Prefix = 'JIMIT-abc-' }, @{ Prefix = 'jimit-a*-' }
    ) {
        { Assert-EntraIdConnectorLabRunPrefix -Prefix $Prefix } | Should -Throw '*not a run prefix*'
    }

    It 'accepts a run prefix' {
        { Assert-EntraIdConnectorLabRunPrefix -Prefix 'jimit-abc123-' } | Should -Not -Throw
    }
}

Describe 'New-EntraIdConnectorLabCertificate' {

    It 'makes a certificate with its private key, valid for two days, in whole seconds' {
        $certificate = New-EntraIdConnectorLabCertificate
        try {
            $certificate.HasPrivateKey | Should -BeTrue
            ($certificate.NotAfter - $certificate.NotBefore).TotalHours | Should -BeGreaterThan 48
            ($certificate.NotAfter - $certificate.NotBefore).TotalHours | Should -BeLessThan 48.2
            $certificate.NotAfter.Millisecond | Should -Be 0
        }
        finally { $certificate.Dispose() }
    }
}

Describe 'New-EntraIdConnectorLabClientAssertion' {

    BeforeAll {
        $script:Certificate = New-EntraIdConnectorLabCertificate
        $script:Decode = { param([string]$Part)
            $padded = $Part.Replace('-', '+').Replace('_', '/'); $padded += '=' * ((4 - $padded.Length % 4) % 4)
            [Convert]::FromBase64String($padded) }
    }

    AfterAll { $script:Certificate.Dispose() }

    It 'is signed with the certificate''s private key, and names the certificate by its SHA-256 thumbprint' {
        $parts = (New-EntraIdConnectorLabClientAssertion -TenantId 'tenant' -ClientId 'client' -Certificate $script:Certificate).Split('.')
        $parts.Count | Should -Be 3
        $header = [System.Text.Encoding]::UTF8.GetString((& $script:Decode $parts[0])) | ConvertFrom-Json
        $header.alg | Should -Be 'RS256'
        (& $script:Decode $header.'x5t#S256') | Should -Be ([System.Security.Cryptography.SHA256]::HashData($script:Certificate.RawData))
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($script:Certificate)
        $rsa.VerifyData([System.Text.Encoding]::ASCII.GetBytes("$($parts[0]).$($parts[1])"), (& $script:Decode $parts[2]),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1) | Should -BeTrue
    }

    It 'is addressed to the tenant''s token endpoint, from the app, for ten minutes' {
        $payload = [System.Text.Encoding]::UTF8.GetString((& $script:Decode (New-EntraIdConnectorLabClientAssertion -TenantId 'tenant' -ClientId 'client' -Certificate $script:Certificate).Split('.')[1])) | ConvertFrom-Json
        $payload.aud | Should -Be 'https://login.microsoftonline.com/tenant/oauth2/v2.0/token'
        $payload.iss | Should -Be 'client'
        $payload.sub | Should -Be 'client'
        ($payload.exp - $payload.nbf) | Should -Be 600
    }
}

Describe 'Get-EntraIdConnectorLabToken' {

    BeforeAll { $script:Certificate = New-EntraIdConnectorLabCertificate }
    AfterAll { $script:Certificate.Dispose() }

    BeforeEach { Mock Start-Sleep {} }

    It 'signs in with a client assertion and returns the token as a secure string' {
        Mock Invoke-EntraIdConnectorLabHttp { [PSCustomObject]@{ StatusCode = 200; Body = [PSCustomObject]@{ access_token = 'connector-token' } } }

        $token = Get-EntraIdConnectorLabToken -TenantId 'tenant' -ClientId 'client' -Certificate $script:Certificate

        [System.Net.NetworkCredential]::new('', $token).Password | Should -Be 'connector-token'
        Should -Invoke Invoke-EntraIdConnectorLabHttp -Times 1 -ParameterFilter {
            $Body.grant_type -eq 'client_credentials' -and $Body.client_assertion_type -like '*jwt-bearer' -and $Body.client_assertion -and -not $Body.client_secret }
    }

    It 'tries again while Entra ID does not yet know a certificate added moments ago' {
        $script:Attempts = 0
        Mock Invoke-EntraIdConnectorLabHttp {
            $script:Attempts++
            if ($script:Attempts -lt 3) { [PSCustomObject]@{ StatusCode = 401; Body = [PSCustomObject]@{ error_description = "AADSTS700027: The certificate with identifier used to sign the client assertion is not registered on application.`nTrace ID: x" } } }
            else { [PSCustomObject]@{ StatusCode = 200; Body = [PSCustomObject]@{ access_token = 'connector-token' } } }
        }

        $null = Get-EntraIdConnectorLabToken -TenantId 'tenant' -ClientId 'client' -Certificate $script:Certificate

        $script:Attempts | Should -Be 3
    }

    It 'fails at once on a refusal waiting will not cure, naming Entra ID''s reason' {
        Mock Invoke-EntraIdConnectorLabHttp { [PSCustomObject]@{ StatusCode = 400; Body = [PSCustomObject]@{ error_description = "AADSTS90002: Tenant 'tenant' not found.`nTrace ID: x" } } }

        { Get-EntraIdConnectorLabToken -TenantId 'tenant' -ClientId 'client' -Certificate $script:Certificate } | Should -Throw '*AADSTS90002*'
        Should -Invoke Invoke-EntraIdConnectorLabHttp -Times 1
    }

    It 'gives up when the timeout passes' {
        Mock Invoke-EntraIdConnectorLabHttp { [PSCustomObject]@{ StatusCode = 401; Body = [PSCustomObject]@{ error_description = 'AADSTS700027: not registered' } } }

        { Get-EntraIdConnectorLabToken -TenantId 'tenant' -ClientId 'client' -Certificate $script:Certificate -TimeoutSeconds 0 } | Should -Throw '*AADSTS700027*'
    }
}

Describe 'Invoke-EntraIdConnectorLabGraph' {

    BeforeEach { Mock Start-Sleep {} }

    It 'waits as Graph''s Retry-After asks when throttled, then returns the body' {
        $script:Attempts = 0
        Mock Invoke-EntraIdConnectorLabHttp {
            $script:Attempts++
            if ($script:Attempts -eq 1) { [PSCustomObject]@{ StatusCode = 429; Headers = @{ 'Retry-After' = @('7') }; Body = $null } }
            else { [PSCustomObject]@{ StatusCode = 200; Body = [PSCustomObject]@{ id = 'u1' } } }
        }

        (Invoke-EntraIdConnectorLabGraph -Token $script:Token -Path '/users/u1').id | Should -Be 'u1'
        Should -Invoke Start-Sleep -Times 1 -ParameterFilter { $Seconds -eq 7 }
    }

    It 'throws on any other failure with Graph''s own error, without trying again' {
        Mock Invoke-EntraIdConnectorLabHttp { [PSCustomObject]@{ StatusCode = 403; Body = [PSCustomObject]@{ error = [PSCustomObject]@{ code = 'Authorization_RequestDenied'; message = 'Insufficient privileges' } } } }

        { Invoke-EntraIdConnectorLabGraph -Token $script:Token -Path '/users' } | Should -Throw '*(403)*Authorization_RequestDenied*'
        Should -Invoke Invoke-EntraIdConnectorLabHttp -Times 1
    }

    It 'throws with the response text when Graph''s error is not JSON' {
        Mock Invoke-EntraIdConnectorLabHttp { [PSCustomObject]@{ StatusCode = 502; Body = $null; Content = 'Bad Gateway' } }

        { Invoke-EntraIdConnectorLabGraph -Token $script:Token -Path '/users' } | Should -Throw '*(502)*Bad Gateway*'
    }

    It 'sends a body as JSON with the bearer token' {
        Mock Invoke-EntraIdConnectorLabHttp { [PSCustomObject]@{ StatusCode = 201; Body = [PSCustomObject]@{ id = 'g1' } } }

        $null = Invoke-EntraIdConnectorLabGraph -Token $script:Token -Method Post -Path '/groups' -Body @{ displayName = 'jimit-x-g' }

        Should -Invoke Invoke-EntraIdConnectorLabHttp -ParameterFilter {
            $Headers.Authorization -eq 'Bearer graph-token' -and $ContentType -eq 'application/json' -and ($Body | ConvertFrom-Json).displayName -eq 'jimit-x-g' }
    }
}

Describe 'Add-EntraIdConnectorLabCredential and Remove-EntraIdConnectorLabCredential' {

    BeforeAll { $script:Certificate = New-EntraIdConnectorLabCertificate }
    AfterAll { $script:Certificate.Dispose() }

    BeforeEach {
        $script:Patches = [System.Collections.Generic.List[object]]::new()
        $jims = [PSCustomObject]@{ keyId = 'jim-key'; displayName = 'JIM Entra ID Connector (CI)'; customKeyIdentifier = 'QUJD'; type = 'AsymmetricX509Cert'; usage = 'Verify'; key = 'amlt'; endDateTime = [DateTime]::UtcNow.AddDays(300) }
        $stale = [PSCustomObject]@{ keyId = 'stale-key'; displayName = 'JIM integration harness'; customKeyIdentifier = 'REVG'; type = 'AsymmetricX509Cert'; usage = 'Verify'; key = 'b2xk'; endDateTime = [DateTime]::UtcNow.AddHours(-1) }
        $live = [PSCustomObject]@{ keyId = 'live-key'; displayName = 'JIM integration harness'; customKeyIdentifier = 'R0hJ'; type = 'AsymmetricX509Cert'; usage = 'Verify'; key = 'bGl2ZQ=='; endDateTime = [DateTime]::UtcNow.AddHours(20) }
        $script:Existing = @($jims, $stale, $live)
        Mock Invoke-EntraIdConnectorLabGraph -ParameterFilter { $Method -eq 'Get' -or -not $Method } { [PSCustomObject]@{ keyCredentials = $script:Existing } }
        Mock Invoke-EntraIdConnectorLabGraph -ParameterFilter { $Method -eq 'Patch' } { $script:Patches.Add($Body) }
    }

    It 'adds the harness''s certificate, keeping every other credential exactly as Graph returned it' {
        $keyId = Add-EntraIdConnectorLabCredential -AccessToken $script:Token -ApplicationObjectId 'app' -Certificate $script:Certificate

        $sent = @($script:Patches[0].keyCredentials)
        ($sent | Where-Object { $_.keyId -eq 'jim-key' }) | Should -Be $script:Existing[0]
        ($sent | Where-Object { $_.keyId -eq 'live-key' }) | Should -Be $script:Existing[2]
        $new = $sent | Where-Object { $_.keyId -eq $keyId }
        $new.displayName | Should -Be 'JIM integration harness'
        $new.key | Should -Be ([Convert]::ToBase64String($script:Certificate.RawData))
    }

    It 'removes harness certificates that have expired, left by failed runs, and nothing else' {
        $null = Add-EntraIdConnectorLabCredential -AccessToken $script:Token -ApplicationObjectId 'app' -Certificate $script:Certificate

        @($script:Patches[0].keyCredentials | ForEach-Object { $_.keyId }) | Should -Not -Contain 'stale-key'
        @($script:Patches[0].keyCredentials).Count | Should -Be 3
    }

    It 'removes only the certificate named, keeping the rest exactly as Graph returned them' {
        Remove-EntraIdConnectorLabCredential -AccessToken $script:Token -ApplicationObjectId 'app' -KeyId 'live-key'

        $sent = @($script:Patches[0].keyCredentials)
        @($sent | ForEach-Object { $_.keyId }) | Should -Be @('jim-key', 'stale-key')
        $sent[0] | Should -Be $script:Existing[0]
    }

    It 'changes nothing when the certificate is already gone' {
        Remove-EntraIdConnectorLabCredential -AccessToken $script:Token -ApplicationObjectId 'app' -KeyId 'missing'

        $script:Patches.Count | Should -Be 0
    }
}

Describe 'Wait-EntraIdConnectorLabCondition' {

    BeforeEach { Mock Start-Sleep {} }

    It 'returns what the condition returns once it holds' {
        $script:Polls = 0
        $result = Wait-EntraIdConnectorLabCondition -Description 'the user' -Condition { $script:Polls++; if ($script:Polls -ge 3) { 'found' } }

        $result | Should -Be 'found'
        $script:Polls | Should -Be 3
    }

    It 'throws, naming what it waited for, when the timeout passes' {
        { Wait-EntraIdConnectorLabCondition -Description 'jimit-x-alice to be disabled' -Condition { $false } -TimeoutSeconds 0 } |
            Should -Throw '*jimit-x-alice to be disabled*'
    }
}

Describe 'Snapshot of the accounts a run must not touch' {

    BeforeEach {
        $script:Users = @(
            [PSCustomObject]@{ id = 'admin'; displayName = 'Admin'; userPrincipalName = 'admin@tenant'; jobTitle = 'Boss'; businessPhones = @('2', '1'); accountEnabled = $true }
            [PSCustomObject]@{ id = 'run-user'; displayName = 'jimit-abc-alice'; userPrincipalName = 'jimit-abc-alice@tenant'; accountEnabled = $true })
        $script:Groups = @(
            [PSCustomObject]@{ id = 'staff'; displayName = 'Staff'; mailNickname = 'staff'; securityEnabled = $true; groupTypes = @() }
            [PSCustomObject]@{ id = 'run-group'; displayName = 'jimit-abc-sales'; mailNickname = 'jimit-abc-sales'; securityEnabled = $true; groupTypes = @() })
        $script:Members = @{ staff = @([PSCustomObject]@{ id = 'admin' }) }
        Mock Get-EntraIdConnectorLabGraphCollection {
            switch -Wildcard ($Path) {
                '/users`?*' { $script:Users }
                '/groups`?*' { $script:Groups }
                '/groups/*/members*' { @($script:Members[($Path -split '/')[2]]) }
            }
        }
    }

    It 'leaves out everything a run made' {
        $snapshot = Get-EntraIdConnectorLabSnapshot -Token $script:Token

        @($snapshot.Keys | Sort-Object) | Should -Be @('group:staff', 'user:admin')
    }

    It 'finds nothing changed when nothing was, whatever order Graph lists multiple values in' {
        $before = Get-EntraIdConnectorLabSnapshot -Token $script:Token
        $script:Users[0].businessPhones = @('1', '2')
        $after = Get-EntraIdConnectorLabSnapshot -Token $script:Token

        Compare-EntraIdConnectorLabSnapshot -Before $before -After $after | Should -BeNullOrEmpty
    }

    It 'reports a property changed on an account the run did not make' {
        $before = Get-EntraIdConnectorLabSnapshot -Token $script:Token
        $script:Users[0].jobTitle = 'Intern'
        $after = Get-EntraIdConnectorLabSnapshot -Token $script:Token

        $differences = Compare-EntraIdConnectorLabSnapshot -Before $before -After $after
        @($differences).Count | Should -Be 1
        $differences[0] | Should -BeLike 'user:admin changed*Boss*Intern*'
    }

    It 'reports a member added to a group the run did not make' {
        $before = Get-EntraIdConnectorLabSnapshot -Token $script:Token
        $script:Members.staff += [PSCustomObject]@{ id = 'run-user' }
        $after = Get-EntraIdConnectorLabSnapshot -Token $script:Token

        (Compare-EntraIdConnectorLabSnapshot -Before $before -After $after)[0] | Should -BeLike 'group:staff changed*run-user*'
    }

    It 'reports an account deleted' {
        $before = Get-EntraIdConnectorLabSnapshot -Token $script:Token
        $script:Users = @($script:Users[1])
        $after = Get-EntraIdConnectorLabSnapshot -Token $script:Token

        (Compare-EntraIdConnectorLabSnapshot -Before $before -After $after)[0] | Should -BeLike 'user:admin was deleted*'
    }
}

Describe 'Get-EntraIdConnectorLabGraphCollection' {

    It 'follows next links, and stops at the page that has none' {
        Mock Invoke-EntraIdConnectorLabGraph {
            if ($Path -eq '/users') { [PSCustomObject]@{ value = @([PSCustomObject]@{ id = 'u1' }, [PSCustomObject]@{ id = 'u2' }); '@odata.nextLink' = 'https://graph.microsoft.com/v1.0/users?$skiptoken=x' } }
            else { [PSCustomObject]@{ value = @([PSCustomObject]@{ id = 'u3' }) } }
        }

        @(Get-EntraIdConnectorLabGraphCollection -Token $script:Token -Path '/users' | ForEach-Object { $_.id }) | Should -Be @('u1', 'u2', 'u3')
    }

    It 'returns nothing for an empty collection' {
        Mock Invoke-EntraIdConnectorLabGraph { [PSCustomObject]@{ value = @() } }

        @(Get-EntraIdConnectorLabGraphCollection -Token $script:Token -Path '/groups').Count | Should -Be 0
    }
}

Describe 'Test-EntraIdConnectorLabRunObject' {

    It 'tells a group by its display name or mail nickname, though a group has no user principal name' {
        Test-EntraIdConnectorLabRunObject -Object ([PSCustomObject]@{ id = 'g'; displayName = 'jimit-abc-sales'; mailNickname = 'x' }) | Should -BeTrue
        Test-EntraIdConnectorLabRunObject -Object ([PSCustomObject]@{ id = 'g'; displayName = 'Staff'; mailNickname = 'staff' }) | Should -BeFalse
    }

    It 'tells a user by any of the three' {
        Test-EntraIdConnectorLabRunObject -Object ([PSCustomObject]@{ displayName = 'Alice'; userPrincipalName = 'jimit-abc-alice@t'; mailNickname = 'a' }) | Should -BeTrue
    }
}

Describe 'Remove-EntraIdConnectorLabObject' {

    BeforeEach {
        $script:Deleted = [System.Collections.Generic.List[string]]::new()
        $old = [DateTime]::UtcNow.AddHours(-30).ToString('o'); $recent = [DateTime]::UtcNow.AddHours(-1).ToString('o')
        $script:Found = @{
            users = @(
                [PSCustomObject]@{ id = 'mine'; displayName = 'jimit-abc-alice'; createdDateTime = $recent }
                [PSCustomObject]@{ id = 'other-run-recent'; displayName = 'jimit-xyz-bob'; createdDateTime = $recent }
                [PSCustomObject]@{ id = 'other-run-old'; displayName = 'jimit-xyz-carol'; createdDateTime = $old }
                [PSCustomObject]@{ id = 'case'; displayName = 'JIMIT-abc-dave'; createdDateTime = $old })
            groups = @([PSCustomObject]@{ id = 'my-group'; displayName = 'jimit-abc-sales'; createdDateTime = $recent })
        }
        # Graph's startswith ignores case, so the stub does too: the helper must check the prefix itself.
        Mock Get-EntraIdConnectorLabGraphCollection {
            $kind = ($Path -split '[/?]')[1]
            $prefix = [regex]::Match($Path, "startswith\(displayName,'([^']*)'\)").Groups[1].Value
            @($script:Found[$kind] | Where-Object { $_.displayName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
        }
        Mock Invoke-EntraIdConnectorLabGraph -ParameterFilter { $Method -eq 'Delete' } {
            $script:Deleted.Add($Path)
            $kind, $id = ($Path -split '/')[1, 2]
            $script:Found[$kind] = @($script:Found[$kind] | Where-Object { $_.id -ne $id })
        }
        Mock Start-Sleep {}
    }

    It 'deletes this run''s users and groups, and nothing of any other run''s' {
        Remove-EntraIdConnectorLabObject -Prefix 'jimit-abc-' -Token $script:Token | Should -Be 2

        @($script:Deleted) | Should -Be @('/users/mine', '/groups/my-group')
    }

    It 'sweeps any run''s leftovers older than the age given, and nothing newer' {
        Remove-EntraIdConnectorLabObject -OlderThanHours 24 -Token $script:Token | Should -Be 1

        @($script:Deleted) | Should -Be @('/users/other-run-old')
    }

    It 'keeps listing until Graph has listed everything the run made, since a list lags the writes before it' {
        # Graph listed three of the run's objects at first and the fourth only on a later read.
        $late = [PSCustomObject]@{ id = 'late'; displayName = 'jimit-abc-erin'; createdDateTime = [DateTime]::UtcNow.ToString('o') }
        $script:Listings = 0
        Mock Get-EntraIdConnectorLabGraphCollection -ParameterFilter { $Path -like '/users*' } {
            $script:Listings++
            if ($script:Listings -eq 2) { $script:Found.users = @($script:Found.users) + $late }
            @($script:Found.users | Where-Object { $_.displayName.StartsWith('jimit-abc-', [StringComparison]::Ordinal) })
        }

        Remove-EntraIdConnectorLabObject -Prefix 'jimit-abc-' -Token $script:Token | Should -Be 3

        @($script:Deleted) | Should -Be @('/users/mine', '/users/late', '/groups/my-group')
    }

    It 'refuses a prefix that is not a whole run prefix, deleting nothing' {
        { Remove-EntraIdConnectorLabObject -Prefix 'jimit-' -Token $script:Token } | Should -Throw '*not a run prefix*'
        $script:Deleted.Count | Should -Be 0
    }

    It 'counts an object already gone as done, and fails on any other refusal' {
        Mock Invoke-EntraIdConnectorLabGraph -ParameterFilter { $Method -eq 'Delete' -and $Path -eq '/users/mine' } { throw 'Microsoft Graph refused DELETE /users/mine (404): Request_ResourceNotFound' }
        { Remove-EntraIdConnectorLabObject -Prefix 'jimit-abc-' -Token $script:Token } | Should -Not -Throw

        Mock Invoke-EntraIdConnectorLabGraph -ParameterFilter { $Method -eq 'Delete' -and $Path -eq '/users/mine' } { throw 'Microsoft Graph refused DELETE /users/mine (403): Authorization_RequestDenied' }
        { Remove-EntraIdConnectorLabObject -Prefix 'jimit-abc-' -Token $script:Token } | Should -Throw '*(403)*'
    }
}
