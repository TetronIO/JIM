# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Updates the settings on an existing Synchronisation Rule Mapping (Attribute Flow).

    .DESCRIPTION
        Changes how an existing Attribute Flow behaves, leaving what it reads and writes alone.
        Only the parameters you supply are changed; everything else is left as it is.

        What a mapping targets, and whether its source is an attribute or an Expression, cannot be
        changed here. Those revalidate against attribute types and plurality, and for an import
        mapping they reopen its place in the attribute's priority order, so they remain a
        Remove-JIMSyncRuleMapping followed by a New-JIMSyncRuleMapping.

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule the mapping belongs to.

    .PARAMETER MappingId
        The unique identifier of the Mapping to update.

    .PARAMETER InputObject
        Mapping object to update (from pipeline).

    .PARAMETER Expression
        Replaces the mapping's expression. Expression mappings only.

    .PARAMETER MissingInputBehaviour
        What the expression does when an attribute it reads has no value on the object being
        synchronised. Expression mappings only.
        - EvaluateAnyway: evaluate with the input absent and contribute whatever it returns.
        - ContributeNoValue: do not evaluate; contribute nothing, resolved by Attribute Priority.
        - FailMapping: do not evaluate; record an ExpressionMissingInput error. The object's other
          attributes still flow.
        - FailObject: do not evaluate anything for the object; it is errored and left untouched.

    .PARAMETER NullIsValue
        Whether a contribution of no value from this mapping is authoritative. Import mappings only.

    .PARAMETER InboundValueProcessing
        Text value-processing transforms applied as the value flows to the Metaverse, as a
        comma-separated set of flag names (for example 'TreatWhitespaceAsNoValue, TrimWhitespace').
        Import mappings only.

    .PARAMETER CaseNormalisation
        Case normalisation applied as the value flows to the Metaverse. Import mappings only.

    .PARAMETER InitialExportOnly
        Whether the mapping flows only during the initial provisioning export. Export mappings only.

    .PARAMETER Enabled
        Enables or disables the mapping. A disabled mapping is skipped by synchronisation in both
        directions until it is re-enabled; re-enabling clears any recorded disabled reason. Applies
        to import and export mappings alike.

    .PARAMETER TokenKind
        Changes which uniqueness token a generated mapping appends: OnlyIfTaken, Sequence or Random.
        Generated mappings only. Converting an ordinary mapping to or from a generated one is not
        supported here (delete and create).

    .PARAMETER SuffixStyle
        OnlyIfTaken only: whether the collision suffix is a Number or a Letter.

    .PARAMETER SuffixStart
        OnlyIfTaken only: the first suffix value tried once the bare base value is taken.

    .PARAMETER SequenceStart
        Sequence only: the lowest number this flow will ever issue. Raising it above the target
        attribute's counter moves the counter forward at save time; the response's
        Generation.SequenceSkippedAhead reports the move, and a warning names the old and new
        positions. A lower or equal value has no effect.

    .PARAMETER SequenceIncrement
        Sequence only: how much the counter advances per issued number.

    .PARAMETER FixedWidth
        Sequence only: zero-pads the number to this many digits. Supply 0 to clear the padding;
        omit to leave it unchanged.

    .PARAMETER OnWidthExceeded
        Sequence only: what happens when a number would no longer fit -FixedWidth. StopAndReport
        stops the object with an attributed error; AllowLonger lets the number grow past the width.

    .PARAMETER RandomFormat
        Random only: the shape of the token (Guid, Hex or Digits).

    .PARAMETER RandomLength
        Random only: the length of the token in characters. Required alongside -RandomFormat Hex
        or Digits; must not be supplied for Guid.

    .PARAMETER Separator
        The characters placed between the base value and the token. Supply an empty string ('' or
        $null) to clear it; omit to leave it unchanged.

    .PARAMETER AttemptLimit
        The maximum number of candidates tried, per object per synchronisation run, before
        generation fails hard for that object.

    .PARAMETER NeverReuse
        Whether a value whose assignment is deleted is retired and never issued again by this flow.
        Always treated as true for a Sequence token, whatever is supplied.

    .PARAMETER PassThru
        Returns the updated mapping.

    .OUTPUTS
        None by default. The updated mapping when -PassThru is supplied. A generated mapping's
        Generation property carries its uniqueness token settings; Generation.SequenceSkippedAhead
        is present only when -SequenceStart raised the target attribute's counter on this save.

    .EXAMPLE
        Set-JIMSyncRuleMapping -SyncRuleId 2 -MappingId 15 -MissingInputBehaviour FailObject

        Stops an object with a missing input exporting at all, rather than exporting a
        Distinguished Name built around the gap.

    .EXAMPLE
        Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 8 -Expression 'Lower(cs["mail"])' -PassThru

        Rewrites an import mapping's expression and returns the updated mapping.

    .EXAMPLE
        Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 8 -Enabled $false

        Disables one Attribute Flow without touching the Synchronisation Rule; the mapping is
        skipped by synchronisation until it is re-enabled with -Enabled $true.

    .EXAMPLE
        Get-JIMSyncRuleMapping -SyncRuleId 1 |
            Where-Object { $_.sourceType -eq 'ExpressionMapping' } |
            Set-JIMSyncRuleMapping -SyncRuleId 1 -MissingInputBehaviour FailMapping

        Reports every expression mapping on the Rule that meets an object with a missing input,
        rather than letting it contribute a value built around the gap.

    .EXAMPLE
        Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -SequenceStart 500000

        Raises a generated Sequence mapping's Sequence Start; if the counter was already below
        500000 this moves it forward and warns naming the old and new positions.

    .EXAMPLE
        Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -FixedWidth 0

        Clears a generated Sequence mapping's fixed width, so numbers are no longer zero-padded.

    .LINK
        Get-JIMSyncRuleMapping
        New-JIMSyncRuleMapping
        Remove-JIMSyncRuleMapping
        Get-JIMGeneratedValueSequence
        Restart-JIMGeneratedValues
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$MappingId,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [string]$Expression,

        [ValidateSet('EvaluateAnyway', 'ContributeNoValue', 'FailMapping', 'FailObject')]
        [string]$MissingInputBehaviour,

        [bool]$NullIsValue,

        [string]$InboundValueProcessing,

        [ValidateSet('None', 'Upper', 'Lower', 'Title')]
        [string]$CaseNormalisation,

        [bool]$InitialExportOnly,

        [bool]$Enabled,

        # Unique Value Generation (#242): a generated mapping's uniqueness token settings. Refused by the
        # server for a mapping that is not currently a generated mapping.
        [ValidateSet('OnlyIfTaken', 'Sequence', 'Random')]
        [string]$TokenKind,

        [ValidateSet('Number', 'Letter')]
        [string]$SuffixStyle,

        [int]$SuffixStart,

        [long]$SequenceStart,

        [int]$SequenceIncrement,

        [int]$FixedWidth,

        [ValidateSet('StopAndReport', 'AllowLonger')]
        [string]$OnWidthExceeded,

        [ValidateSet('Guid', 'Hex', 'Digits')]
        [string]$RandomFormat,

        [int]$RandomLength,

        [string]$Separator,

        [int]$AttemptLimit,

        [bool]$NeverReuse,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $mapId = if ($InputObject) { $InputObject.id } else { $MappingId }

        # Only send what the caller named. An omitted setting must reach the API as absent rather than
        # as a default, or every call would silently rewrite settings it was never asked about.
        $body = @{}
        if ($PSBoundParameters.ContainsKey('Expression')) { $body.expression = $Expression }
        if ($PSBoundParameters.ContainsKey('MissingInputBehaviour')) { $body.missingInputBehaviour = $MissingInputBehaviour }
        if ($PSBoundParameters.ContainsKey('NullIsValue')) { $body.nullIsValue = $NullIsValue }
        if ($PSBoundParameters.ContainsKey('InboundValueProcessing')) { $body.inboundValueProcessing = $InboundValueProcessing }
        if ($PSBoundParameters.ContainsKey('CaseNormalisation')) { $body.caseNormalisation = $CaseNormalisation }
        if ($PSBoundParameters.ContainsKey('InitialExportOnly')) { $body.initialExportOnly = $InitialExportOnly }
        if ($PSBoundParameters.ContainsKey('Enabled')) { $body.enabled = $Enabled }

        # Unique Value Generation (#242): only settings the caller named are sent, nested under `generation`
        # (matching UpdateSyncRuleMappingGenerationRequest). -FixedWidth 0 clears the padding; omitting it
        # leaves it unchanged.
        $generation = @{}
        if ($PSBoundParameters.ContainsKey('TokenKind')) { $generation.tokenKind = $TokenKind }
        if ($PSBoundParameters.ContainsKey('SuffixStyle')) { $generation.suffixStyle = $SuffixStyle }
        if ($PSBoundParameters.ContainsKey('SuffixStart')) { $generation.suffixStart = $SuffixStart }
        if ($PSBoundParameters.ContainsKey('SequenceStart')) { $generation.sequenceStart = $SequenceStart }
        if ($PSBoundParameters.ContainsKey('SequenceIncrement')) { $generation.sequenceIncrement = $SequenceIncrement }
        if ($PSBoundParameters.ContainsKey('FixedWidth')) { $generation.fixedWidth = $FixedWidth }
        if ($PSBoundParameters.ContainsKey('OnWidthExceeded')) { $generation.onWidthExceeded = $OnWidthExceeded }
        if ($PSBoundParameters.ContainsKey('RandomFormat')) { $generation.randomFormat = $RandomFormat }
        if ($PSBoundParameters.ContainsKey('RandomLength')) { $generation.randomLength = $RandomLength }
        if ($PSBoundParameters.ContainsKey('Separator')) { $generation.separator = $Separator }
        if ($PSBoundParameters.ContainsKey('AttemptLimit')) { $generation.attemptLimit = $AttemptLimit }
        if ($PSBoundParameters.ContainsKey('NeverReuse')) { $generation.neverReuse = $NeverReuse }
        if ($generation.Count -gt 0) { $body.generation = $generation }

        if ($body.Count -eq 0) {
            Write-Error "No settings were supplied to change. Supply at least one of -Expression, -MissingInputBehaviour, -NullIsValue, -InboundValueProcessing, -CaseNormalisation, -InitialExportOnly, -Enabled, or a generated mapping's uniqueness token settings (-TokenKind, -SuffixStyle, -SuffixStart, -SequenceStart, -SequenceIncrement, -FixedWidth, -OnWidthExceeded, -RandomFormat, -RandomLength, -Separator, -AttemptLimit, -NeverReuse)."
            return
        }

        $displayName = "Mapping $mapId in Synchronisation Rule $SyncRuleId"

        if ($PSCmdlet.ShouldProcess($displayName, "Update Synchronisation Rule Mapping")) {
            Write-Verbose "Updating Synchronisation Rule Mapping: $mapId in Synchronisation Rule: $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$mapId" -Method 'PATCH' -Body $body

                Write-Verbose "Updated Synchronisation Rule Mapping: $mapId"

                # A raised -SequenceStart moves the target attribute's counter forward at save time (plan
                # decision 3); the response reports it rather than doing it silently.
                if ($result.Generation.SequenceSkippedAhead) {
                    $skip = $result.Generation.SequenceSkippedAhead
                    Write-Warning "Mapping $mapId's Sequence counter moved from $($skip.From) to $($skip.To) to honour the requested Sequence Start."
                }

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Synchronisation Rule Mapping: $_"
            }
        }
    }
}
