# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemObjectPassword {
    <#
    .SYNOPSIS
        Sets the password on one Connected System Object.

    .DESCRIPTION
        Writes the password straight to the Connected System. Nothing is staged, retried or stored: there is
        nowhere in JIM to keep a password and no second attempt worth keeping one for. The attempt is recorded
        as an Activity against the object, carrying the outcome and, where the target refused, its verbatim
        reason.

        This is the automation counterpart of the Set Password action in the administration portal, for the
        account whose provisioning password was parked, the person who never received theirs, and the reset
        that has to happen now.

        You supply the password. JIM does not generate one here: doing so would mean returning a password in a
        response body, which this API never does. Use the portal's Set Password dialog when you want JIM to
        generate a password that follows the Connected System's discovered policy.

        This is a password-reset primitive. Anyone who can call it can reset any account in this connector
        space, subject only to what the Connected System's own service account is permitted to do.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System the account lives in.

    .PARAMETER Id
        The unique identifier (GUID) of the Connected System Object.

    .PARAMETER Password
        The password to set, as a SecureString. Sent to the Connected System and nowhere else: never logged,
        never persisted by JIM, and never echoed back.

    .PARAMETER Generate
        Has JIM generate a password satisfying the policy it discovered on the Connected System, instead of you
        supplying one. Use this rather than inventing a password in your own script: JIM knows what the target
        demands, and a hand-rolled generator rediscovers the passphrase trap, where three words offer two
        character categories against a directory that wants three.

        The generated password is returned on the result's password property as a SecureString, whether or not
        -PassThru is given. JIM stores nothing and cannot give it to you again.

    .PARAMETER ExpiryBehaviour
        What happens to the password once it is set.
        Valid values: RequireChangeAtNextSignIn, ExpiresAccordingToTargetPolicy, NeverExpires.
        Defaults to RequireChangeAtNextSignIn, which is the right default for a password somebody else chose.
        A Connected System that cannot honour the choice applies what it can and reports the difference.

    .PARAMETER EnableAccount
        Enables the account as part of setting the password. Omit it to leave the account's enabled state
        untouched, which is what a reset on an already-enabled account should do. Directories that refuse to
        enable an account without a compliant password need the password first, which is why this belongs here.

    .PARAMETER Force
        Skips the confirmation prompt.

    .PARAMETER PassThru
        If specified, returns the expiry behaviour actually applied and any caveat about it.

    .OUTPUTS
        If -PassThru is specified, returns an object with these properties:

        | Property               | Description                                                              |
        |------------------------|--------------------------------------------------------------------------|
        | AppliedExpiryBehaviour | The expiry behaviour really applied, which is not always the one asked for |
        | ExpiryBehaviourWarning | Why the requested behaviour could not be honoured, or null if it was       |

        No property carries the password.

    .EXAMPLE
        $result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 3 -Id $csoId -Generate -EnableAccount -Force
        ConvertFrom-SecureString -SecureString $result.password -AsPlainText

        Has JIM produce a compliant password, sets it, enables the account, and reads back what was used.
        Capture it: this is the only chance to.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 -Password $password

        Sets the password on one account, requiring a change at the next sign-in, and prompts for confirmation
        first.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 -Password $password -EnableAccount -Force -PassThru

        Sets the password and enables the account, without prompting, and reports the expiry behaviour the
        directory actually applied.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 |
            Set-JIMConnectedSystemObjectPassword -Password $password -ExpiryBehaviour NeverExpires

        Sets the password on a piped Connected System Object, on an account whose password should not age (a
        service account, say).

    .LINK
        Get-JIMConnectedSystemObject
        Set-JIMSyncRuleInitialPassword
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'SuppliedPassword')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'SuppliedPassword')]
        [ValidateNotNull()]
        [securestring]$Password,

        [Parameter(Mandatory, ParameterSetName = 'GeneratedPassword')]
        [switch]$Generate,

        [Parameter()]
        [ValidateSet('RequireChangeAtNextSignIn', 'ExpiresAccordingToTargetPolicy', 'NeverExpires')]
        [string]$ExpiryBehaviour,

        [Parameter()]
        [switch]$EnableAccount,

        [Parameter()]
        [switch]$Force,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($Generate) {
            # Asked for by the caller, so JIM produces one that satisfies what this system itself demands. The
            # point of asking JIM rather than inventing a password is that JIM knows the target's rules; a
            # hand-rolled generator rediscovers the passphrase trap, where three words offer two character
            # categories against a directory that wants three.
            try {
                $generated = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/generate-password" -Method 'POST'
            }
            catch {
                Write-Error "Failed to generate a password for Connected System ${ConnectedSystemId}: $_"
                return
            }

            $plainPassword = $generated.password
            $generatedPassword = ConvertTo-SecureString -String $plainPassword -AsPlainText -Force
        }
        else {
            # A SecureString on the parameter so a password never sits in the session's command history in clear
            # text. It has to be unwrapped to be sent, since the wire format is JSON over TLS; the plain value is
            # held for the length of one call and nothing else in this function touches it.
            $plainPassword = ConvertFrom-SecureString -SecureString $Password -AsPlainText
        }

        if ([string]::IsNullOrWhiteSpace($plainPassword)) {
            Write-Error "A password is required."
            return
        }

        $body = @{
            password = $plainPassword
        }

        if ($PSBoundParameters.ContainsKey('ExpiryBehaviour')) {
            $body.expiryBehaviour = $ExpiryBehaviour
        }

        # Only sent when asked for. Omitting it means "leave the account's enabled state alone"; sending false
        # would ask the Connected System to disable an account nobody asked it to touch.
        if ($EnableAccount) {
            $body.enableAccount = $true
        }

        if ($Force -or $PSCmdlet.ShouldProcess($Id, "Set the password on this Connected System Object")) {
            Write-Verbose "Setting the password on Connected System Object $Id in Connected System $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id/password" -Method 'POST' -Body $body

                # A generated password is returned whatever -PassThru says: the caller never had it, and it is
                # not recoverable from anywhere once this call returns. Withholding it would set a password
                # nobody can use.
                if ($Generate) {
                    $result | Add-Member -NotePropertyName 'password' -NotePropertyValue $generatedPassword -PassThru
                }
                elseif ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to set the password on Connected System Object ${Id}: $_"
            }
        }
    }
}
