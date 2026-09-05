# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Sync-JIMMetaverseObjectPassword {
    <#
    .SYNOPSIS
        Synchronises a password change for a person to every Connected System configured to receive one.

    .DESCRIPTION
        Tells JIM that this person's password has changed. JIM records one queued change per Connected System
        that is enabled for Password Synchronisation and in which they have an account, and the Password
        Delivery Service makes the first attempt within about a second of that, whatever the synchronisation
        engine is doing at the time.

        By default the command returns as soon as the change is recorded. Nobody waits on a directory, and a
        system being unavailable delays the password rather than losing it: JIM retries with a backoff, and
        stops and asks for help rather than trying forever. A second change for the same person replaces an
        undelivered first one, so only the newest password is ever sent.

        Pass -Wait to be told what each system did with the password before the command returns. It holds the
        request for up to that many seconds and returns as soon as every target has settled: set, retrying,
        parked, held or expired. Settled says whether all of them had by the time it returned; a target still
        Queued or Delivering when the wait ran out is reported as such, and its outcome is on the Activity and
        the person's Password Synchronisation tab once it lands.

        Which systems receive the password is their own configuration, not a choice made here. A system with
        Password Synchronisation switched off still accumulates the change and receives it when it is switched
        back on; a system that is not configured for it at all is not a target.

        This is not Set-JIMMetaverseObjectPassword. That command sets a password you choose on whichever
        accounts you name, straight away, and tells you whether each target accepted it: the right tool when you
        are choosing the password, in the systems you choose. Use this one when the person has changed their own
        password somewhere and every system should end up holding it.

        The password is encrypted before JIM stores it and is never logged, never returned, and never recorded
        on an Activity.

    .PARAMETER Id
        The unique identifier (GUID) of the Metaverse Object whose password changed.

    .PARAMETER Password
        The new password, as a SecureString.

    .PARAMETER ExpiryBehaviour
        What happens to the password once a target holds it.
        Valid values: RequireChangeAtNextSignIn, ExpiresAccordingToTargetPolicy, NeverExpires.
        Defaults to ExpiresAccordingToTargetPolicy, which is the right default for a password the person chose
        themselves: demanding they choose another one at next sign-in would defeat the point of synchronising
        the one they just set. (Set-JIMMetaverseObjectPassword defaults the other way, because there somebody
        else chose the password.)

    .PARAMETER Wait
        How many seconds, from 0 to 30, to wait for the systems to answer before returning. Defaults to 0, which
        returns as soon as the change is recorded. A wait ends early once every target has settled; the ceiling
        is the API's, and a script needing to watch longer than that should poll Get-JIMPendingPasswordChange
        for the person instead.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        PSCustomObject with ActivityId, Settled (whether every target had reached an outcome a caller need not
        wait on by the time the command returned), QueuedForNoSystems, and one entry per Connected System under
        Targets: ConnectedSystemId, ConnectedSystemName, Enabled, ConnectedSystemObjectId, State (Queued,
        Delivering, Set, Retrying, Parked, Held, Expired or Cancelled), NextAttemptAt (for a target that is
        Retrying), Message (the target's own words on its most recent outcome) and AttemptCount. Without -Wait,
        every target is Queued or Held and Settled is false unless nothing was queued.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Sync-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -Password $password

        Queues the password for every Connected System this person has an account in that takes synchronised
        passwords, and returns as soon as it is recorded.

    .EXAMPLE
        $result = Sync-JIMMetaverseObjectPassword -Id $id -Password $password -Wait 10 -Force
        $result.Targets | Select-Object ConnectedSystemName, State, Message
        if (-not $result.Settled) { Write-Warning "Not every system had answered after 10 seconds; check the person's Password Synchronisation tab." }

        Waits up to ten seconds and reports which systems took the password (State is Set), which are being
        retried or were parked and why, and whether anything was still on its way when the wait ran out. A
        service desk script uses this to tell the caller their reset has landed before they hang up.

    .EXAMPLE
        Sync-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -Password $password -Force |
            Select-Object -ExpandProperty Targets

        Queues without prompting and lists the Connected Systems the change was queued for.

    .EXAMPLE
        $result = Sync-JIMMetaverseObjectPassword -Id $id -Password $password -Force
        if ($result.QueuedForNoSystems) { Write-Warning "No system takes synchronised passwords for this person." }

        Checks the case worth checking: the change was recorded but reached nothing, because no Connected
        System this person has an account in is enabled for Password Synchronisation.

    .LINK
        Set-JIMMetaverseObjectPassword
        Set-JIMConnectedSystemPasswordSynchronisation
        Get-JIMMetaverseObject
    #>
    <#
        One parameter set, unlike Set-JIMMetaverseObjectPassword's four. There is no account selection to make
        (the Connected Systems' own configuration decides that) and no -Generate (a synchronised password comes
        from the person, so JIM producing one would be the opposite of synchronising it).
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id,

        [Parameter(Mandatory)]
        [ValidateNotNull()]
        [securestring]$Password,

        [Parameter()]
        [ValidateSet('RequireChangeAtNextSignIn', 'ExpiresAccordingToTargetPolicy', 'NeverExpires')]
        [string]$ExpiryBehaviour,

        [Parameter()]
        [ValidateRange(0, 30)]
        [int]$Wait = 0,

        [Parameter()]
        [switch]$Force
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $target = "Metaverse Object $Id"
        $action = "Synchronise a password change to every Connected System configured to receive one"
        if (-not $Force -and -not $PSCmdlet.ShouldProcess($target, $action)) {
            return
        }

        # A SecureString keeps the value out of the session history and out of a transcript as readable text. It
        # has to be unwrapped to be sent, since the wire format is JSON over TLS; the plain value is held for
        # the length of one call and nothing else in this function touches it.
        $plainPassword = ConvertFrom-SecureString -SecureString $Password -AsPlainText

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

        # Sent only when asked for. The server's default is the contract for a request that names no wait, and
        # an explicit zero would pin that default into every script written against this version.
        if ($PSBoundParameters.ContainsKey('Wait')) {
            $body.wait = $Wait
        }

        Write-Verbose "Synchronising a password change for Metaverse Object $Id$(if ($Wait -gt 0) { " and waiting up to $Wait second(s) for the outcome" })"

        try {
            Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id/password" -Method 'POST' -Body $body
        }
        catch {
            Write-Error "Failed to synchronise the password change: $_"
        }
    }
}
