# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

#Requires -Modules Pester

<#
.SYNOPSIS
    Pester tests for the redaction applied to a request body before it reaches the debug stream.

.DESCRIPTION
    Invoke-JIMApi writes the outgoing request body to Write-Debug so an operator can see what a
    cmdlet sent. Several cmdlets send a password: Set-JIMMetaverseObjectPassword,
    Sync-JIMMetaverseObjectPassword, Set-JIMConnectedSystemObjectPassword, and
    Set-JIMSyncRuleInitialPassword all put a plaintext value in the body, having deliberately taken
    it as a SecureString to keep it out of the session history in the first place. Running any of
    them under -Debug, or in a transcript with $DebugPreference set, wrote that value out again in
    clear text.

    This is JIM's never-log invariant on the client side, and it has the same standing as the
    server-side one: no password value in any log, at any level, ever.

    The redaction is by property name and is deliberately conservative. Connected System setting
    values are the case that decides the design: a service account's password travels as a
    stringValue keyed by setting id, exactly like a hostname or a base DN does, so nothing in the
    payload distinguishes them and every stringValue is redacted.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '..' 'JIM.psd1'
    Get-Module JIM -ErrorAction SilentlyContinue | Remove-Module -Force
    Import-Module $ModulePath -Force

    $script:JimModule = Get-Module JIM

    # The helper is private to the module, so invoke it in the module's own scope.
    function Invoke-Redaction {
        param($Body)

        & $script:JimModule {
            param($b)
            Get-JIMRedactedBody -Body $b
        } $Body
    }
}

Describe 'Get-JIMRedactedBody' {

    Context 'password-carrying bodies' {

        It 'redacts the password a set-password cmdlet sends' {
            $redacted = Invoke-Redaction -Body @{ password = 'Correct-Horse-Battery-Staple' }

            $redacted | Should -Not -Match 'Correct-Horse-Battery-Staple'
            $redacted | Should -Match 'password'
        }

        It 'redacts a static initial password' {
            $redacted = Invoke-Redaction -Body @{ staticPassword = 'Sh4red!nitial'; source = 'Static' }

            $redacted | Should -Not -Match 'Sh4red'
            $redacted | Should -Match 'Static'
        }

        It 'leaves the non-secret fields beside a password readable' {
            $redacted = Invoke-Redaction -Body @{
                password         = 'Correct-Horse-Battery-Staple'
                expiryBehaviour  = 'MustChangeAtNextSignIn'
            }

            $redacted | Should -Not -Match 'Correct-Horse'
            $redacted | Should -Match 'MustChangeAtNextSignIn'
        }

        It 'does not disclose the password length' {
            # The server-side invariant is explicit that neither the value nor its length is logged;
            # a redaction that varied with the input would leak the length one debug line at a time.
            $short = Invoke-Redaction -Body @{ password = 'aB1!' }
            $long = Invoke-Redaction -Body @{ password = 'a-very-considerably-longer-password-indeed' }

            $short | Should -BeExactly $long
        }
    }

    Context 'other credential-shaped names' {

        It 'redacts <Name>' -ForEach @(
            @{ Name = 'secret' }
            @{ Name = 'clientSecret' }
            @{ Name = 'apiKey' }
            @{ Name = 'token' }
            @{ Name = 'accessToken' }
            @{ Name = 'refreshToken' }
            @{ Name = 'credential' }
            @{ Name = 'passphrase' }
            @{ Name = 'privateKey' }
        ) {
            $body = @{ $Name = 'the-sensitive-value' }

            Invoke-Redaction -Body $body | Should -Not -Match 'the-sensitive-value'
        }

        It 'matches the name case-insensitively' {
            Invoke-Redaction -Body @{ PASSWORD = 'shouty-secret' } | Should -Not -Match 'shouty-secret'
        }

        It 'leaves a name that merely mentions passwords alone' {
            # These carry a state or a count, not a value, and are useful in a debug line.
            $redacted = Invoke-Redaction -Body @{
                passwordSynchronisationEnabled = $true
                passwordExpiryBehaviour        = 'ExpiresAccordingToTargetPolicy'
            }

            $redacted | Should -Match 'ExpiresAccordingToTargetPolicy'
        }
    }

    Context 'Connected System setting values' {

        It 'redacts every stringValue, because a service account password is shaped like a hostname' {
            $body = @{
                '40' = @{ stringValue = 'ldaps://dc1.corp.local' }
                '41' = @{ stringValue = 'the-service-account-password' }
                '55' = @{ intValue = 10 }
            }

            $redacted = Invoke-Redaction -Body $body

            $redacted | Should -Not -Match 'the-service-account-password'
            $redacted | Should -Not -Match 'dc1.corp.local'
        }

        It 'keeps the setting identifiers and non-string values, which is what the debug line is for' {
            $body = @{
                '40' = @{ stringValue = 'ldaps://dc1.corp.local' }
                '55' = @{ intValue = 10 }
                '56' = @{ checkboxValue = $true }
            }

            $redacted = Invoke-Redaction -Body $body

            $redacted | Should -Match '40'
            $redacted | Should -Match '55'
            $redacted | Should -Match '10'
        }
    }

    Context 'nesting and shapes' {

        It 'redacts a password nested inside an object' {
            $body = @{ configuration = @{ bind = @{ password = 'deeply-nested-secret' } } }

            Invoke-Redaction -Body $body | Should -Not -Match 'deeply-nested-secret'
        }

        It 'redacts a password inside an array element' {
            $body = @{ accounts = @(
                    @{ name = 'alice'; password = 'array-element-secret' }
                    @{ name = 'bob' }
                )
            }

            $redacted = Invoke-Redaction -Body $body

            $redacted | Should -Not -Match 'array-element-secret'
            $redacted | Should -Match 'alice'
        }

        It 'redacts a body that arrives already serialised as JSON' {
            $body = '{"password":"pre-serialised-secret","expiryBehaviour":"NoChange"}'

            $redacted = Invoke-Redaction -Body $body

            $redacted | Should -Not -Match 'pre-serialised-secret'
            $redacted | Should -Match 'NoChange'
        }

        It 'suppresses a string body it cannot parse rather than logging it raw' {
            # An unparseable body cannot be inspected, so it cannot be shown to be free of secrets.
            $redacted = Invoke-Redaction -Body 'password=hunter2&user=alice'

            $redacted | Should -Not -Match 'hunter2'
        }

        It 'handles a PSCustomObject body' {
            $body = [PSCustomObject]@{ password = 'custom-object-secret'; name = 'alice' }

            $redacted = Invoke-Redaction -Body $body

            $redacted | Should -Not -Match 'custom-object-secret'
            $redacted | Should -Match 'alice'
        }

        It 'returns something for a null body rather than throwing' {
            { Invoke-Redaction -Body $null } | Should -Not -Throw
        }
    }
}

