# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMMetaverseObjectPassword {
    <#
    .SYNOPSIS
        Sets a person's password, on the accounts you name or on every Connected System configured for Password
        Synchronisation.

    .DESCRIPTION
        The one command for giving somebody a password, aimed one of two ways:

        Named accounts. Pass -ConnectedSystemId to set the password on this person's accounts in those Connected
        Systems. This is the reset case: you chose the password for them. By default it expires at next sign-in,
        and the command waits up to ten seconds to tell you what each account did with it. An account in a
        system whose Password Synchronisation is switched off is still delivered to; you named it. -EnableAccount
        is available in this mode only.

        Every configured system. Omit -ConnectedSystemId and the password goes to every Connected System
        configured for Password Synchronisation in which the person has an account, including systems that are
        switched off (held until switched on) and systems where the account does not exist yet (delivered once it
        does). This is the event case: the person's password changed somewhere, and the rest should hold it. By
        default expiry is left to each system's own policy, since a password the person chose should not demand
        they choose another, and the command returns as soon as the change is recorded. To set a password on
        every account the person has regardless of configuration, name the systems:
        -ConnectedSystemId (Get-JIMMetaverseObject -Id $id).ConnectedSystemObjects.ConnectedSystemId

        Either way JIM queues one change per Connected System, encrypted, and the Password Delivery Service makes
        the first attempt within about a second, whatever the synchronisation engine is doing. A system that is
        unavailable delays the password rather than losing it: JIM retries on its own clock, and a system that
        refuses the password parks it for you to look at rather than retrying into the same refusal. The password
        is held only until it is delivered; a copy a system refused is kept, still encrypted, so JIM can finish
        the job once the cause is dealt with, until the change expires or retention removes it. It is never
        logged, never returned, and never recorded on an Activity.

        Supply the password with -Password, or use -Generate to have JIM produce one that satisfies the
        discovered policy of every Connected System the person has an account in. A generated password is
        returned once, on the result's GeneratedPassword property; JIM holds it only until it is delivered and
        cannot give it to you again.

        Each target's State says where its password got to: Set, Retrying (with NextAttemptAt), Parked (with the
        system's own Message), Held behind a switched-off system, or Queued and Delivering while still in flight.
        A Parked target is reported as a non-terminating error as well, so a script that stops on errors stops
        on a refusal; the result is written to the pipeline first either way.

    .PARAMETER Id
        The unique identifier (GUID) of the Metaverse Object whose password this is.

    .PARAMETER ConnectedSystemId
        The Connected Systems to set the password in. The person must have an account in every one named; the
        command refuses, and sets nothing, where they do not. Omit it to propagate the password to every Connected
        System configured for Password Synchronisation instead.

    .PARAMETER Password
        The password, as a SecureString. Encrypted before JIM stores it and held only until delivered; never
        logged, never returned, and never recorded on an Activity.

    .PARAMETER Generate
        Has JIM generate one password that satisfies every Connected System the person has an account in, instead
        of you supplying one. With -ConnectedSystemId, only the systems named count. JIM reconciles their
        discovered policies (the longest minimum length any of them demands, and only the character categories
        all of them count) and refuses outright where no single password can satisfy them all, rather than
        handing back one accepted by the first system and refused by the second. A system JIM could read no
        policy from is reported as a warning, because the password is about to go there and JIM cannot promise
        it will be accepted.

        The generated password is returned on the result's GeneratedPassword property as a SecureString. That is
        the only chance to capture it.

    .PARAMETER ExpiryBehaviour
        What happens to the password once each target has it.
        Valid values: RequireChangeAtNextSignIn, ExpiresAccordingToTargetPolicy, NeverExpires.
        Defaults to RequireChangeAtNextSignIn with -ConnectedSystemId (somebody else chose this password) and to
        ExpiresAccordingToTargetPolicy without it (the person chose it, and should not be made to choose another).

    .PARAMETER EnableAccount
        Enables the named accounts as part of setting the password. Omit it to leave their enabled state
        untouched. Only available with -ConnectedSystemId: a password propagated to every configured system never
        enables an account, because it reaches accounts an administrator may have disabled on purpose.

    .PARAMETER Wait
        How many seconds, from 0 to 30, to wait for the systems to answer before returning. Defaults to 10 with
        -ConnectedSystemId and to 0 without it. A wait ends early once every target has settled; a target still
        Queued or Delivering when it runs out is reported as such, with Settled false, and its outcome appears on
        the Activity and the person's Password tab once it lands. The ceiling is the API's; a script that needs to
        watch for longer should poll Get-JIMPendingPasswordChange -MetaverseObjectId instead.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        One PSCustomObject describing the change:

        | Property           | Description                                                                          |
        |--------------------|--------------------------------------------------------------------------------------|
        | ActivityId         | The Activity recording the change; its children hold each system's outcome           |
        | Origin             | Explicit (accounts named) or Propagated (every configured system)                    |
        | Settled            | Whether every target had reached an outcome you need not wait on when this returned  |
        | QueuedForNoSystems | True when a propagated change found no configured system; nothing was queued         |
        | Targets            | One entry per Connected System, described below                                      |
        | GeneratedPassword  | The password JIM produced, as a SecureString; present only with -Generate            |

        Each entry under Targets:

        | Property                | Description                                                                     |
        |-------------------------|---------------------------------------------------------------------------------|
        | ConnectedSystemId       | The Connected System                                                            |
        | ConnectedSystemName     | Its name                                                                        |
        | ConnectedSystemObjectId | The account, or null where the person has no account in this system yet         |
        | Enabled                 | Whether the system is taking propagated passwords; false means Held             |
        | State                   | Queued, Delivering, Set, Retrying, Parked, Held, Expired or Cancelled           |
        | NextAttemptAt           | When the next attempt falls due, for a Retrying target                          |
        | Message                 | The system's own words on its most recent outcome                               |
        | AttemptCount            | How many delivery attempts this system has had                                  |
        | FailureReason           | Why the last attempt failed; empty before an attempt or once set                |

        No property carries the password you supplied.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        $result = Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -ConnectedSystemId 1,2 -Password $password
        $result.Targets | Select-Object ConnectedSystemName, State, Message

        Sets one password on this person's accounts in Connected Systems 1 and 2, requiring a change at the next
        sign-in, waits up to ten seconds, and shows what each system did with it.

    .EXAMPLE
        $result = Set-JIMMetaverseObjectPassword -Id $id -ConnectedSystemId 3 -Generate -EnableAccount -Force
        ConvertFrom-SecureString -SecureString $result.GeneratedPassword -AsPlainText

        Has JIM produce a password the named system will accept, sets it, enables the account, and reads back
        what was used. Capture it: this is the only chance to.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -Password $password -Force

        Records that the person's password changed and queues it for every Connected System configured for
        Password Synchronisation, returning as soon as it is recorded.

    .EXAMPLE
        $result = Set-JIMMetaverseObjectPassword -Id $id -Password $password -Wait 10 -Force
        if (-not $result.Settled) { Write-Warning "Not every system had answered after 10 seconds; check the person's Password tab." }

        Propagates the password and stays on the line for up to ten seconds to be told which systems took it.

    .EXAMPLE
        $systems = (Get-JIMMetaverseObject -Id $id).ConnectedSystemObjects.ConnectedSystemId
        Set-JIMMetaverseObjectPassword -Id $id -ConnectedSystemId $systems -Generate -Force

        Sets one generated password on every account the person has, whatever each system's Password
        Synchronisation configuration says.

    .LINK
        Set-JIMConnectedSystemObjectPassword
        Get-JIMPendingPasswordChange
        Get-JIMMetaverseObject
    #>
    <#
        Four parameter sets, because two independent choices are made here and the binder enforces what goes
        with what: where the password is aimed (named systems, or every configured system) and where it comes
        from (supplied, or generated by JIM). -EnableAccount lives only in the Named sets so the binder, rather
        than a runtime check, refuses it for a propagated change. The default set is the propagate case with a
        supplied password (decision D5): the event case, which needs no account selection.
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'PropagateSuppliedPassword')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'NamedSuppliedPassword')]
        [Parameter(Mandatory, ParameterSetName = 'NamedGeneratedPassword')]
        [ValidateNotNullOrEmpty()]
        [int[]]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'NamedSuppliedPassword')]
        [Parameter(Mandatory, ParameterSetName = 'PropagateSuppliedPassword')]
        [ValidateNotNull()]
        [securestring]$Password,

        [Parameter(Mandatory, ParameterSetName = 'NamedGeneratedPassword')]
        [Parameter(Mandatory, ParameterSetName = 'PropagateGeneratedPassword')]
        [switch]$Generate,

        [Parameter()]
        [ValidateSet('RequireChangeAtNextSignIn', 'ExpiresAccordingToTargetPolicy', 'NeverExpires')]
        [string]$ExpiryBehaviour,

        [Parameter(ParameterSetName = 'NamedSuppliedPassword')]
        [Parameter(ParameterSetName = 'NamedGeneratedPassword')]
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

        $namedAccounts = $PSBoundParameters.ContainsKey('ConnectedSystemId')

        # The person's accounts are needed to resolve named systems to the accounts the API takes, and to know
        # which systems a generated password has to satisfy. A propagated change with a supplied password needs
        # neither, and the server resolves its own targets.
        $accounts = @()
        if ($namedAccounts -or $Generate) {
            try {
                $metaverseObject = Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id" -Method 'GET'
            }
            catch {
                Write-Error "Failed to read the Metaverse Object's accounts: $_"
                return
            }

            $accounts = @($metaverseObject.ConnectedSystemObjects)
        }

        $connectedSystemObjectIds = $null
        if ($namedAccounts) {
            $systemIds = @($ConnectedSystemId | Select-Object -Unique)
            $accounts = @($accounts | Where-Object { $systemIds -contains $_.ConnectedSystemId })

            # Refused rather than quietly narrowed. A caller who named three systems and had the password set in
            # two would believe all three took it.
            $missing = @($systemIds | Where-Object { $_ -notin @($accounts | ForEach-Object { $_.ConnectedSystemId }) })
            if ($missing.Count -gt 0) {
                Write-Error "This Metaverse Object has no account in Connected System $($missing -join ', '). Nothing was set."
                return
            }

            $connectedSystemObjectIds = @($accounts | ForEach-Object { $_.Id })
        }

        if ($Generate) {
            # Generated against every system the password is going to, not one of them: one password has to
            # satisfy the strictest of several systems, and an administrator cannot see those policies to reason
            # about them. JIM reconciles them and refuses outright where no single password can satisfy them
            # all, which is far better than a password accepted by the first system and refused by the second.
            $policySystemIds = @($accounts | ForEach-Object { $_.ConnectedSystemId } | Select-Object -Unique)
            if ($policySystemIds.Count -eq 0) {
                Write-Error "This Metaverse Object has no accounts, so there is no Connected System policy to generate a password against. Supply one with -Password instead."
                return
            }

            try {
                $generated = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/generate-password" -Method 'POST' -Body @{ connectedSystemIds = $policySystemIds }
            }
            catch {
                Write-Error "Failed to generate a password for these accounts: $_"
                return
            }

            $plainPassword = $generated.password
            $generatedPassword = ConvertTo-SecureString -String $plainPassword -AsPlainText -Force

            foreach ($systemName in $generated.systemsWithNoDiscoveredPolicy) {
                Write-Warning "JIM could read no password policy from $systemName, so it cannot promise this password will be accepted there."
            }
        }
        else {
            # A SecureString on the parameter so a password never sits in the session's command history in clear
            # text. It is unwrapped once here to be sent; the wire format is JSON over TLS.
            $plainPassword = ConvertFrom-SecureString -SecureString $Password -AsPlainText
        }

        if ([string]::IsNullOrWhiteSpace($plainPassword)) {
            Write-Error "A password is required."
            return
        }

        $body = @{ password = $plainPassword }
        if ($namedAccounts) { $body.connectedSystemObjectIds = $connectedSystemObjectIds }
        if ($PSBoundParameters.ContainsKey('ExpiryBehaviour')) { $body.expiryBehaviour = $ExpiryBehaviour }
        # Only sent when asked for. Omitting it means "leave the accounts' enabled state alone"; sending false
        # would ask each Connected System to disable an account nobody asked it to touch.
        if ($EnableAccount) { $body.enableAccount = $true }
        # Sent only when asked for. The server's defaults per mode are the contract for a request that names no
        # wait, and an explicit value would pin them into every script written against this version.
        if ($PSBoundParameters.ContainsKey('Wait')) { $body.wait = $Wait }

        if ($namedAccounts) {
            $target = "$($accounts.Count) account(s) of Metaverse Object $Id in Connected System $($systemIds -join ', ')"
            $action = "Set the password on these accounts"
        }
        else {
            $target = "Metaverse Object $Id"
            $action = "Set the password on every Connected System configured for Password Synchronisation"
        }

        if (-not ($Force -or $PSCmdlet.ShouldProcess($target, $action))) {
            return
        }

        Write-Verbose "Setting a password for Metaverse Object $Id$(if ($namedAccounts) { " on $($accounts.Count) named account(s)" } else { " on every configured Connected System" })"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id/password" -Method 'POST' -Body $body
        }
        catch {
            Write-Error "Failed to set the password: $_"
            return
        }

        # A generated password is carried on the result: the caller never had it, and it is not recoverable once
        # this call returns. Withholding it would set a password nobody can use.
        if ($Generate) {
            $result | Add-Member -NotePropertyName 'GeneratedPassword' -NotePropertyValue $generatedPassword -Force
        }

        # The result first; then a refusal per target, in the system's own words, because a parked password is
        # something the caller has to act on. The result rides on each error's TargetObject, so a script that
        # stops on errors can still read the other targets from the exception it caught.
        $result

        foreach ($parkedTarget in @($result.Targets | Where-Object { $_.State -eq 'Parked' })) {
            Write-Error -Message "$($parkedTarget.ConnectedSystemName) refused the password: $($parkedTarget.Message)" -TargetObject $result
        }

        if ($null -ne $result.PSObject.Properties['Settled'] -and -not $result.Settled -and $PSBoundParameters.ContainsKey('Wait') -and $Wait -gt 0) {
            Write-Warning "Not every Connected System had answered within $Wait second(s). Delivery continues; follow Activity $($result.ActivityId) or the person's Password tab."
        }
    }
}
