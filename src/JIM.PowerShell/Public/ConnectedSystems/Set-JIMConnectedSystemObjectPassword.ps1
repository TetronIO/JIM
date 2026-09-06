# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemObjectPassword {
    <#
    .SYNOPSIS
        Sets the password on one Connected System Object.

    .DESCRIPTION
        The account-scoped form of Set Password: the same operation as Set-JIMMetaverseObjectPassword with this
        one account named, for scripts that hold the account rather than the person. JIM queues the change,
        encrypted, and the Password Delivery Service writes it within about a second, whatever the
        synchronisation engine is doing; by default the command waits up to ten seconds and tells you what the
        account did with the password.

        The password is held only until it is delivered; once the account has it, JIM's copy is gone. A copy the
        system refused is kept, still encrypted, so JIM can finish the job once the cause is dealt with, until the
        change expires or retention removes it. It is never logged, never returned, and never recorded on an
        Activity. A system that was unreachable is retried on JIM's own clock; one that refused the password parks
        it, with its own words in the target's Message, for you to look at. A Parked target is reported as a
        non-terminating error as well, so a script that stops on errors stops on a refusal; the result is written
        to the pipeline first either way.

        This is the automation counterpart of the Set Password action on a Connected System Object in the
        administration portal, for the new starter about to sign in for the first time, the account whose
        provisioning password was parked, and the reset that has to happen now. The object must be joined to a
        Metaverse Object: a password belongs to a person, and an account nobody is joined to has nowhere to record
        it.

        Supply the password with -Password, or use -Generate to have JIM produce one that follows the Connected
        System's discovered policy. A generated password is returned once, on the result's GeneratedPassword
        property; JIM cannot give it to you again.

        This resets the password on whichever account it is pointed at. Anyone who can call it can reset any
        account in this connector space, subject only to what the Connected System's own service account is
        permitted to do. The account is delivered to even where the system's Password Synchronisation is switched
        off; you named it.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System the account lives in.

    .PARAMETER Id
        The unique identifier (GUID) of the Connected System Object.

    .PARAMETER Password
        The password to set, as a SecureString. Encrypted before JIM stores it and held only until delivered;
        never logged, never returned, and never recorded on an Activity.

    .PARAMETER Generate
        Has JIM generate a password satisfying the policy it discovered on the Connected System, instead of you
        supplying one. Use this rather than inventing a password in your own script: JIM knows what the target
        demands, and a hand-rolled generator rediscovers the passphrase trap, where three words offer two
        character categories against a directory that wants three.

        The generated password is returned on the result's GeneratedPassword property as a SecureString. That is
        the only chance to capture it.

    .PARAMETER ExpiryBehaviour
        What happens to the password once it is set.
        Valid values: RequireChangeAtNextSignIn, ExpiresAccordingToTargetPolicy, NeverExpires.
        Defaults to RequireChangeAtNextSignIn, which is the right default for a password somebody else chose.
        A Connected System that cannot honour the choice applies what it can and says so in the target's Message.

    .PARAMETER EnableAccount
        Enables the account as part of setting the password. Omit it to leave the account's enabled state
        untouched, which is what a reset on an already-enabled account should do. Directories that refuse to
        enable an account without a compliant password need the password first, which is why this belongs here.

    .PARAMETER Wait
        How many seconds, from 0 to 30, to wait for the account to answer before returning. Defaults to 10. Pass 0
        to return as soon as the change is recorded. A wait ends early once the target has settled; one still
        Queued or Delivering when it runs out is reported as such, with Settled false, and its outcome appears on
        the Activity and the person's Password tab once it lands.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        One PSCustomObject describing the change, in the same shape Set-JIMMetaverseObjectPassword returns:

        | Property          | Description                                                                          |
        |-------------------|--------------------------------------------------------------------------------------|
        | ActivityId        | The Activity recording the change; its child holds the account's outcome             |
        | Origin            | Always Explicit: the account was named                                               |
        | Settled           | Whether the account had reached an outcome you need not wait on when this returned   |
        | Targets           | One entry, for this account's Connected System, described below                      |
        | GeneratedPassword | The password JIM produced, as a SecureString; present only with -Generate            |

        The entry under Targets:

        | Property                | Description                                                                    |
        |-------------------------|--------------------------------------------------------------------------------|
        | ConnectedSystemId       | The Connected System                                                           |
        | ConnectedSystemName     | Its name                                                                       |
        | ConnectedSystemObjectId | The account                                                                    |
        | Enabled                 | Whether the system is taking propagated passwords; this account is delivered to either way |
        | State                   | Queued, Delivering, Set, Retrying, Parked, Expired or Cancelled                |
        | NextAttemptAt           | When the next attempt falls due, for a Retrying target                         |
        | Message                 | The system's own words on its most recent outcome                              |
        | AttemptCount            | How many delivery attempts this account has had                                |

        No property carries the password you supplied.

    .EXAMPLE
        $result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 3 -Id $csoId -Generate -EnableAccount -Force
        ConvertFrom-SecureString -SecureString $result.GeneratedPassword -AsPlainText

        Has JIM produce a compliant password, sets it, enables the account, and reads back what was used.
        Capture it: this is the only chance to.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 -Password $password

        Sets the password on one account, requiring a change at the next sign-in, prompting for confirmation
        first and waiting up to ten seconds to report what the account did with it.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        $result = Set-JIMConnectedSystemObjectPassword -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 -Password $password -EnableAccount -Force
        $result.Targets[0] | Select-Object State, Message

        Sets the password and enables the account, without prompting, and shows whether the directory took it.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Get-JIMConnectedSystemObject -ConnectedSystemId 1 -Id 3f2a91c4-5b6d-4e7f-8a90-1b2c3d4e5f60 |
            Set-JIMConnectedSystemObjectPassword -Password $password -ExpiryBehaviour NeverExpires

        Sets the password on a piped Connected System Object, on an account whose password should not age (a
        service account, say).

    .LINK
        Set-JIMMetaverseObjectPassword
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
        [ValidateRange(0, 30)]
        [int]$Wait,

        [Parameter()]
        [switch]$Force
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

        # Sent only when asked for. The server's default is the contract for a request that names no wait, and
        # an explicit value would pin it into every script written against this version.
        if ($PSBoundParameters.ContainsKey('Wait')) {
            $body.wait = $Wait
        }

        if (-not ($Force -or $PSCmdlet.ShouldProcess($Id, "Set the password on this Connected System Object"))) {
            return
        }

        Write-Verbose "Setting the password on Connected System Object $Id in Connected System $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/$Id/password" -Method 'POST' -Body $body
        }
        catch {
            Write-Error "Failed to set the password on Connected System Object ${Id}: $_"
            return
        }

        # A generated password is carried on the result: the caller never had it, and it is not recoverable from
        # anywhere once this call returns. Withholding it would set a password nobody can use.
        if ($Generate) {
            $result | Add-Member -NotePropertyName 'GeneratedPassword' -NotePropertyValue $generatedPassword -Force
        }

        # The result first; then the refusal, in the system's own words, because a parked password is something
        # the caller has to act on. The result rides on the error's TargetObject, so a script that stops on
        # errors can still read it from the exception it caught.
        $result

        foreach ($parkedTarget in @($result.Targets | Where-Object { $_.State -eq 'Parked' })) {
            Write-Error -Message "$($parkedTarget.ConnectedSystemName) refused the password: $($parkedTarget.Message)" -TargetObject $result
        }

        if ($null -ne $result.PSObject.Properties['Settled'] -and -not $result.Settled -and -not ($PSBoundParameters.ContainsKey('Wait') -and $Wait -eq 0)) {
            Write-Warning "The Connected System had not answered within the wait. Delivery continues; follow Activity $($result.ActivityId) or the person's Password tab."
        }
    }
}
