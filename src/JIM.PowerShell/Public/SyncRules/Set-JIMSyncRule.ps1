# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMSyncRule {
    <#
    .SYNOPSIS
        Updates an existing Synchronisation Rule in JIM.

    .DESCRIPTION
        Updates the properties of an existing Sync Rule.
        Only the parameters provided will be updated.

    .PARAMETER Id
        The unique identifier of the Sync Rule to update.

    .PARAMETER InputObject
        Sync Rule object to update (from pipeline).

    .PARAMETER Name
        The new name for the Sync Rule.

    .PARAMETER Enable
        Enables the Sync Rule.

    .PARAMETER Disable
        Disables the Sync Rule.

    .PARAMETER ProjectToMetaverse
        For Import rules, sets whether objects will be projected to the Metaverse.

    .PARAMETER ProvisionToConnectedSystem
        For Export rules, sets whether objects will be provisioned to the Connected System.

    .PARAMETER InboundOutOfScopeAction
        For Import rules: action to take when a CSO falls out of this rule's scope.
        Valid values:
          - Disconnect: break the CSO -> MVO join. Whether attributes contributed by
            this connected system are also recalled from the MVO depends on the CSO
            type's RemoveContributedAttributesOnObsoletion flag, the MVO type's
            deletion grace period, and whether the MVO is slated for immediate
            deletion.
          - RemainJoined: keep the join intact and stop further attribute flow.

    .PARAMETER OutboundDeprovisionAction
        For Export rules: action to take when an MVO falls out of this rule's scope.
        Valid values: Disconnect (break the join, leave the CSO untouched in the target system),
        Delete (queue a delete PendingExport so the CSO is removed from the target system).

    .PARAMETER EnforceState
        For Export rules: whether inbound changes from the target system trigger re-evaluation
        of this rule to detect and remediate drift.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Synchronisation Rule's change history.

    .PARAMETER PassThru
        If specified, returns the updated Sync Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Sync Rule object.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Name "Updated Rule Name"

        Updates the name of the Sync Rule with ID 1.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Disable

        Disables the Sync Rule with ID 1.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Enable -ChangeReason "Re-enabled after maintenance (CHG0042)" -PassThru

        Enables the Sync Rule, records a reason against its change history, and returns the updated object.

    .EXAMPLE
        Get-JIMSyncRule -Id 1 | Set-JIMSyncRule -ProjectToMetaverse $true

        Updates a Sync Rule from the pipeline to enable projection.

    .LINK
        Get-JIMSyncRule
        New-JIMSyncRule
        Remove-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'Enable', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'Disable', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'Enable')]
        [switch]$Enable,

        [Parameter(Mandatory, ParameterSetName = 'Disable')]
        [switch]$Disable,

        [Parameter()]
        [bool]$ProjectToMetaverse,

        [Parameter()]
        [bool]$ProvisionToConnectedSystem,

        [Parameter()]
        [ValidateSet('Disconnect', 'RemainJoined')]
        [string]$InboundOutOfScopeAction,

        [Parameter()]
        [ValidateSet('Disconnect', 'Delete')]
        [string]$OutboundDeprovisionAction,

        [Parameter()]
        [bool]$EnforceState,

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

        $ruleId = if ($InputObject) { $InputObject.id } else { $Id }

        # Build update body
        $body = @{}

        if ($Name) {
            $body.name = $Name
        }

        if ($Enable) {
            $body.enabled = $true
        }
        elseif ($Disable) {
            $body.enabled = $false
        }

        if ($PSBoundParameters.ContainsKey('ProjectToMetaverse')) {
            $body.projectToMetaverse = $ProjectToMetaverse
        }

        if ($PSBoundParameters.ContainsKey('ProvisionToConnectedSystem')) {
            $body.provisionToConnectedSystem = $ProvisionToConnectedSystem
        }

        if ($PSBoundParameters.ContainsKey('InboundOutOfScopeAction')) {
            $body.inboundOutOfScopeAction = $InboundOutOfScopeAction
        }

        if ($PSBoundParameters.ContainsKey('OutboundDeprovisionAction')) {
            $body.outboundDeprovisionAction = $OutboundDeprovisionAction
        }

        if ($PSBoundParameters.ContainsKey('EnforceState')) {
            $body.enforceState = $EnforceState
        }

        # A change reason alone is not an update; require at least one actual property change first.
        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSBoundParameters.ContainsKey('ChangeReason')) {
            $body.changeReason = $ChangeReason
        }

        $displayName = $Name ?? $ruleId

        if ($PSCmdlet.ShouldProcess($displayName, "Update Sync Rule")) {
            Write-Verbose "Updating Sync Rule: $ruleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Sync Rule: $ruleId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Sync Rule: $_"
            }
        }
    }
}
