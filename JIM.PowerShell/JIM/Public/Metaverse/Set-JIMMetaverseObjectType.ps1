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
        - WhenAuthoritativeSourceDisconnected: Objects are deleted when any authoritative source disconnects (requires DeletionTriggerConnectedSystemIds)

    .PARAMETER DeletionGracePeriod
        Grace period before deletion is executed, as a TimeSpan.
        Examples: [TimeSpan]::FromMinutes(1), [TimeSpan]::FromDays(30), [TimeSpan]::FromHours(2)
        Set to [TimeSpan]::Zero or omit for immediate deletion when conditions are met.

    .PARAMETER DeletionTriggerConnectedSystemIds
        Array of Connected System IDs that are authoritative sources for deletion.
        Required when DeletionRule is WhenAuthoritativeSourceDisconnected.
        When set, the MVO is deleted if ANY of these systems disconnect.
        Ignored when DeletionRule is Manual or WhenLastConnectorDisconnected.

    .PARAMETER PassThru
        If specified, returns the updated Object Type object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Object Type object.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected -DeletionGracePeriod ([TimeSpan]::FromDays(30))

        Configures User type to delete 30 days after last connector disconnects.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Name 'User' -DeletionGracePeriod ([TimeSpan]::Zero)

        Configures immediate deletion for User type when connectors disconnect.

    .EXAMPLE
        Get-JIMMetaverseObjectType -Name 'User' | Set-JIMMetaverseObjectType -DeletionGracePeriod ([TimeSpan]::FromDays(7)) -PassThru

        Updates from pipeline and returns the updated object.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 1 -DeletionGracePeriod ([TimeSpan]::FromMinutes(1))

        Configures a 1-minute grace period (useful for testing).

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenAuthoritativeSourceDisconnected -DeletionTriggerConnectedSystemIds 1,2

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
        [ValidateSet('Manual', 'WhenLastConnectorDisconnected', 'WhenAuthoritativeSourceDisconnected')]
        [string]$DeletionRule,

        [Parameter()]
        [TimeSpan]$DeletionGracePeriod,

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
            'Manual'                              = 0
            'WhenLastConnectorDisconnected'       = 1
            'WhenAuthoritativeSourceDisconnected' = 2
        }

        # Build update body
        $body = @{}

        if ($DeletionRule) {
            $body.deletionRule = $deletionRuleMap[$DeletionRule]
        }

        if ($PSBoundParameters.ContainsKey('DeletionGracePeriod')) {
            # API expects TimeSpan as "d.hh:mm:ss" string format
            $body.deletionGracePeriod = $DeletionGracePeriod.ToString()
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
