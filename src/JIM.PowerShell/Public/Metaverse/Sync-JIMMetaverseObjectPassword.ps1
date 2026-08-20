# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Sync-JIMMetaverseObjectPassword {
    <#
    .SYNOPSIS
        Synchronises a password change for a person to every Connected System configured to receive one.

    .DESCRIPTION
        Tells JIM that this person's password has changed. JIM records one queued change per Connected System
        that is enabled for Password Synchronisation and in which they have an account, then delivers them on
        its own clock.

        The command returns as soon as the change is recorded, which is the point of it. Nobody waits on a
        directory, and a system being unavailable delays the password rather than losing it: JIM retries with a
        backoff, and stops and asks for help rather than trying forever. A second change for the same person
        replaces an undelivered first one, so only the newest password is ever sent.

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

    .PARAMETER Force
        Skips the confirmation prompt.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Sync-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -Password $password

        Queues the password for every Connected System this person has an account in that takes synchronised
        passwords.

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

        Write-Verbose "Synchronising a password change for Metaverse Object $Id"

        try {
            Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id/password" -Method 'POST' -Body $body
        }
        catch {
            Write-Error "Failed to synchronise the password change: $_"
        }
    }
}