Describe 'Invoke-JIMApi debug output' {

    It 'routes the body through the redactor rather than logging it raw' {
        $source = Get-Content (Join-Path $PSScriptRoot '..' 'Private' 'Invoke-JIMApi.ps1') -Raw

        $source | Should -Not -Match 'Write-Debug\s+"Request body:\s*\$\(\$params\.Body\)"'
        $source | Should -Match 'Get-JIMRedactedBody'
    }

    It 'writes no password to any stream when a real request runs under -Debug' {
        # The end-to-end guard: that Get-JIMRedactedBody works in isolation proves nothing unless the
        # code path writing to Write-Debug actually routes through it.
        #
        # This needs the layer below as well (issue #1516, which stops PowerShell's own
        # Invoke-RestMethod dumping the request beside JIM's line). That is why this assertion lives
        # here rather than there: it can only be true once both halves are in.
        $records = & $script:JimModule {
            $script:JIMConnection = @{ Url = 'https://jim.invalid'; AuthMethod = 'ApiKey'; ApiKey = 'k' }
            $DebugPreference = 'Continue'

            try {
                Invoke-JIMApiRequest -Endpoint '/api/v1/metaverse/objects/1/password' -Method 'POST' `
                    -Body @{ password = 'Correct-Horse-Battery-Staple'; expiryBehaviour = 'NoChange' }
            }
            catch {
                # Expected; the host does not resolve.
            }
        } 5>&1 2>&1 3>&1 4>&1 6>&1

        $written = ($records | Out-String)

        $written | Should -Not -Match 'Correct-Horse-Battery-Staple'
        $written | Should -Match 'Request body:'
        $written | Should -Match 'NoChange'
    }
}
