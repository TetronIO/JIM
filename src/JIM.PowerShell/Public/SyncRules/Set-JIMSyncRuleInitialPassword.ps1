# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMSyncRuleInitialPassword {
    <#
    .SYNOPSIS
        Changes a Synchronisation Rule's initial password settings.

    .DESCRIPTION
        A newly provisioned Connected System Object has no password and cannot be signed in to. Turning this on has
        JIM set one through the Connected System's password channel, straight after it is created. By default JIM
        generates a different password for each, at the moment it is set, and stores none of them.

        -Source Static is the exception: it sets one password you choose on every Connected System Object the rule
        provisions, so you can tell a new starter what it is. It is not recommended: they all share that password
        until each person changes it, so anybody who learns it can sign in as any new starter who has not. The
        password is stored encrypted, cannot be shown again on any surface, and is the only password JIM stores.

        Only the parameters you give change; everything else keeps its stored value. The exception is the generator
        settings, which are replaced as a set: giving any of -Style, -Length, -Minimum*, -Word*, -Appended*,
        -PermittedSymbols or -ExcludeAmbiguousCharacters sends the whole policy, because those settings only make
        sense together. Read the current values with Get-JIMSyncRuleInitialPassword first when changing one of them
        on its own.

        Turning this on needs an export Synchronisation Rule that provisions to the Connected System: only a
        Connected System Object JIM has just created has never had a password. Settings that cannot produce a
        password are refused here, rather than parking every Connected System Object they touch.

    .PARAMETER Id
        The ID of the Synchronisation Rule.

    .PARAMETER InputObject
        A Synchronisation Rule from Get-JIMSyncRule, through the pipeline.

    .PARAMETER Enable
        Sets an initial password on the accounts this Synchronisation Rule provisions.

    .PARAMETER Disable
        Stops setting an initial password. The settings are kept, so re-enabling restores them.

    .PARAMETER Source
        Where the password comes from. Discovered generates one per Connected System Object from the password policy
        JIM discovered on the Connected System, re-deriving the settings whenever the policy is read again. Custom
        generates one per Connected System Object from the settings saved on this rule. Static sets the one password
        given with -StaticPassword on every Connected System Object; it is not recommended.

    .PARAMETER StaticPassword
        The one password to set on every Connected System Object this Synchronisation Rule provisions, used when
        -Source is Static. Give it as a SecureString, so it does not sit in the session's command history in clear
        text. Write-only: JIM encrypts it and never returns it. Omit it to leave the stored password as it is, which
        is what makes changing another setting safe. A password the Connected System would refuse is rejected here,
        rather than parking every Connected System Object the rule provisions.

    .PARAMETER Style
        How the password is composed.

    .PARAMETER Length
        How many characters to produce. Ignored by the Words style, whose length follows from the words drawn.

    .PARAMETER MinimumUppercase
        The fewest uppercase letters a generated password must contain (RandomCharacters style).

    .PARAMETER MinimumLowercase
        The fewest lowercase letters a generated password must contain (RandomCharacters style).

    .PARAMETER MinimumDigits
        The fewest digits a generated password must contain (RandomCharacters style).

    .PARAMETER MinimumSymbols
        The fewest symbols a generated password must contain (RandomCharacters style).

    .PARAMETER PermittedSymbols
        The symbols JIM may use. Narrow this where something downstream cannot cope with a given character.

    .PARAMETER WordCount
        How many words to draw (Words style).

    .PARAMETER WordSeparator
        What goes between the words (Words style).

    .PARAMETER WordCapitalisation
        How the words are capitalised (Words style).

    .PARAMETER AppendedDigitCount
        How many digits to append (Words and Pronounceable styles). Usually how a passphrase reaches the three
        character categories a stock Active Directory domain requires.

    .PARAMETER AppendSymbol
        Whether to append one symbol (Words and Pronounceable styles).

    .PARAMETER ExcludeAmbiguousCharacters
        Whether to leave out characters that are easily confused when a password is read out or copied by hand.

    .PARAMETER ExpiryBehaviour
        What happens to the password once it is set. A Connector that cannot honour the choice records what it
        applied instead, for each Connected System Object. On Active Directory and Samba AD, Password never expires
        follows this only where no export has written the account's userAccountControl by the time the password is
        delivered; where one has, the Attribute Flow decides it. Requiring a change at the next sign-in always applies.

    .PARAMETER EnableAccount
        Whether the Connected System Object is enabled once the password is set. It applies only where no export has
        written the object's enabled state (such as userAccountControl) by the time the password is delivered; where
        one has, the Attribute Flow decides. $false leaves it as it was created.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Synchronisation Rule's change history.

    .PARAMETER PassThru
        Returns the settings as changed.

    .OUTPUTS
        With -PassThru, the settings as changed; otherwise nothing.

    .EXAMPLE
        Set-JIMSyncRuleInitialPassword -Id 5 -Enable

        Sets an initial password on the accounts Synchronisation Rule 5 provisions, following the password
        policy JIM discovered on the Connected System.

    .EXAMPLE
        Set-JIMSyncRuleInitialPassword -Id 5 -Enable -Source Custom -Style Words -WordCount 4 -WordSeparator Hyphen -AppendedDigitCount 2

        Uses a four-word passphrase with two appended digits, which is easier to read out to somebody over the
        telephone than a random string.

    .EXAMPLE
        $password = Read-Host -AsSecureString "Initial password for every new account"
        Set-JIMSyncRuleInitialPassword -Id 5 -Enable -Source Static -StaticPassword $password

        Sets one password on every account this rule provisions. Not recommended: every new starter shares it
        until each of them changes it. Leave -ExpiryBehaviour at its default so that first sign-in ends each
        account's share of it.

    .EXAMPLE
        $password = Read-Host -AsSecureString "New shared initial password"
        Set-JIMSyncRuleInitialPassword -Id 5 -StaticPassword $password -ChangeReason "Rotated after a leaver (CHG0043)"

        Rotates the shared password, leaving every other setting alone. Do this whenever somebody who knew it
        leaves; Get-JIMSyncRuleInitialPassword reports when it last changed.

    .EXAMPLE
        Set-JIMSyncRuleInitialPassword -Id 5 -Disable -ChangeReason "Accounts are now provisioned pre-enabled (CHG0042)"

        Stops setting an initial password, recording why against the rule's change history.

    .EXAMPLE
        Get-JIMSyncRule -Id 5 | Set-JIMSyncRuleInitialPassword -ExpiryBehaviour NeverExpires -PassThru

        Changes only the expiry behaviour, leaving every other setting as it was.

    .LINK
        Get-JIMSyncRuleInitialPassword
        Set-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.SyncRuleInitialPasswordResponse')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [switch]$Enable,

        [Parameter()]
        [switch]$Disable,

        [Parameter()]
        [ValidateSet('Discovered', 'Custom', 'Static')]
        [string]$Source,

        [Parameter()]
        [ValidateNotNull()]
        [securestring]$StaticPassword,

        [Parameter()]
        [ValidateSet('RandomCharacters', 'Words', 'Pronounceable')]
        [string]$Style,

        [Parameter()]
        [ValidateRange(1, 256)]
        [int]$Length,

        [Parameter()]
        [ValidateRange(0, 64)]
        [int]$MinimumUppercase,

        [Parameter()]
        [ValidateRange(0, 64)]
        [int]$MinimumLowercase,

        [Parameter()]
        [ValidateRange(0, 64)]
        [int]$MinimumDigits,

        [Parameter()]
        [ValidateRange(0, 64)]
        [int]$MinimumSymbols,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$PermittedSymbols,

        [Parameter()]
        [ValidateRange(1, 16)]
        [int]$WordCount,

        [Parameter()]
        [ValidateSet('None', 'Hyphen', 'FullStop', 'Underscore', 'Digit', 'RandomSymbol')]
        [string]$WordSeparator,

        [Parameter()]
        [ValidateSet('Lowercase', 'EachWord', 'Uppercase', 'FirstWordOnly', 'RandomWord')]
        [string]$WordCapitalisation,

        [Parameter()]
        [ValidateRange(0, 16)]
        [int]$AppendedDigitCount,

        [Parameter()]
        [bool]$AppendSymbol,

        [Parameter()]
        [bool]$ExcludeAmbiguousCharacters,

        [Parameter()]
        [ValidateSet('RequireChangeAtNextSignIn', 'ExpiresAccordingToTargetPolicy', 'NeverExpires')]
        [string]$ExpiryBehaviour,

        [Parameter()]
        [bool]$EnableAccount,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($Enable -and $Disable) {
            Write-Error "Specify either -Enable or -Disable, not both."
            return
        }

        $ruleId = if ($InputObject) { $InputObject.id } else { $Id }

        $body = @{}

        if ($Enable) {
            $body.enabled = $true
        }
        elseif ($Disable) {
            $body.enabled = $false
        }

        if ($PSBoundParameters.ContainsKey('Source')) {
            $body.source = $Source
        }

        if ($PSBoundParameters.ContainsKey('StaticPassword')) {
            # Unwrapped once, here, and sent over TLS for the API to encrypt. A SecureString on the parameter is
            # what keeps it out of the session's command history and any transcript of it.
            $body.staticPassword = ConvertFrom-SecureString -SecureString $StaticPassword -AsPlainText
        }

        if ($PSBoundParameters.ContainsKey('ExpiryBehaviour')) {
            $body.expiryBehaviour = $ExpiryBehaviour
        }

        if ($PSBoundParameters.ContainsKey('EnableAccount')) {
            $body.enableAccount = $EnableAccount
        }

        # The generator settings travel as one object because they only make sense together, so touching any of
        # them means reading the stored set and sending it back with the changes applied. Sending a partial
        # policy would silently reset the fields left out to the API's defaults.
        $policyParameters = @(
            'Style', 'Length', 'MinimumUppercase', 'MinimumLowercase', 'MinimumDigits', 'MinimumSymbols',
            'PermittedSymbols', 'WordCount', 'WordSeparator', 'WordCapitalisation', 'AppendedDigitCount',
            'AppendSymbol', 'ExcludeAmbiguousCharacters')
        $changedPolicyParameters = @($policyParameters | Where-Object { $PSBoundParameters.ContainsKey($_) })

        if ($changedPolicyParameters.Count -gt 0) {
            try {
                $current = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId/initial-password" -Method 'GET'
            }
            catch {
                Write-Error "Failed to read the current initial password settings before updating them: $_"
                return
            }

            # The stored names come back normalised to PascalCase by ConvertTo-JIMOutputObject, and the
            # parameter names are PascalCase too, so a changed setting overwrites the copied one rather than
            # sitting beside it under a differently-cased key. JSON binding is case-insensitive at the far end,
            # but a body carrying both spellings of the same setting is a trap for whoever reads it next.
            $policy = @{}
            foreach ($property in $current.CustomPolicy.PSObject.Properties) {
                $policy[$property.Name] = $property.Value
            }

            foreach ($parameter in $changedPolicyParameters) {
                $policy[$parameter] = $PSBoundParameters[$parameter]
            }

            $body.customPolicy = $policy
        }

        # A change reason alone is not an update; require at least one actual change first.
        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSBoundParameters.ContainsKey('ChangeReason')) {
            $body.changeReason = $ChangeReason
        }

        if ($PSCmdlet.ShouldProcess($ruleId, "Update Synchronisation Rule initial password configuration")) {
            Write-Verbose "Updating the initial password configuration of Synchronisation Rule: $ruleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId/initial-password" -Method 'PUT' -Body $body

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update the initial password configuration: $_"
            }
        }
    }
}
