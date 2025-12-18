function Set-JIMMetaverseObjectType {
    <#
    .SYNOPSIS
        Updates a Metaverse Object Type's deletion rules in JIM.

    .DESCRIPTION
        Updates the deletion rule settings for an existing Metaverse Object Type.
        This controls how and when Metaverse Objects of this type are automatically deleted.

    .PARAMETER Id
        The unique identifier of the Object Type to update.

    .PARAMETER Name
        The name of a specific Object Type to update.

    .PARAMETER InputObject
        Object Type object to update (from pipeline).

    .PARAMETER DeletionRule
        The deletion rule for objects of this type.
        - Manual: Objects are never automatically deleted
        - WhenLastConnectorDisconnected: Objects are deleted when all connectors are removed

    .PARAMETER DeletionGracePeriodDays
        Number of days to wait after deletion conditions are met before deleting.
        Set to 0 for immediate deletion when conditions are met.

    .PARAMETER DeletionTriggerConnectedSystemIds
        Array of Connected System IDs that trigger deletion when disconnected.
        When set, the MVO is deleted if ANY of these systems disconnect.
        When empty, the MVO is only deleted when ALL connectors disconnect.

    .PARAMETER PassThru
        If specified, returns the updated Object Type object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Object Type object.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected -DeletionGracePeriodDays 30

        Configures User type to delete 30 days after last connector disconnects.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Name 'User' -DeletionGracePeriodDays 0

        Configures immediate deletion for User type when connectors disconnect.

    .EXAMPLE
        Get-JIMMetaverseObjectType -Name 'User' | Set-JIMMetaverseObjectType -DeletionGracePeriodDays 7 -PassThru

        Updates from pipeline and returns the updated object.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 1 -DeletionTriggerConnectedSystemIds 1,2

        Configure deletion to trigger when HR system (ID 1) or AD system (ID 2) disconnects.

    .LINK
        Get-JIMMetaverseObjectType
        Get-JIMMetaverseObject
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [ValidateSet('Manual', 'WhenLastConnectorDisconnected')]
        [string]$DeletionRule,

        [Parameter()]
        [ValidateRange(0, 3650)]
        [int]$DeletionGracePeriodDays,

        [Parameter()]
        [int[]]$DeletionTriggerConnectedSystemIds,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Resolve name to ID if using ByName parameter set
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $resolvedType = Resolve-JIMMetaverseObjectType -Name $Name
                $Id = $resolvedType.id
            }
            catch {
                Write-Error $_
                return
            }
        }
        elseif ($InputObject) {
            $Id = $InputObject.id
        }

        # Map deletion rule string to enum integer value (MetaverseObjectDeletionRule enum)
        $deletionRuleMap = @{
            'Manual'                        = 0
            'WhenLastConnectorDisconnected' = 1
        }

        # Build update body
        $body = @{}

        if ($DeletionRule) {
            $body.deletionRule = $deletionRuleMap[$DeletionRule]
        }

        if ($PSBoundParameters.ContainsKey('DeletionGracePeriodDays')) {
            $body.deletionGracePeriodDays = $DeletionGracePeriodDays
        }

        if ($PSBoundParameters.ContainsKey('DeletionTriggerConnectedSystemIds')) {
            $body.deletionTriggerConnectedSystemIds = $DeletionTriggerConnectedSystemIds
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        $displayName = $Name ?? "ID $Id"

        if ($PSCmdlet.ShouldProcess($displayName, "Update Metaverse Object Type deletion rules")) {
            Write-Verbose "Updating Metaverse Object Type: $Id"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/object-types/$Id" -Method 'PUT' -Body $body

                Write-Verbose "Updated Metaverse Object Type: $Id"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Metaverse Object Type: $_"
            }
        }
    }
}
