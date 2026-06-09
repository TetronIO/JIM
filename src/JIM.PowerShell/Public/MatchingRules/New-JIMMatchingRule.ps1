# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMMatchingRule {
    <#
    .SYNOPSIS
        Creates a new Object Matching Rule in JIM.

    .DESCRIPTION
        Creates a new Object Matching Rule for a Connected System Object Type.
        Object Matching Rules define how objects from a Connected System are correlated
        with Metaverse Objects during import (join) and export (provisioning) operations.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ObjectTypeId
        The unique identifier of the Object Type this rule applies to.

    .PARAMETER MetaverseObjectTypeId
        The Metaverse Object Type ID to search when evaluating this rule.
        Required for simple mode matching rules.

    .PARAMETER SourceAttributeId
        The Connected System attribute ID to use as the source for matching (import matching).
        Either this or SourceMetaverseAttributeId must be specified.

    .PARAMETER SourceMetaverseAttributeId
        The Metaverse attribute ID to use as the source for matching (export matching).
        Either this or SourceAttributeId must be specified.

    .PARAMETER TargetMetaverseAttributeId
        The Metaverse attribute ID to match against.

    .PARAMETER Order
        The evaluation order for this rule (lower values are evaluated first).
        If not specified, the rule will be added at the end.

    .PARAMETER CaseSensitive
        Whether the matching should be case-sensitive.
        When false (default), 'emp123' matches 'EMP123'.
        When true, 'emp123' does NOT match 'EMP123'.

    .PARAMETER PassThru
        If specified, returns the created Matching Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the created Matching Rule object.

    .EXAMPLE
        New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -MetaverseObjectTypeId 1 -SourceAttributeId 25 -TargetMetaverseAttributeId 5

        Creates a matching rule that maps CS attribute 25 to MV attribute 5, searching MVO type 1.

    .EXAMPLE
        New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -MetaverseObjectTypeId 1 -SourceMetaverseAttributeId 3 -TargetMetaverseAttributeId 5

        Creates an export matching rule that maps MV attribute 3 to MV attribute 5.

    .EXAMPLE
        New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -MetaverseObjectTypeId 1 -SourceAttributeId 25 -TargetMetaverseAttributeId 5 -Order 0 -PassThru

        Creates a matching rule at order 0 and returns the created rule.

    .LINK
        Get-JIMMatchingRule
        Set-JIMMatchingRule
        Remove-JIMMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory)]
        [int]$ObjectTypeId,

        [Parameter(Mandatory)]
        [int]$MetaverseObjectTypeId,

        [Parameter(ParameterSetName = 'CSAttribute')]
        [int]$SourceAttributeId,

        [Parameter(ParameterSetName = 'MVAttribute')]
        [int]$SourceMetaverseAttributeId,

        [Parameter(Mandatory)]
        [int]$TargetMetaverseAttributeId,

        [Parameter()]
        [int]$Order,

        [Parameter()]
        [bool]$CaseSensitive,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Build source based on which attribute was specified
        $source = @{ order = 0 }
        if ($PSBoundParameters.ContainsKey('SourceAttributeId')) {
            $source.connectedSystemAttributeId = $SourceAttributeId
        }
        elseif ($PSBoundParameters.ContainsKey('SourceMetaverseAttributeId')) {
            $source.metaverseAttributeId = $SourceMetaverseAttributeId
        }
        else {
            Write-Error "Either -SourceAttributeId or -SourceMetaverseAttributeId must be specified."
            return
        }

        $body = @{
            connectedSystemObjectTypeId = $ObjectTypeId
            metaverseObjectTypeId = $MetaverseObjectTypeId
            targetMetaverseAttributeId = $TargetMetaverseAttributeId
            sources = @($source)
        }

        if ($PSBoundParameters.ContainsKey('Order')) {
            $body.order = $Order
        }

        if ($PSBoundParameters.ContainsKey('CaseSensitive')) {
            $body.caseSensitive = $CaseSensitive
        }

        if ($PSCmdlet.ShouldProcess("Connected System $ConnectedSystemId, Object Type $ObjectTypeId", "Create Matching Rule")) {
            Write-Verbose "Creating Matching Rule for Connected System ID: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-rules" -Method 'POST' -Body $body

                Write-Verbose "Created Matching Rule ID: $($result.id)"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to create Matching Rule: $_"
            }
        }
    }
}
