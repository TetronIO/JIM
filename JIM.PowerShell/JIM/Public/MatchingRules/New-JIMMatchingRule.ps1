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

    .PARAMETER SourceAttributeId
        The Connected System attribute ID to use as the source for matching.

    .PARAMETER TargetMetaverseAttributeId
        The Metaverse attribute ID to match against.

    .PARAMETER Order
        The evaluation order for this rule (lower values are evaluated first).
        If not specified, the rule will be added at the end.

    .PARAMETER PassThru
        If specified, returns the created Matching Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the created Matching Rule object.

    .EXAMPLE
        New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -SourceAttributeId 25 -TargetMetaverseAttributeId 5

        Creates a matching rule that maps CS attribute 25 to MV attribute 5.

    .EXAMPLE
        New-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 -SourceAttributeId 25 -TargetMetaverseAttributeId 5 -Order 0 -PassThru

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
        [int]$SourceAttributeId,

        [Parameter(Mandatory)]
        [int]$TargetMetaverseAttributeId,

        [Parameter()]
        [int]$Order,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $body = @{
            connectedSystemObjectTypeId = $ObjectTypeId
            targetMetaverseAttributeId = $TargetMetaverseAttributeId
            sources = @(
                @{
                    order = 0
                    connectedSystemAttributeId = $SourceAttributeId
                }
            )
        }

        if ($PSBoundParameters.ContainsKey('Order')) {
            $body.order = $Order
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
