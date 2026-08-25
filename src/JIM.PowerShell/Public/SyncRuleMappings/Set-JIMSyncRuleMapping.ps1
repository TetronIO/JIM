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

    .PARAMETER PassThru
        Returns the updated mapping.

    .OUTPUTS
        None by default. The updated mapping when -PassThru is supplied.

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

    .LINK
        Get-JIMSyncRuleMapping
        New-JIMSyncRuleMapping
        Remove-JIMSyncRuleMapping
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

        if ($body.Count -eq 0) {
            Write-Error "No settings were supplied to change. Supply at least one of -Expression, -MissingInputBehaviour, -NullIsValue, -InboundValueProcessing, -CaseNormalisation, -InitialExportOnly or -Enabled."
            return
        }

        $displayName = "Mapping $mapId in Synchronisation Rule $SyncRuleId"

        if ($PSCmdlet.ShouldProcess($displayName, "Update Synchronisation Rule Mapping")) {
            Write-Verbose "Updating Synchronisation Rule Mapping: $mapId in Synchronisation Rule: $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings/$mapId" -Method 'PATCH' -Body $body

                Write-Verbose "Updated Synchronisation Rule Mapping: $mapId"

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
