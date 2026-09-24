# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Creates a new Synchronisation Rule Mapping (attribute flow rule) in JIM.

    .DESCRIPTION
        Creates a new attribute flow mapping for a Synchronisation Rule.
        For Import rules, this maps Connected System attributes to Metaverse attributes.
        For Export rules, this maps Metaverse attributes to Connected System attributes.
        Alternatively, an expression can be used as the source for dynamic value generation.

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule to add the mapping to.
        Also accepts pipeline input via the Id property.

    .PARAMETER TargetMetaverseAttributeId
        For Import rules: The ID of the Metaverse attribute that will receive the value.

    .PARAMETER TargetConnectedSystemAttributeId
        For Export rules: The ID of the Connected System attribute that will receive the value.

    .PARAMETER SourceConnectedSystemAttributeId
        For Import rules: The ID of the Connected System attribute to use as the source.
        Can be a single value or an array for multiple sources.
        Mutually exclusive with -Expression.

    .PARAMETER SourceMetaverseAttributeId
        For Export rules: The ID of the Metaverse attribute to use as the source.
        Can be a single value or an array for multiple sources.
        Mutually exclusive with -Expression.

    .PARAMETER MissingInputBehaviour
        For expression mappings: what to do when an attribute the expression reads has no value on the object
        being synchronised. Omit for EvaluateAnyway, which evaluates the expression regardless and is what JIM
        has always done.
        - EvaluateAnyway: evaluate with the input absent and contribute whatever it returns.
        - ContributeNoValue: do not evaluate; contribute nothing, resolved by Attribute Priority. Not an error.
        - FailMapping: do not evaluate; record an ExpressionMissingInput error. The object's other attributes
          still flow.
        - FailObject: do not evaluate anything for the object; it is errored and left untouched.

    .PARAMETER Expression
        An expression to evaluate for the source value.
        Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
        Example: '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"'

    .PARAMETER PreserveWhitespace
        For import mappings only. By default JIM treats a whitespace-only or empty imported text value as no
        value (it does not flow, and clears any existing Metaverse value). Use this switch to preserve
        whitespace as a literal value instead.

    .PARAMETER TrimWhitespace
        For import mappings only. Removes leading and trailing whitespace from the imported text value.

    .PARAMETER CollapseInternalWhitespace
        For import mappings only. Collapses runs of internal whitespace down to a single space.

    .PARAMETER CaseNormalisation
        For import mappings only. Normalises the case of the imported text value: None (default), Upper, Lower
        or Title.

    .PARAMETER NullIsValue
        For import mappings only. When this Synchronisation Rule applies to a Connected System Object that is
        joined to a Metaverse Object, but contributes no value, treat that as an authoritative "no value":
        clear the Metaverse Object attribute value rather than falling back to a lower-priority contributor.
        A rule that results in no opinion (rule disabled, Connected System Object not joined, or out of scope)
        is skipped regardless. Off by default. Change it on an existing mapping with
        Set-JIMMetaverseAttributePriority or Move-JIMMetaverseAttributePriority.

    .PARAMETER InitialExportOnly
        For export mappings only. When set, the mapping only flows during the initial provisioning (Create)
        export; afterwards the target attribute is unmanaged by JIM on that Connected System Object and
        Drift Correction does not re-assert it.

    .PARAMETER Enabled
        Whether the mapping is evaluated by synchronisation from the moment it is created. Omit to create the
        mapping enabled (the server default). Supply $false to create it disabled, so it can be ordered and
        reviewed before it starts flowing values; a disabled mapping is skipped in both directions until it
        is re-enabled with Set-JIMSyncRuleMapping -Enabled $true.

    .PARAMETER Generate
        Makes this "JIM generates it" (Unique Value Generation, #242): the mapping's value is its base
        expression (optional; supply with -Expression) plus a uniqueness token. Requires either
        -TargetMetaverseAttributeId (an import mapping) or -TargetConnectedSystemAttributeId (an export
        mapping). Exclusions and Collision Remediation are not configurable from any surface in this release.

    .PARAMETER TokenKind
        Which uniqueness token the mapping appends. Omit for OnlyIfTaken (the server default): the base value
        is tried first, and only suffixed when it is already taken.
        - OnlyIfTaken: try the base value; append a collision suffix only when needed. Requires -Expression.
        - Sequence: always append the next number from the target attribute's counter. Never reused.
        - Random: always append a cryptographically random token (-RandomFormat, -RandomLength).

    .PARAMETER SuffixStyle
        OnlyIfTaken only: whether the collision suffix is a Number or a Letter. Omit for Number.

    .PARAMETER SuffixStart
        OnlyIfTaken only: the first suffix value tried once the bare base value is taken. Omit for 1.

    .PARAMETER SequenceStart
        Sequence only: the lowest number this flow will ever issue. If this stands above the target
        attribute's counter, the save moves the counter forward to it and the result's
        Generation.SequenceSkippedAhead reports the move; a warning is written naming the old and new
        positions. Omit for 1.

    .PARAMETER SequenceIncrement
        Sequence only: how much the counter advances per issued number. Omit for 1.

    .PARAMETER FixedWidth
        Sequence only: zero-pads the number to this many digits. Omit for no padding.

    .PARAMETER OnWidthExceeded
        Sequence only, and only meaningful with -FixedWidth: what happens when a number would no longer fit.
        StopAndReport (the default) stops the object with an attributed error; AllowLonger lets the number
        grow past the configured width.

    .PARAMETER RandomFormat
        Random only: the shape of the token. Guid (the default, a 36-character GUID), Hex (lower-case
        hexadecimal) or Digits (decimal digits). -RandomLength is required for Hex and Digits.

    .PARAMETER RandomLength
        Random only: the length of the token in characters. Required for -RandomFormat Hex or Digits; must be
        omitted for Guid, whose length is fixed.

    .PARAMETER Separator
        The characters placed between the base value and the token, when both are present. Omit for no
        separator. Never valid for a Number target.

    .PARAMETER AttemptLimit
        The maximum number of candidates tried, per object per synchronisation run, before generation fails
        hard for that object. Omit for 1000.

    .PARAMETER NeverReuse
        Whether a value whose assignment is deleted is retired and never issued again by this flow. Omit for
        $true (the server default and recommended setting). Always treated as true for a Sequence token,
        regardless of what is supplied, because its forward-only counter makes reuse impossible.

    .OUTPUTS
        PSCustomObject representing the created Synchronisation Rule Mapping. A generated mapping's Generation
        property carries its uniqueness token settings; Generation.SequenceSkippedAhead is present only when
        -SequenceStart raised the target attribute's counter on this save.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10

        Creates an import mapping that flows data from CS attribute 10 to MV attribute 5.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -SourceMetaverseAttributeId 8

        Creates an export mapping that flows data from MV attribute 8 to CS attribute 15.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -Expression '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=TestUsers,DC=domain,DC=local"'

        Creates an export mapping that uses an expression to construct a Distinguished Name.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -Expression '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=TestUsers,DC=domain,DC=local"' -MissingInputBehaviour FailObject

        Builds a Distinguished Name, and refuses to export an object with no Display Name rather than exporting
        "CN=,OU=TestUsers,DC=domain,DC=local".

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -Expression 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"]) + "@company.com"'

        Creates an import mapping that uses an expression to construct an email address.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -TrimWhitespace -CaseNormalisation Lower

        Creates an import mapping that trims surrounding whitespace and lower-cases the value (and, by default,
        treats whitespace-only values as no value).

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -NullIsValue

        Creates an import mapping that asserts no value when the source is connected and in scope but supplies
        nothing, clearing the Metaverse Object attribute value instead of falling through to a lower-priority
        contributor. The mapping still lands at the bottom of the attribute's priority list; promote it with
        Move-JIMMetaverseAttributePriority before it can win resolution.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -SourceMetaverseAttributeId 8 -InitialExportOnly

        Creates an export mapping that only flows during initial provisioning; the attribute is unmanaged afterwards.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10 -Enabled $false

        Creates the mapping disabled, so it can be ordered and reviewed before it starts flowing values.
        Enable it when ready with Set-JIMSyncRuleMapping -Enabled $true.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 `
            -Expression 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"])' -Generate

        Creates a generated import mapping: try "first.last" bare, and only if it is already taken append a
        collision suffix (OnlyIfTaken, the default token kind).

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 12 -Generate -TokenKind Sequence -SequenceStart 100000 -FixedWidth 6

        Creates a generated Employee Number mapping with no base expression: a bare, zero-padded, six-digit
        Sequence starting at 100000.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 40 -Generate -TokenKind Random -RandomFormat Hex -RandomLength 12

        Creates a generated export mapping whose value is a twelve-character random hexadecimal token, with no
        base expression.

    .LINK
        Get-JIMSyncRuleMapping
        Remove-JIMSyncRuleMapping
        Get-JIMSyncRule
        Get-JIMGeneratedValueSequence
        Restart-JIMGeneratedValues
        Test-JIMExpression
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ImportAttribute')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ImportExpression')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ExportAttribute')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ExportExpression')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ImportGenerated')]
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, ParameterSetName = 'ExportGenerated')]
        [Alias('Id')]
        [int]$SyncRuleId,

        [Parameter(ParameterSetName = 'ImportAttribute')]
        [Parameter(ParameterSetName = 'ImportExpression')]
        [Parameter(ParameterSetName = 'ImportGenerated')]
        [int]$TargetMetaverseAttributeId,

        [Parameter(ParameterSetName = 'ExportAttribute')]
        [Parameter(ParameterSetName = 'ExportExpression')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [int]$TargetConnectedSystemAttributeId,

        [Parameter(ParameterSetName = 'ImportAttribute')]
        [int[]]$SourceConnectedSystemAttributeId,

        [Parameter(ParameterSetName = 'ExportAttribute')]
        [int[]]$SourceMetaverseAttributeId,

        # The base expression. Mandatory for an ordinary Expression mapping; optional for a generated mapping
        # (Unique Value Generation, #242), whose Sequence and Random token kinds can stand with no base value.
        [Parameter(ParameterSetName = 'ImportExpression')]
        [Parameter(ParameterSetName = 'ExportExpression')]
        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [string]$Expression,

        # What the Expression does when an attribute it reads has no value on the object being synchronised.
        # Omit for EvaluateAnyway, which is what JIM has always done.
        [Parameter(ParameterSetName = 'ImportExpression')]
        [Parameter(ParameterSetName = 'ExportExpression')]
        [ValidateSet('EvaluateAnyway', 'ContributeNoValue', 'FailMapping', 'FailObject')]
        [string]$MissingInputBehaviour,

        # "JIM generates it" (Unique Value Generation, #242), import and export mappings alike: the mapping's
        # value is its (optional) base expression plus a uniqueness token, rather than a plain attribute or
        # Expression mapping.
        [Parameter(Mandatory, ParameterSetName = 'ImportGenerated')]
        [Parameter(Mandatory, ParameterSetName = 'ExportGenerated')]
        [switch]$Generate,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [ValidateSet('OnlyIfTaken', 'Sequence', 'Random')]
        [string]$TokenKind,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [ValidateSet('Number', 'Letter')]
        [string]$SuffixStyle,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [int]$SuffixStart,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [long]$SequenceStart,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [int]$SequenceIncrement,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [int]$FixedWidth,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [ValidateSet('StopAndReport', 'AllowLonger')]
        [string]$OnWidthExceeded,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [ValidateSet('Guid', 'Hex', 'Digits')]
        [string]$RandomFormat,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [int]$RandomLength,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [string]$Separator,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [int]$AttemptLimit,

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [bool]$NeverReuse,

        # Inbound value processing (import mappings only). Whitespace-only/empty text values are treated as
        # no value by default; use -PreserveWhitespace to keep them as literal values instead.
        [Parameter(ParameterSetName = 'ImportAttribute')]
        [Parameter(ParameterSetName = 'ImportExpression')]
        [switch]$PreserveWhitespace,

        [Parameter(ParameterSetName = 'ImportAttribute')]
        [Parameter(ParameterSetName = 'ImportExpression')]
        [switch]$TrimWhitespace,

        [Parameter(ParameterSetName = 'ImportAttribute')]
        [Parameter(ParameterSetName = 'ImportExpression')]
        [switch]$CollapseInternalWhitespace,

        [Parameter(ParameterSetName = 'ImportAttribute')]
        [Parameter(ParameterSetName = 'ImportExpression')]
        [ValidateSet('None', 'Upper', 'Lower', 'Title')]
        [string]$CaseNormalisation = 'None',

        # Attribute Priority (#91), import mappings only: treat "connected, in scope, but no value" as an
        # authoritative clear rather than falling through to the next contributor.
        [Parameter(ParameterSetName = 'ImportAttribute')]
        [Parameter(ParameterSetName = 'ImportExpression')]
        [switch]$NullIsValue,

        # Initial Export Only (#223), export mappings only: the mapping flows solely during the initial
        # provisioning (Create) export; the attribute is unmanaged by JIM afterwards.
        [Parameter(ParameterSetName = 'ExportAttribute')]
        [Parameter(ParameterSetName = 'ExportExpression')]
        [switch]$InitialExportOnly,

        # Create disabled (#1485), import and export mappings alike: omit to create the mapping enabled (the
        # server default); supply $false to create it disabled for ordering and review before it flows values.
        [Parameter()]
        [bool]$Enabled
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Determine direction and validate parameters
        $isImport = $PSBoundParameters.ContainsKey('TargetMetaverseAttributeId')
        $isExport = $PSBoundParameters.ContainsKey('TargetConnectedSystemAttributeId')
        $hasExpression = $PSBoundParameters.ContainsKey('Expression') -and -not [string]::IsNullOrWhiteSpace($Expression)
        $isGenerated = $PSBoundParameters.ContainsKey('Generate')

        if (-not $isImport -and -not $isExport) {
            Write-Error "You must specify either -TargetMetaverseAttributeId (for import) or -TargetConnectedSystemAttributeId (for export)."
            return
        }

        # Build request body
        $body = @{
            sources = @()
        }

        if ($isImport) {
            $body.targetMetaverseAttributeId = $TargetMetaverseAttributeId

            if ($hasExpression) {
                # Expression-based import mapping
                $expressionSource = @{
                    order = 0
                    expression = $Expression
                }
                if ($PSBoundParameters.ContainsKey('MissingInputBehaviour')) {
                    # Sent as the enum member name; the API rejects numeric ordinals.
                    $expressionSource.missingInputBehaviour = $MissingInputBehaviour
                }
                $body.sources += $expressionSource
            }
            elseif ($SourceConnectedSystemAttributeId) {
                # Attribute-based import mapping
                $order = 0
                foreach ($sourceId in $SourceConnectedSystemAttributeId) {
                    $body.sources += @{
                        order = $order
                        connectedSystemAttributeId = $sourceId
                    }
                    $order++
                }
            }
            elseif ($isGenerated) {
                # A generated mapping's base expression is optional: Sequence and Random token kinds stand
                # with no base value at all (the server validates this against -TokenKind).
            }
            else {
                Write-Error "-SourceConnectedSystemAttributeId or -Expression is required for import mappings."
                return
            }

            # Inbound value processing (#843), import mappings only. The flags enum is sent as a
            # comma-separated set of names; whitespace is treated as no value unless -PreserveWhitespace.
            $processingFlags = @()
            if (-not $PreserveWhitespace) { $processingFlags += 'TreatWhitespaceAsNoValue' }
            if ($TrimWhitespace) { $processingFlags += 'TrimWhitespace' }
            if ($CollapseInternalWhitespace) { $processingFlags += 'CollapseInternalWhitespace' }
            $body.inboundValueProcessing = if ($processingFlags.Count -gt 0) { $processingFlags -join ', ' } else { 'None' }
            $body.caseNormalisation = $CaseNormalisation

            # Attribute Priority (#91). Sent only when asked for, so the server's default of false stands otherwise.
            if ($NullIsValue) { $body.nullIsValue = $true }

            $targetDescription = "MV Attribute $TargetMetaverseAttributeId"
        }
        else {
            $body.targetConnectedSystemAttributeId = $TargetConnectedSystemAttributeId

            if ($hasExpression) {
                # Expression-based export mapping
                $expressionSource = @{
                    order = 0
                    expression = $Expression
                }
                if ($PSBoundParameters.ContainsKey('MissingInputBehaviour')) {
                    # Sent as the enum member name; the API rejects numeric ordinals.
                    $expressionSource.missingInputBehaviour = $MissingInputBehaviour
                }
                $body.sources += $expressionSource
            }
            elseif ($SourceMetaverseAttributeId) {
                # Attribute-based export mapping
                $order = 0
                foreach ($sourceId in $SourceMetaverseAttributeId) {
                    $body.sources += @{
                        order = $order
                        metaverseAttributeId = $sourceId
                    }
                    $order++
                }
            }
            elseif ($isGenerated) {
                # A generated mapping's base expression is optional: Sequence and Random token kinds stand
                # with no base value at all (the server validates this against -TokenKind).
            }
            else {
                Write-Error "-SourceMetaverseAttributeId or -Expression is required for export mappings."
                return
            }

            # Initial Export Only (#223), export mappings only.
            if ($InitialExportOnly) {
                $body.initialExportOnly = $true
            }

            $targetDescription = "CS Attribute $TargetConnectedSystemAttributeId"
        }

        # Create disabled (#1485). Sent only when asked for, so the server's default of enabled stands
        # otherwise.
        if ($PSBoundParameters.ContainsKey('Enabled')) {
            $body.enabled = $Enabled
        }

        # "JIM generates it" (Unique Value Generation, #242). Only the settings actually supplied are sent;
        # every omitted one leaves the server's own default standing (see SyncRuleMappingGeneration).
        if ($isGenerated) {
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
            $body.generation = $generation
        }

        if ($PSCmdlet.ShouldProcess("$targetDescription in Synchronisation Rule $SyncRuleId", "Create Mapping")) {
            Write-Verbose "Creating Synchronisation Rule Mapping for Synchronisation Rule: $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings" -Method 'POST' -Body $body

                Write-Verbose "Created Synchronisation Rule Mapping with ID: $($result.id)"

                # A raised -SequenceStart moves the target attribute's counter forward at save time (plan
                # decision 3); the response reports it rather than doing it silently.
                if ($result.Generation.SequenceSkippedAhead) {
                    $skip = $result.Generation.SequenceSkippedAhead
                    Write-Warning "Mapping $($result.Id)'s Sequence counter moved from $($skip.From) to $($skip.To) to honour the requested Sequence Start."
                }

                $result
            }
            catch {
                Write-Error "Failed to create Synchronisation Rule Mapping: $_"
            }
        }
    }
}
