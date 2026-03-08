function Set-JIMSyncRuleMatchingRule {
    <#
    .SYNOPSIS
        Updates an existing Object Matching Rule on a Sync Rule (advanced mode).

    .DESCRIPTION
        Updates an Object Matching Rule on a specific Sync Rule.
        You can update the order, target Metaverse attribute, or source attributes.

    .PARAMETER SyncRuleId
        The unique identifier of the Sync Rule.

    .PARAMETER Id
        The unique identifier of the Matching Rule to update.

    .PARAMETER Order
        The new evaluation order for this rule (lower values are evaluated first).

    .PARAMETER TargetMetaverseAttributeId
        The new Metaverse attribute ID to match against.

    .PARAMETER SourceAttributeId
        The new Connected System attribute ID to use as the source.
        Note: This replaces all existing sources with a single new source.

    .PARAMETER SourceMetaverseAttributeId
        The new Metaverse attribute ID to use as the source (for export matching).
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
        Set-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12 -Order 0

        Updates the order of Matching Rule 12 on Sync Rule 5 to be first (order 0).

    .EXAMPLE
        Get-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12 | Set-JIMSyncRuleMatchingRule -CaseSensitive $false

        Updates case sensitivity using pipeline input.

    .LINK
        Get-JIMSyncRuleMatchingRule
        New-JIMSyncRuleMatchingRule
        Remove-JIMSyncRuleMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter()]
        [int]$Order,

        [Parameter()]
        [int]$TargetMetaverseAttributeId,

        [Parameter()]
        [int]$SourceAttributeId,

        [Parameter()]
        [int]$SourceMetaverseAttributeId,

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
        elseif ($PSBoundParameters.ContainsKey('SourceMetaverseAttributeId')) {
            $body.sources = @(
                @{
                    order = 0
                    metaverseAttributeId = $SourceMetaverseAttributeId
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

        if ($PSCmdlet.ShouldProcess("Matching Rule $Id on Sync Rule $SyncRuleId", "Update")) {
            Write-Verbose "Updating Matching Rule ID: $Id for Sync Rule ID: $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/matching-rules/$Id" -Method 'PUT' -Body $body

                Write-Verbose "Updated Matching Rule ID: $Id"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to update Matching Rule: $_"
            }
        }
    }
}
