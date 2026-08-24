# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for issue #1516: the module must not write a credential to the debug stream.

.DESCRIPTION
    With the debug stream active, PowerShell's own Invoke-RestMethod and Invoke-WebRequest emit a
    "WebRequest Detail" record dumping the request headers and body verbatim. JIM sends its API key
    as the X-API-Key header on every authenticated call, so that record carried the key every time
    somebody ran a cmdlet under -Debug, or in a session with $DebugPreference set. The OAuth token
    exchange leaked both halves of itself the same way, and the four password cmdlets leaked the
    password they had deliberately taken as a SecureString.

    Nothing JIM logs is involved, which is what made it worth a guard of its own: the leak is a
    cmdlet JIM does not own, narrating a request JIM built.

    The fix is -Debug:$false at each call, which suppresses the built-in record and leaves JIM's own
    Write-Debug output alone. That output is the reason an operator passes -Debug at all, so a test
    below pins it as well: turning the debug stream off wholesale would "pass" the leak tests while
    removing the feature.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    $script:JimModule = Get-Module JIM

    # Runs a real request under -Debug against a host that does not resolve, and returns everything
    # that reached any stream before it failed. The failure is the point: what matters is what was
    # written on the way out, not the response.
    function Get-EverythingWritten {
        param([hashtable]$Body = @{ name = 'alice' })

        $records = & $script:JimModule {
            param($requestBody)

            $script:JIMConnection = @{
                Url        = 'https://jim.invalid'
                AuthMethod = 'ApiKey'
                ApiKey     = 'jim_ak_the_api_key_value'
            }
            $DebugPreference = 'Continue'

            try {
                Invoke-JIMApiRequest -Endpoint '/api/v1/metaverse/objects/1/password' -Method 'POST' -Body $requestBody
            }
            catch {
                # Expected; the host does not resolve.
            }
        } $Body 5>&1 2>&1 3>&1 4>&1 6>&1

        return ($records | Out-String)
    }
}

Describe 'Credentials and the debug stream' {

    It 'does not write the API key when a request runs under -Debug' {
        Get-EverythingWritten | Should -Not -Match 'jim_ak_the_api_key_value'
    }

    It 'does not write the request headers verbatim' {
        # The header dump is the shape of the leak rather than the key itself; asserting on it too
        # means a future PowerShell that renames the record still fails this if it starts printing.
        Get-EverythingWritten | Should -Not -Match 'X-API-Key:'
    }

    It 'still writes JIM own diagnostics under -Debug' {
        # The guard must not be implemented by silencing the debug stream: the request line is what
        # an operator passes -Debug to see.
        $written = Get-EverythingWritten

        $written | Should -Match 'Invoking JIM API: POST'
        $written | Should -Match 'jim.invalid/api/v1/metaverse/objects/1/password'
    }
}

Describe 'HTTP calls that carry a credential' {

    It 'pass -Debug:$false so the built-in request dump cannot leak it' {
        # A source-shape sweep, because the defect is the ABSENCE of the guard on a call somebody adds
        # later, which no per-call test can see. The rule is per FILE rather than per call: every HTTP
        # call in a file that handles credentials is guarded, including the OIDC discovery call, which
        # carries nothing itself. That way a call added to one of these files inherits the guard rather
        # than having to remember it, and there is no exception list to keep accurate.
        #
        # The genuinely unauthenticated cmdlets (health, version, auth config) are not listed: they
        # carry nothing to leak, and the built-in debug record helps diagnose a connectivity problem.
        # Every authenticated call funnels through Invoke-JIMApi, so this list is the whole surface.
        $credentialCarryingCalls = @(
            @{ File = 'Private/Invoke-JIMApi.ps1'; Carries = 'the API key header, and a password on the set-password endpoints' }
            @{ File = 'Private/Invoke-OAuthBrowserFlow.ps1'; Carries = 'the authorisation code, refresh token and issued tokens' }
            @{ File = 'Public/Certificates/Export-JIMCertificate.ps1'; Carries = 'the API key header' }
        )

        foreach ($call in $credentialCarryingCalls) {
            $path = Join-Path $PSScriptRoot '..' $call.File
            $lines = Get-Content $path

            # Matched as an invocation rather than a mention, so a doc comment naming the cmdlet
            # (Invoke-JIMApi's 429 retry notes do) is not mistaken for an unguarded call.
            $unguarded = $lines |
                Where-Object { $_ -match '(^\s*|=\s*)Invoke-(RestMethod|WebRequest)\b' } |
                Where-Object { $_ -notmatch '-Debug:\$false' }

            $unguarded | Should -BeNullOrEmpty -Because (
                "$($call.File) carries $($call.Carries); an HTTP call there without -Debug:`$false " +
                'writes it to the debug stream verbatim')
        }
    }
}
