# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMSyncRuleInitialPassword {
    <#
    .SYNOPSIS
        Configures the initial password a Synchronisation Rule sets on the accounts it provisions.

    .DESCRIPTION
        A newly provisioned account has no password and cannot be signed in to. Turning this on has JIM
        generate one per account and set it through the Connected System's password channel, straight after the
        account is created. The password is generated at the moment it is set and is never stored by JIM.

        Only the parameters provided are changed; everything else keeps its stored value. The exception is the
        generator settings, which are replaced as a set: supplying any of the -Style, -Length, -Minimum*,
        -Word*, -Appended*, -PermittedSymbols or -ExcludeAmbiguousCharacters parameters sends the whole policy,
        because those settings only make sense together. Read the current values with
        Get-JIMSyncRuleInitialPassword first if you are changing one of them in isolation.

        Turning this on requires an Export Synchronisation Rule that provisions to the Connected System: only
        an account JIM has just created has never had a password. Settings that cannot produce a password are
        refused here rather than parking every account they touch.

    .PARAMETER Id
        The unique identifier of the Synchronisation Rule.

    .PARAMETER InputObject
        Synchronisation Rule object to update (from pipeline).

    .PARAMETER Enable
        Sets an initial password on the accounts this Synchronisation Rule provisions.

    .PARAMETER Disable
        Stops setting an initial password. The settings are kept, so re-enabling restores them.

    .PARAMETER Source
        Where the generator settings come from.
        Valid values:
          - Discovered: derive them from the password policy JIM discovered on the Connected System, and
            re-derive whenever that policy is read again.
          - Custom: use exactly the settings saved on this rule.

    .PARAMETER Style
        How the password is composed. Valid values: RandomCharacters, Words, Pronounceable.

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
        Valid values: None, Hyphen, FullStop, Underscore, Digit, RandomSymbol.

    .PARAMETER WordCapitalisation
        How the words are capitalised (Words style).
        Valid values: Lowercase, EachWord, Uppercase, FirstWordOnly, RandomWord.

    .PARAMETER AppendedDigitCount
        How many digits to append (Words and Pronounceable styles). Usually how a passphrase reaches the three
        character categories a stock Active Directory domain requires.

    .PARAMETER AppendSymbol
        Whether to append one symbol (Words and Pronounceable styles).

    .PARAMETER ExcludeAmbiguousCharacters
        Whether to leave out characters that are easily confused when a password is read out or copied by hand.

    .PARAMETER ExpiryBehaviour
        What happens to the password once it is set.
        Valid values: RequireChangeAtNextSignIn, ExpiresAccordingToTargetPolicy, NeverExpires.
        A Connector that cannot honour the choice records what it applied instead, per account.

    .PARAMETER EnableAccount
        Whether the account is enabled once the password is set. Directories that refuse to enable an account
        without a compliant password need the password first, which is why this belongs here rather than in an
        Attribute Flow.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Synchronisation Rule's change history.

    .PARAMETER PassThru
        If specified, returns the updated initial password configuration.

    .OUTPUTS
        If -PassThru is specified, returns the updated initial password configuration.

    .EXAMPLE
        Set-JIMSyncRuleInitialPassword -Id 5 -Enable

        Sets an initial password on the accounts Synchronisation Rule 5 provisions, following the password
        policy JIM discovered on the Connected System.

    .EXAMPLE
        Set-JIMSyncRuleInitialPassword -Id 5 -Enable -Source Custom -Style Words -WordCount 4 -WordSeparator Hyphen -AppendedDigitCount 2

        Uses a four-word passphrase with two appended digits, which is easier to read out to somebody over the
        telephone than a random string.

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
    [OutputType([PSCustomObject])]
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
        [ValidateSet('Discovered', 'Custom')]
        [string]$Source,

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
