# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMSyncRule {
    <#
    .SYNOPSIS
        Creates a new Synchronisation Rule in JIM.

    .DESCRIPTION
        Creates a Synchronisation Rule that defines how data flows between a Connected System and the Metaverse.
        For Import rules, set -ProjectToMetaverse to create Metaverse objects from imported data.
        For Export rules, set -ProvisionToConnectedSystem to create Connected System objects.

    .PARAMETER Name
        The name for the Synchronisation Rule.

    .PARAMETER Description
        An optional description of what the Synchronisation Rule does.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System this rule applies to.

    .PARAMETER ConnectedSystemName
        The name of the Connected System this rule applies to. Must be an exact match.

    .PARAMETER ConnectedSystemObjectTypeId
        The ID of the Connected System Object Type.

    .PARAMETER MetaverseObjectTypeId
        The ID of the Metaverse Object Type.

    .PARAMETER Direction
        The direction of the Synchronisation Rule: Import (from Connected System to Metaverse) or
        Export (from Metaverse to Connected System).

    .PARAMETER ProjectToMetaverse
        For Import rules, if specified, objects will be projected (created) in the Metaverse.

    .PARAMETER ProvisionToConnectedSystem
        For Export rules, if specified, objects will be provisioned (created) in the Connected System.

    .PARAMETER Enabled
        Whether the Synchronisation Rule is enabled. Defaults to $true.

    .PARAMETER OutboundDeprovisionAction
        For Export rules: action to take when an MVO falls out of this rule's scope or is deleted.
        Valid values: Disconnect (break the join, leave the CSO untouched in the target system),
        Delete (queue a delete PendingExport so the CSO is removed from the target system).
        Defaults to Disconnect when not specified.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Synchronisation Rule's change history.

    .PARAMETER PassThru
        If specified, returns the created Synchronisation Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the created Synchronisation Rule object.

    .EXAMPLE
        New-JIMSyncRule -Name "Import Users" -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -ProjectToMetaverse

        Creates an import Synchronisation Rule that projects users to the Metaverse.

    .EXAMPLE
        New-JIMSyncRule -Name "Import Users" -ConnectedSystemName 'Contoso AD' -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -ProjectToMetaverse

        Creates an import Synchronisation Rule using the Connected System name.

    .EXAMPLE
        New-JIMSyncRule -Name "Export Users to AD" -ConnectedSystemId 2 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Export -ProvisionToConnectedSystem -PassThru

        Creates an export Synchronisation Rule that provisions users to the Connected System.

    .EXAMPLE
        New-JIMSyncRule -Name "Import Users" -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -Description "Imports user accounts from the HR system"

        Creates an import Synchronisation Rule with a description of what the rule does.

    .EXAMPLE
        New-JIMSyncRule -Name "Export Users to AD" -ConnectedSystemId 2 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Export -ProvisionToConnectedSystem -OutboundDeprovisionAction Delete

        Creates an export Synchronisation Rule that provisions users and deletes the corresponding objects from the Connected System when their Metaverse Objects are deleted or fall out of scope.

    .LINK
        Get-JIMSyncRule
        Set-JIMSyncRule
        Remove-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter()]
        [string]$Description,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$ConnectedSystemName,

        [Parameter(Mandatory)]
        [int]$ConnectedSystemObjectTypeId,

        [Parameter(Mandatory)]
        [int]$MetaverseObjectTypeId,

        [Parameter(Mandatory)]
        [ValidateSet('Import', 'Export')]
        [string]$Direction,

        [switch]$ProjectToMetaverse,

        [switch]$ProvisionToConnectedSystem,

        [bool]$Enabled = $true,

        [Parameter()]
        [ValidateSet('Disconnect', 'Delete')]
        [string]$OutboundDeprovisionAction,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Resolve ConnectedSystemName to ConnectedSystemId if specified
        if ($PSBoundParameters.ContainsKey('ConnectedSystemName')) {
            $connectedSystem = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
            $ConnectedSystemId = $connectedSystem.id
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Synchronisation Rule")) {
            Write-Verbose "Creating Synchronisation Rule: $Name"

            # Map direction string to API enum value
            $directionValue = switch ($Direction) {
                'Import' { 1 }
                'Export' { 2 }
            }

            $body = @{
                name = $Name
                connectedSystemId = $ConnectedSystemId
                connectedSystemObjectTypeId = $ConnectedSystemObjectTypeId
                metaverseObjectTypeId = $MetaverseObjectTypeId
                direction = $directionValue
                enabled = $Enabled
            }

            if ($PSBoundParameters.ContainsKey('Description')) {
                $body.description = $Description
            }

            if ($ProjectToMetaverse) {
                $body.projectToMetaverse = $true
            }

            if ($ProvisionToConnectedSystem) {
                $body.provisionToConnectedSystem = $true
            }

            if ($PSBoundParameters.ContainsKey('OutboundDeprovisionAction')) {
                $body.outboundDeprovisionAction = $OutboundDeprovisionAction
            }

            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $body.changeReason = $ChangeReason
            }

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules" -Method 'POST' -Body $body

                Write-Verbose "Created Synchronisation Rule: $($result.id) ($($result.name))"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to create Synchronisation Rule: $_"
            }
        }
    }
}
