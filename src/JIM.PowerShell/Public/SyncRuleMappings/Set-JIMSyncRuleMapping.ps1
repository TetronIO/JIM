# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Changes how an Attribute Flow behaves.

    .DESCRIPTION
        Changes how an existing Attribute Flow behaves, leaving what it reads and writes alone.
        Only the parameters you supply are changed; everything else is left as it is.

        What a mapping targets, and whether its source is an attribute or an Expression, cannot be
        changed here. Those revalidate against attribute types and plurality, and for an import
        mapping they reopen its place in the attribute's Attribute Priority order, so they remain a
        Remove-JIMSyncRuleMapping followed by a New-JIMSyncRuleMapping.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule the mapping belongs to.

    .PARAMETER MappingId
        The ID of the mapping to change.

    .PARAMETER InputObject
        A mapping from Get-JIMSyncRuleMapping, through the pipeline.

    .PARAMETER Expression
        Replaces the mapping's expression. Expression mappings only.

    .PARAMETER MissingInputBehaviour
        On an expression mapping, what happens when an attribute the expression reads has no value: EvaluateAnyway
        runs it regardless and contributes what it returns; ContributeNoValue skips it and contributes nothing, so
        Attribute Priority decides; FailMapping skips it and records an error, while the object's other attributes
        still flow; and FailObject leaves the whole object untouched and errored.

    .PARAMETER NullIsValue
        On an import mapping, whether no value is authoritative: when the rule applies to a joined, in-scope
        Connected System Object that has no value, the Metaverse attribute is cleared rather than taken from a
        lower-priority mapping.

    .PARAMETER InboundValueProcessing
        Text value-processing transforms applied as the value flows to the Metaverse, as a
        comma-separated set of flag names (for example 'TreatWhitespaceAsNoValue, TrimWhitespace').
        Import mappings only.

    .PARAMETER CaseNormalisation
        On an import mapping, the case a text value is converted to.

    .PARAMETER InitialExportOnly
        On an export mapping, whether the attribute is written only when the Connected System Object is created;
        after that JIM leaves it alone, and Drift Correction does not put it back.

    .PARAMETER Enabled
        Enables or disables the mapping. A disabled mapping is skipped in both directions; enabling it clears the
        reason JIM recorded for disabling it, if any.

    .PARAMETER KeepUnique
        On an Expression mapping whose value is built from a generated value (an Email built from a generated
        Account Name), whether a target refusing the value as already in use is fixed by correcting the generated
        value. $false reports the refusal as an ordinary export error instead. Changing it releases any decision the
        generated value is waiting on. Refused on any other mapping.

    .PARAMETER TokenKind
        Changes a generated mapping's uniqueness token. A mapping cannot be made generated, or stop being generated,
        here: delete it and create it again.

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
        Random only: the shape of the token.

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

    .PARAMETER CollisionRemediation
        Whether JIM corrects a generated value a target rejects as already in use (Collision Remediation). Omit to
        leave it unchanged. Changing it releases any of this Attribute Flow's values held for a decision, so the next
        export tries again under the new setting.

    .PARAMETER ExcludeConnectedSystemId
        Generated import mappings only: replaces the IDs of the Connected Systems left out of the value's
        availability checks. Values already in use in an excluded Connected System do not stop JIM choosing
        them. Each must be a Connected System the value is exported to unchanged, otherwise the update is
        refused naming the Connected System. Pass @() to clear every exclusion, so every Connected System is
        checked again; omit to leave the exclusions unchanged. Refused for a generated value on an export
        Synchronisation Rule, which is checked only in its own Connected System.

    .PARAMETER SaveAnyway
        Saves even though the change stops configuration test cases passing. Without it, or
        -AcceptNewResult, such a change is refused, nothing is saved, and the error names each case.
        Configuration History records the cases saved past, by name.

    .PARAMETER AcceptNewResult
        Takes what the configuration now gives as what each configuration test case that stopped
        passing expects, rewriting those cases, and saves. A case whose new result cannot be
        expected (it needs review, could not run, or its Scope cannot be determined) still refuses
        the change unless -SaveAnyway is given too.

    .PARAMETER PassThru
        Returns the mapping as changed.

    .OUTPUTS
        With -PassThru, the mapping as changed; otherwise nothing. Its warnings, and any derived Attribute Flows the
        change left with a missing input, are written to the warning stream either way. AwaitingSynchronisation names the
        Connected System Object Type an import mapping's change makes its Connected System's next synchronisation, Delta
        or Full, process in full, with how many objects that is and about how long it takes (empty for an export
        mapping), and a warning saying the same is written to the warning stream.

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
        Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -KeepUnique $false

        Stops correcting a generated Account Name when a target refuses the User Principal Name built
        from it (mapping 12): such a refusal is then reported as an ordinary export error.

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

    .EXAMPLE
        Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -ExcludeConnectedSystemId 4, 7

        Leaves Connected Systems 4 and 7 out of a generated value's availability checks, replacing any
        exclusions it had.

    .EXAMPLE
        Set-JIMSyncRuleMapping -SyncRuleId 1 -MappingId 12 -ExcludeConnectedSystemId @()

        Clears a generated value's exclusions, so every Connected System it is exported to is checked again.

    .LINK
        Get-JIMSyncRuleMapping
        New-JIMSyncRuleMapping
        Remove-JIMSyncRuleMapping
        Get-JIMGeneratedValueSequence
        Restart-JIMGeneratedValues
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'ById')]
    [OutputType('JIM.SyncRuleMappingDto')]
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

        # Keep unique (Generated Value Forms, #2023), Expression mappings only.
        [bool]$KeepUnique,

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

        [bool]$CollisionRemediation,

        # Exclusions (release 3): replaces them; @() clears them, and is sent as an empty JSON array.
        [int[]]$ExcludeConnectedSystemId,

        [switch]$SaveAnyway,

        [switch]$AcceptNewResult,

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
        if ($PSBoundParameters.ContainsKey('KeepUnique')) { $body.keepUnique = $KeepUnique }

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
        if ($PSBoundParameters.ContainsKey('CollisionRemediation')) { $generation.collisionRemediation = $CollisionRemediation }
        # Always a JSON array: @() keeps a single ID from serialising as a bare number, and an empty list
        # from being dropped, since [] is what tells the API to clear the exclusions.
        if ($PSBoundParameters.ContainsKey('ExcludeConnectedSystemId')) { $generation.exclusions = @($ExcludeConnectedSystemId) }
        if ($generation.Count -gt 0) { $body.generation = $generation }

        if ($body.Count -eq 0) {
            Write-Error "No settings were supplied to change. Supply at least one of -Expression, -MissingInputBehaviour, -NullIsValue, -InboundValueProcessing, -CaseNormalisation, -InitialExportOnly, -Enabled, -KeepUnique, or a generated mapping's uniqueness token settings (-TokenKind, -SuffixStyle, -SuffixStart, -SequenceStart, -SequenceIncrement, -FixedWidth, -OnWidthExceeded, -RandomFormat, -RandomLength, -Separator, -AttemptLimit, -NeverReuse, -CollisionRemediation)."
            return
        }

        $displayName = "Mapping $mapId in Synchronisation Rule $SyncRuleId"

        if ($PSCmdlet.ShouldProcess($displayName, "Update Synchronisation Rule Mapping")) {
            Write-Verbose "Updating Synchronisation Rule Mapping: $mapId in Synchronisation Rule: $SyncRuleId"

            try {
                # The save gate (#2055 Phase 4b): what to do if the change stops configuration test cases passing.
                if ($SaveAnyway) { $body.saveAnyway = $true }
                if ($AcceptNewResult) { $body.acceptNewResults = $true }
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$mapId" -Method 'PATCH' -Body $body

                Write-Verbose "Updated Synchronisation Rule Mapping: $mapId"

                # A raised -SequenceStart moves the target attribute's counter forward at save time (plan
                # decision 3); the response reports it rather than doing it silently.
                if ($result.Generation.SequenceSkippedAhead) {
                    $skip = $result.Generation.SequenceSkippedAhead
                    Write-Warning "Mapping $mapId's Sequence counter moved from $($skip.From) to $($skip.To) to honour the requested Sequence Start."
                }

                # Non-blocking warnings the save raised (for example, an Attribute Flow deriving a Metaverse
                # attribute that calls a function returning a different value each time, #1750). The save went
                # ahead; each warning is written as the server phrased it.
                foreach ($saveWarning in @($result.Warnings | Where-Object { $_ })) {
                    Write-Warning $saveWarning
                }

                # Derived flows the update left with a missing input (#1750, FR 3): reported, never blocking.
                Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows $result.DependentDerivedFlows

                # The Object Types the save made the next synchronisation process in full (#2615): advised, never blocking.
                Write-JIMAwaitingSynchronisationWarning -AwaitingSynchronisation $result.AwaitingSynchronisation

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
