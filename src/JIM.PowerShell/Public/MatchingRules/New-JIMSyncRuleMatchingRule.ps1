function New-JIMSyncRuleMatchingRule {
    <#
    .SYNOPSIS
        Creates a new Object Matching Rule on a Sync Rule (advanced mode).

    .DESCRIPTION
        Creates a new Object Matching Rule on a specific Sync Rule.
        This is used in advanced mode where matching rules are per-sync rule
        rather than per-object type. The Metaverse Object Type is derived from
        the sync rule automatically.

    .PARAMETER SyncRuleId
        The unique identifier of the Sync Rule.

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
        New-JIMSyncRuleMatchingRule -SyncRuleId 5 -SourceAttributeId 25 -TargetMetaverseAttributeId 5

        Creates a matching rule on sync rule 5 that maps CS attribute 25 to MV attribute 5.

    .EXAMPLE
        New-JIMSyncRuleMatchingRule -SyncRuleId 5 -SourceMetaverseAttributeId 3 -TargetMetaverseAttributeId 5 -PassThru

        Creates an export matching rule on sync rule 5 and returns the created rule.

    .LINK
        Get-JIMSyncRuleMatchingRule
        Set-JIMSyncRuleMatchingRule
        Remove-JIMSyncRuleMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

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
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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
            targetMetaverseAttributeId = $TargetMetaverseAttributeId
            sources = @($source)
        }

        if ($PSBoundParameters.ContainsKey('Order')) {
            $body.order = $Order
        }

        if ($PSBoundParameters.ContainsKey('CaseSensitive')) {
            $body.caseSensitive = $CaseSensitive
        }

        if ($PSCmdlet.ShouldProcess("Sync Rule $SyncRuleId", "Create Matching Rule")) {
            Write-Verbose "Creating Matching Rule for Sync Rule ID: $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/matching-rules" -Method 'POST' -Body $body

                Write-Verbose "Created Matching Rule ID: $($result.id)"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to create Matching Rule: $_"
            }
        }
    }
}
