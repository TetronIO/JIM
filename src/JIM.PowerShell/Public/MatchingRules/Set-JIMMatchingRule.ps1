function Set-JIMMatchingRule {
    <#
    .SYNOPSIS
        Updates an existing Object Matching Rule in JIM.

    .DESCRIPTION
        Updates an Object Matching Rule for a Connected System.
        You can update the order, target Metaverse attribute, or source attributes.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER Id
        The unique identifier of the Matching Rule to update.

    .PARAMETER Order
        The new evaluation order for this rule (lower values are evaluated first).

    .PARAMETER TargetMetaverseAttributeId
        The new Metaverse attribute ID to match against.

    .PARAMETER SourceAttributeId
        The new Connected System attribute ID to use as the source.
        Note: This replaces all existing sources with a single new source.

    .PARAMETER CaseSensitive
        Whether the matching should be case-sensitive.
        When false (default), 'emp123' matches 'EMP123'.
        When true, 'emp123' does NOT match 'EMP123'.

    .PARAMETER PassThru
        If specified, returns the updated Matching Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Matching Rule object.

    .EXAMPLE
        Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -Order 0

        Updates the order of Matching Rule 5 to be first (order 0).

    .EXAMPLE
        Set-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -TargetMetaverseAttributeId 10 -PassThru

        Updates the target MV attribute and returns the updated rule.

    .EXAMPLE
        Get-JIMMatchingRule -ConnectedSystemId 1 -Id 5 | Set-JIMMatchingRule -Order 2

        Updates the order using pipeline input.

    .LINK
        Get-JIMMatchingRule
        New-JIMMatchingRule
        Remove-JIMMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter()]
        [int]$Order,

        [Parameter()]
        [int]$TargetMetaverseAttributeId,

        [Parameter()]
        [int]$SourceAttributeId,

        [Parameter()]
        [bool]$CaseSensitive,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $body = @{}

        if ($PSBoundParameters.ContainsKey('Order')) {
            $body.order = $Order
        }

        if ($PSBoundParameters.ContainsKey('TargetMetaverseAttributeId')) {
            $body.targetMetaverseAttributeId = $TargetMetaverseAttributeId
        }

        if ($PSBoundParameters.ContainsKey('SourceAttributeId')) {
            $body.sources = @(
                @{
                    order = 0
                    connectedSystemAttributeId = $SourceAttributeId
                }
            )
        }

        if ($PSBoundParameters.ContainsKey('CaseSensitive')) {
            $body.caseSensitive = $CaseSensitive
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSCmdlet.ShouldProcess("Matching Rule $Id", "Update")) {
            Write-Verbose "Updating Matching Rule ID: $Id for Connected System ID: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-rules/$Id" -Method 'PUT' -Body $body

                Write-Verbose "Updated Matching Rule ID: $Id"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to update Matching Rule: $_"
            }
        }
    }
}
