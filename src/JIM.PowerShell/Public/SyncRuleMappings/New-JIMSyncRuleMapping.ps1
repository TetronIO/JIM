# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Adds an Attribute Flow to a Synchronisation Rule.

    .DESCRIPTION
        Adds an Attribute Flow (mapping) to a Synchronisation Rule. On an import rule it writes a Metaverse
        attribute from Connected System attributes or an expression; on an export rule it writes a Connected System
        attribute from Metaverse attributes or an expression. With -Generate, its value is a generated value: a base
        expression plus a uniqueness token.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule to add the mapping to.

    .PARAMETER TargetMetaverseAttributeId
        On an import rule, the ID of the Metaverse attribute the mapping writes.

    .PARAMETER TargetConnectedSystemAttributeId
        On an export rule, the ID of the Connected System attribute the mapping writes.

    .PARAMETER SourceConnectedSystemAttributeId
        On an import rule, the IDs of the Connected System attributes the mapping reads. Cannot be used with
        -Expression.

    .PARAMETER SourceMetaverseAttributeId
        On an export rule, the IDs of the Metaverse attributes the mapping reads. Cannot be used with -Expression.

    .PARAMETER MissingInputBehaviour
        On an expression mapping, what happens when an attribute the expression reads has no value: EvaluateAnyway
        runs it regardless and contributes what it returns; ContributeNoValue skips it and contributes nothing, so
        Attribute Priority decides; FailMapping skips it and records an error, while the object's other attributes
        still flow; and FailObject leaves the whole object untouched and errored. EvaluateAnyway when omitted.

    .PARAMETER Expression
        The expression the mapping evaluates, reading attributes as mv["Name"] and cs["Name"]: for example '"CN=" +
        EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"'. With -Generate, the base value the token is
        added to.

    .PARAMETER PreserveWhitespace
        On an import mapping, keeps a text value that is empty or only whitespace as a value. Without it, such a
        value counts as no value: it does not flow, and it clears the Metaverse attribute.

    .PARAMETER TrimWhitespace
        On an import mapping, removes leading and trailing whitespace from a text value.

    .PARAMETER CollapseInternalWhitespace
        On an import mapping, collapses each run of whitespace inside a text value to one space.

    .PARAMETER CaseNormalisation
        On an import mapping, the case a text value is converted to.

    .PARAMETER NullIsValue
        On an import mapping, makes no value authoritative: when the rule applies to a joined, in-scope Connected
        System Object that has no value, the Metaverse attribute is cleared rather than taken from a lower-priority
        mapping. Change it later with Set-JIMMetaverseAttributePriority or Move-JIMMetaverseAttributePriority.

    .PARAMETER InitialExportOnly
        On an export mapping, writes the attribute only when the Connected System Object is created; after that JIM
        leaves it alone, and Drift Correction does not put it back.

    .PARAMETER Enabled
        Whether the mapping takes part in synchronisation from the start. $false creates it disabled, so it can be
        put in order and reviewed first; enable it later with Set-JIMSyncRuleMapping.

    .PARAMETER KeepUnique
        On an Expression mapping whose value is built from a generated value (an Email built from a generated
        Account Name), whether a target refusing the value as already in use is fixed by correcting the generated
        value. $false reports the refusal as an ordinary export error instead. Not accepted on a generated mapping.

    .PARAMETER Generate
        Makes the mapping a generated value: a base value from -Expression, which a Sequence or Random token can do
        without, plus a uniqueness token. Give -TargetMetaverseAttributeId for an import mapping, or
        -TargetConnectedSystemAttributeId for an export one.

    .PARAMETER TokenKind
        The uniqueness token: OnlyIfTaken tries the base value first and adds a suffix only when it is taken, and
        needs -Expression; Sequence adds the next number from the attribute's counter, never reused; Random adds a
        random token (see -RandomFormat). OnlyIfTaken when omitted.

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
        How many candidates are tried for one object in one synchronisation run before generation fails for it. 1000
        when omitted.

    .PARAMETER NeverReuse
        Whether a value whose assignment is deleted is retired and never issued again by this flow. Omit for
        $true (the server default and recommended setting). Always treated as true for a Sequence token,
        regardless of what is supplied, because its forward-only counter makes reuse impossible.

    .PARAMETER CollisionRemediation
        Whether JIM corrects a generated value a target rejects as already in use (Collision Remediation). While no
        other Connected System holds the value, JIM chooses the next free one and exports it again; once another
        does, the export is held for a decision instead (see Get-JIMGeneratedValueDecision). Acts only on a
        rejection from a Connected System whose Connector reports collisions (Generation.Participants'
        ReportsCollisions); elsewhere a collision is an ordinary export error. Omit for $true.

    .PARAMETER ExcludeConnectedSystemId
        Import generated mappings only: the IDs of Connected Systems to leave out of the value's availability
        checks. Values already in use in an excluded Connected System do not stop JIM choosing them; JIM neither
        checks its own records of that system nor probes it. Each must be a Connected System the value is
        exported to unchanged (an export Attribute Flow taking the generated Metaverse attribute as its only
        source); otherwise the request is refused naming the Connected System. Omit to check every Connected
        System the value is exported to. Get-JIMSyncRuleMapping's Generation.Participants lists them.

    .OUTPUTS
        The mapping created. Any warnings the save raised are written to the warning stream as well.
        AwaitingSynchronisation names the Connected System Object Type an import mapping makes its Connected System's next
        synchronisation, Delta or Full, process in full, with how many objects that is and about how long it takes (empty
        for an export mapping), and a warning saying the same is written to the warning stream.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10

        Creates an import mapping that flows Connected System attribute 10 into Metaverse attribute 5.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -SourceMetaverseAttributeId 8

        Creates an export mapping that flows Metaverse attribute 8 out to Connected System attribute 15.

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

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 `
            -Expression 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"])' -Generate -ExcludeConnectedSystemId 4

        Creates a generated Account Name that is not checked for availability in Connected System 4, for example
        a system known to keep its own accounts unique some other way.

    .LINK
        Get-JIMSyncRuleMapping
        Remove-JIMSyncRuleMapping
        Get-JIMSyncRule
        Get-JIMGeneratedValueSequence
        Restart-JIMGeneratedValues
        Test-JIMExpression
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.SyncRuleMappingDto')]
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

        # "Generated Value" (Unique Value Generation, #242), import and export mappings alike: the mapping's
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

        [Parameter(ParameterSetName = 'ImportGenerated')]
        [Parameter(ParameterSetName = 'ExportGenerated')]
        [bool]$CollisionRemediation,

        # Exclusions (release 3), import generated mappings only: an export-mode generated value is checked
        # only in its own Connected System, so it has nothing to exclude.
        [Parameter(ParameterSetName = 'ImportGenerated')]
        [int[]]$ExcludeConnectedSystemId,

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
        [bool]$Enabled,

        # Keep unique (Generated Value Forms, #2023), Expression mappings only: omit to keep the server default
        # (on); supply $false so a target's refusal of a value built from a generated value is an ordinary error.
        [Parameter(ParameterSetName = 'ImportExpression')]
        [Parameter(ParameterSetName = 'ExportExpression')]
        [bool]$KeepUnique
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

        # Keep unique (#2023). Sent only when asked for, so the server's default of on stands otherwise.
        if ($PSBoundParameters.ContainsKey('KeepUnique')) {
            $body.keepUnique = $KeepUnique
        }

        # "Generated Value" (Unique Value Generation, #242). Only the settings actually supplied are sent;
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
            if ($PSBoundParameters.ContainsKey('CollisionRemediation')) { $generation.collisionRemediation = $CollisionRemediation }
            # Always sent as a JSON array, even for a single ID: the @() keeps one value from serialising as a
            # bare number.
            if ($PSBoundParameters.ContainsKey('ExcludeConnectedSystemId')) { $generation.exclusions = @($ExcludeConnectedSystemId) }
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

                # Non-blocking warnings the save raised (for example, an Attribute Flow deriving a Metaverse
                # attribute that calls a function returning a different value each time, #1750). The save went
                # ahead; each warning is written as the server phrased it.
                foreach ($saveWarning in @($result.Warnings | Where-Object { $_ })) {
                    Write-Warning $saveWarning
                }

                # The Object Types the save made the next synchronisation process in full (#2615): advised, never blocking.
                Write-JIMAwaitingSynchronisationWarning -AwaitingSynchronisation $result.AwaitingSynchronisation

                $result
            }
            catch {
                Write-Error "Failed to create Synchronisation Rule Mapping: $_"
            }
        }
    }
}
