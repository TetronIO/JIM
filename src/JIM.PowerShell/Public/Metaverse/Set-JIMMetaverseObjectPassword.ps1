# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMMetaverseObjectPassword {
    <#
    .SYNOPSIS
        Sets the same password on several of a person's accounts across Connected Systems.

    .DESCRIPTION
        Resolves the accounts a Metaverse Object is joined to and sets one password on the ones you name,
        writing to each Connected System in turn. This is the automation counterpart of the Set Password action
        on a Metaverse Object in the administration portal.

        There is no transaction across Connected Systems. Each write is independent, and a run routinely ends
        with some accounts changed and others not, so every account's outcome is reported separately. Where one
        refuses, the person is left with a different password there from the accounts that accepted it.

        You supply the password. JIM does not generate one here, because that would mean returning a password in
        a response body, which this API never does. Use the portal when you want JIM to generate one that
        satisfies every selected system's discovered policy at once.

        You must name the Connected Systems, or pass -AllAccounts. Setting a password everywhere by default
        would turn a reset in one system into a reset in all of them.

    .PARAMETER Id
        The unique identifier (GUID) of the Metaverse Object.

    .PARAMETER ConnectedSystemId
        The Connected Systems to set the password in. Accounts in other systems are left alone.

    .PARAMETER AllAccounts
        Sets the password on every account this person has, in every Connected System JIM can set passwords in.

    .PARAMETER Password
        The password to set, as a SecureString. Sent to each Connected System and nowhere else: never logged,
        never persisted by JIM, and never echoed back.

    .PARAMETER ExpiryBehaviour
        What happens to the password once it is set, applied to every account.
        Valid values: RequireChangeAtNextSignIn, ExpiresAccordingToTargetPolicy, NeverExpires.
        Defaults to RequireChangeAtNextSignIn, which is the right default for a password somebody else chose.

    .PARAMETER EnableAccount
        Enables the accounts as part of setting the password. Omit it to leave their enabled state untouched.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        One object per account attempted:

        | Property               | Description                                                       |
        |------------------------|-------------------------------------------------------------------|
        | ConnectedSystemId      | The Connected System the account is in                            |
        | ConnectedSystemName    | Its name                                                          |
        | ConnectedSystemObjectId| The account                                                       |
        | Success                | Whether the password was set                                      |
        | AppliedExpiryBehaviour | The expiry behaviour really applied, where it was set             |
        | ExpiryBehaviourWarning | Why the requested behaviour could not be honoured, or null        |
        | Message                | The Connected System's own reason, where it refused               |

        No property carries the password.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -ConnectedSystemId 1,2 -Password $password

        Sets one password on this person's accounts in Connected Systems 1 and 2, requiring a change at the next
        sign-in, and prompts for confirmation first.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New password"
        Set-JIMMetaverseObjectPassword -Id 8f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f -AllAccounts -Password $password -Force |
            Where-Object { -not $_.Success }

        Sets the password everywhere without prompting, and lists only the accounts that refused it.

    .LINK
        Set-JIMConnectedSystemObjectPassword
        Get-JIMMetaverseObject
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'BySystem')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'BySystem')]
        [int[]]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'AllAccounts')]
        [switch]$AllAccounts,

        [Parameter(Mandatory)]
        [ValidateNotNull()]
        [securestring]$Password,

        [Parameter()]
        [ValidateSet('RequireChangeAtNextSignIn', 'ExpiresAccordingToTargetPolicy', 'NeverExpires')]
        [string]$ExpiryBehaviour,

        [Parameter()]
        [switch]$EnableAccount,

        [Parameter()]
        [switch]$Force
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # A SecureString on the parameter so a password never sits in the session's command history in clear
        # text. It is unwrapped once here and used for the whole run; the wire format is JSON over TLS.
        $plainPassword = ConvertFrom-SecureString -SecureString $Password -AsPlainText
        if ([string]::IsNullOrWhiteSpace($plainPassword)) {
            Write-Error "A password is required."
            return
        }

        try {
            $metaverseObject = Invoke-JIMApi -Endpoint "/api/v1/metaverse/objects/$Id" -Method 'GET'
        }
        catch {
            Write-Error "Failed to read the Metaverse Object's accounts: $_"
            return
        }

        $accounts = @($metaverseObject.ConnectedSystemObjects)
        if ($PSCmdlet.ParameterSetName -eq 'BySystem') {
            $accounts = @($accounts | Where-Object { $ConnectedSystemId -contains $_.ConnectedSystemId })
        }

        if ($accounts.Count -eq 0) {
            Write-Warning "This Metaverse Object has no accounts in the Connected Systems given."
            return
        }

        $body = @{ password = $plainPassword }
        if ($PSBoundParameters.ContainsKey('ExpiryBehaviour')) { $body.expiryBehaviour = $ExpiryBehaviour }
        # Only sent when asked for. Omitting it means "leave the accounts' enabled state alone"; sending false
        # would ask each Connected System to disable an account nobody asked it to touch.
        if ($EnableAccount) { $body.enableAccount = $true }

        $target = "$($accounts.Count) account(s) of Metaverse Object $Id"
        if (-not ($Force -or $PSCmdlet.ShouldProcess($target, "Set the same password on these accounts"))) {
            return
        }

        # One Connected System at a time. A refusal from one says nothing about the others, so the loop
        # continues and every account gets its own reported outcome.
        foreach ($account in $accounts) {
            Write-Verbose "Setting the password on $($account.Id) in $($account.ConnectedSystemName)"

            $outcome = [ordered]@{
                ConnectedSystemId       = $account.ConnectedSystemId
                ConnectedSystemName     = $account.ConnectedSystemName
                ConnectedSystemObjectId = $account.Id
                Success                 = $false
                AppliedExpiryBehaviour  = $null
                ExpiryBehaviourWarning  = $null
                Message                 = $null
            }

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$($account.ConnectedSystemId)/connector-space/$($account.Id)/password" -Method 'POST' -Body $body
                $outcome.Success = $true
                $outcome.AppliedExpiryBehaviour = $result.AppliedExpiryBehaviour
                $outcome.ExpiryBehaviourWarning = $result.ExpiryBehaviourWarning
            }
            catch {
                # The Connected System's own words, kept verbatim: why a directory refuses a password is a
                # property of that directory's policy and is the most useful thing to hand back.
                $outcome.Message = $_.Exception.Message
                Write-Error "Failed to set the password in $($account.ConnectedSystemName): $_" -ErrorAction Continue
            }

            [PSCustomObject]$outcome
        }
    }
}
