# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMSyncRule {
    <#
    .SYNOPSIS
        Changes a Synchronisation Rule.

    .DESCRIPTION
        Changes a Synchronisation Rule. What you leave out stays as it is.

        Some changes go ahead with a warning rather than being refused. Disabling a rule that holds the last enabled
        contributor of a Metaverse attribute that a Metaverse-Derived Attribute Flow reads writes a warning naming
        each such flow. Taking the rule into projecting into a Metaverse Object Type deleted When Authoritative
        Source Disconnected, from a Connected System that is not one of its authoritative sources, writes a warning
        that objects only this system holds will never be deleted. Any other warning the save raises about the
        rule's Attribute Flows is written too.

    .PARAMETER Id
        The ID of the Synchronisation Rule to change.

    .PARAMETER InputObject
        A Synchronisation Rule from Get-JIMSyncRule, through the pipeline.

    .PARAMETER Name
        The new name of the Synchronisation Rule.

    .PARAMETER Description
        What the Synchronisation Rule is for, for administrators. $null or an empty string clears it.

    .PARAMETER Enable
        Enables the rule, so it takes part in synchronisation.

    .PARAMETER Disable
        Disables the rule, so synchronisation skips it.

    .PARAMETER ProjectToMetaverse
        On an import rule, whether it creates a Metaverse Object for each Connected System Object in scope that
        matches none.

    .PARAMETER ProvisionToConnectedSystem
        On an export rule, whether it creates a Connected System Object for each Metaverse Object in scope that has
        none.

    .PARAMETER InboundOutOfScopeAction
        On an import rule, what happens when a Connected System Object leaves the rule's scope. Disconnect breaks
        the join; whether the values it contributed are recalled as well depends on the Connected System Object
        Type's settings and the Metaverse Object Type's deletion settings. RemainJoined keeps the join and stops the
        values flowing.

    .PARAMETER OutboundDeprovisionAction
        On an export rule, what happens to a Connected System Object when its Metaverse Object leaves the rule's
        scope or is deleted: Disconnect breaks the join and leaves the object in the Connected System, and Delete
        removes it from there.

    .PARAMETER EnforceState
        On an export rule, whether a change imported from the Connected System re-evaluates the rule, so the value
        JIM exported is put back when the target drifts from it.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Synchronisation Rule's change history.

    .PARAMETER PreviewActivityId
        The Configuration Change Preview this change was made after reading, as returned by
        New-JIMConfigurationChangePreview -SyncRuleId. Recorded on the change's Activity so
        "previewed, then applied" is auditable rather than a claim.

    .PARAMETER SaveAnyway
        Saves even though the change stops configuration test cases passing. Without it, or
        -AcceptNewResult, such a change is refused, nothing is saved, and the error names each case.
        Configuration History records the cases saved past, by name.

    .PARAMETER AcceptNewResult
        Takes what the configuration now gives as what each configuration test case that stopped
        passing expects, rewriting those cases, and saves. A case whose new result cannot be
        expected (it needs review, could not run, or its Scope cannot be determined) still refuses
        the change unless -SaveAnyway is given too.

    .PARAMETER PassThru
        Returns the rule as changed.

    .OUTPUTS
        With -PassThru, the rule as changed; otherwise nothing. Its warnings are written to the warning stream
        either way.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Name "Updated Rule Name"

        Updates the name of the Synchronisation Rule with ID 1.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Description "Imports user accounts from the HR system"

        Updates the description of the Synchronisation Rule with ID 1. Pass -Description '' to clear it.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Disable

        Disables the Synchronisation Rule with ID 1.

    .EXAMPLE
        Set-JIMSyncRule -Id 1 -Enable -ChangeReason "Re-enabled after maintenance (CHG0042)" -PassThru

        Enables the Synchronisation Rule, records a reason against its change history, and returns the updated object.

    .EXAMPLE
        Get-JIMSyncRule -Id 1 | Set-JIMSyncRule -ProjectToMetaverse $true

        Updates a Synchronisation Rule from the pipeline to enable projection.

    .LINK
        Get-JIMSyncRule
        New-JIMSyncRule
        Remove-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.SyncRuleSaveResponse')]
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

        [Parameter()]
        [string]$Description,

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

        [Parameter()]
        [guid]$PreviewActivityId,

        [switch]$SaveAnyway,

        [switch]$AcceptNewResult,

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

        if ($PSBoundParameters.ContainsKey('Description')) {
            $body.description = $Description
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

        if ($PSBoundParameters.ContainsKey('PreviewActivityId')) {
            $body.previewActivityId = $PreviewActivityId
        }

        $displayName = $Name ?? $ruleId

        if ($PSCmdlet.ShouldProcess($displayName, "Update Synchronisation Rule")) {
            Write-Verbose "Updating Synchronisation Rule: $ruleId"

            try {
                # The save gate (#2055 Phase 4b): what to do if the change stops configuration test cases passing.
                if ($SaveAnyway) { $body.saveAnyway = $true }
                if ($AcceptNewResult) { $body.acceptNewResults = $true }
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Synchronisation Rule: $ruleId"

                # Non-blocking warnings the save raised (#1750); the save went ahead, and each is written as the
                # server phrased it.
                foreach ($saveWarning in @($result.Warnings | Where-Object { $_ })) {
                    Write-Warning $saveWarning
                }

                # Derived flows the update left with a missing input (#1750, FR 3): reported, never blocking.
                Write-JIMDependentDerivedFlowWarning -DependentDerivedFlows $result.DependentDerivedFlows

                # The update took the rule into projecting from a system that is not an authoritative source of the
                # type (#1256): reported, never blocking.
                if ($result.deletionSourceWarning) {
                    Write-Warning $result.deletionSourceWarning
                }

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Synchronisation Rule: $_"
            }
        }
    }
}
