function New-JIMSyncRuleMapping {
    <#
    .SYNOPSIS
        Creates a new Sync Rule Mapping (attribute flow rule) in JIM.

    .DESCRIPTION
        Creates a new attribute flow mapping for a Sync Rule.
        For Import rules, this maps Connected System attributes to Metaverse attributes.
        For Export rules, this maps Metaverse attributes to Connected System attributes.

    .PARAMETER SyncRuleId
        The unique identifier of the Sync Rule to add the mapping to.

    .PARAMETER TargetMetaverseAttributeId
        For Import rules: The ID of the Metaverse attribute that will receive the value.

    .PARAMETER TargetConnectedSystemAttributeId
        For Export rules: The ID of the Connected System attribute that will receive the value.

    .PARAMETER SourceConnectedSystemAttributeId
        For Import rules: The ID of the Connected System attribute to use as the source.
        Can be a single value or an array for multiple sources.

    .PARAMETER SourceMetaverseAttributeId
        For Export rules: The ID of the Metaverse attribute to use as the source.
        Can be a single value or an array for multiple sources.

    .OUTPUTS
        PSCustomObject representing the created Sync Rule Mapping.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 1 -TargetMetaverseAttributeId 5 -SourceConnectedSystemAttributeId 10

        Creates an import mapping that flows data from CS attribute 10 to MV attribute 5.

    .EXAMPLE
        New-JIMSyncRuleMapping -SyncRuleId 2 -TargetConnectedSystemAttributeId 15 -SourceMetaverseAttributeId 8

        Creates an export mapping that flows data from MV attribute 8 to CS attribute 15.

    .LINK
        Get-JIMSyncRuleMapping
        Remove-JIMSyncRuleMapping
        Get-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$SyncRuleId,

        [Parameter(ParameterSetName = 'Import')]
        [int]$TargetMetaverseAttributeId,

        [Parameter(ParameterSetName = 'Export')]
        [int]$TargetConnectedSystemAttributeId,

        [Parameter(ParameterSetName = 'Import')]
        [int[]]$SourceConnectedSystemAttributeId,

        [Parameter(ParameterSetName = 'Export')]
        [int[]]$SourceMetaverseAttributeId
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Determine direction and validate parameters
        $isImport = $PSBoundParameters.ContainsKey('TargetMetaverseAttributeId')
        $isExport = $PSBoundParameters.ContainsKey('TargetConnectedSystemAttributeId')

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

            if (-not $SourceConnectedSystemAttributeId) {
                Write-Error "-SourceConnectedSystemAttributeId is required for import mappings."
                return
            }

            $order = 0
            foreach ($sourceId in $SourceConnectedSystemAttributeId) {
                $body.sources += @{
                    order = $order
                    connectedSystemAttributeId = $sourceId
                }
                $order++
            }

            $targetDescription = "MV Attribute $TargetMetaverseAttributeId"
        }
        else {
            $body.targetConnectedSystemAttributeId = $TargetConnectedSystemAttributeId

            if (-not $SourceMetaverseAttributeId) {
                Write-Error "-SourceMetaverseAttributeId is required for export mappings."
                return
            }

            $order = 0
            foreach ($sourceId in $SourceMetaverseAttributeId) {
                $body.sources += @{
                    order = $order
                    metaverseAttributeId = $sourceId
                }
                $order++
            }

            $targetDescription = "CS Attribute $TargetConnectedSystemAttributeId"
        }

        if ($PSCmdlet.ShouldProcess("$targetDescription in Sync Rule $SyncRuleId", "Create Mapping")) {
            Write-Verbose "Creating Sync Rule Mapping for Sync Rule: $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/mappings" -Method 'POST' -Body $body

                Write-Verbose "Created Sync Rule Mapping with ID: $($result.id)"

                $result
            }
            catch {
                Write-Error "Failed to create Sync Rule Mapping: $_"
            }
        }
    }
}
