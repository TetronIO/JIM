# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMSyncRule {
    <#
    .SYNOPSIS
        Removes a Synchronisation Rule from JIM.

    .DESCRIPTION
        Permanently deletes a Synchronisation Rule.

    .PARAMETER Id
        The unique identifier of the Sync Rule to delete.

    .PARAMETER InputObject
        Sync Rule object to delete (from pipeline).

    .PARAMETER ChangeReason
        An optional reason for the deletion, recorded against the change history.

    .PARAMETER Force
        Suppresses confirmation prompts.

    .PARAMETER PassThru
        If specified, returns the deleted Sync Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the deleted Sync Rule object.

    .EXAMPLE
        Remove-JIMSyncRule -Id 1

        Removes the Sync Rule with ID 1 (prompts for confirmation).

    .EXAMPLE
        Remove-JIMSyncRule -Id 1 -Force -ChangeReason "Decommissioned (CHG0123)"

        Removes the Sync Rule without confirmation and records a reason against the change history.

    .EXAMPLE
        Get-JIMSyncRule | Where-Object { $_.name -like "Test*" } | Remove-JIMSyncRule -Force

        Removes all Sync Rules with names starting with "Test".

    .LINK
        Get-JIMSyncRule
        New-JIMSyncRule
        Set-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$Force,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $ruleId = if ($InputObject) { $InputObject.id } else { $Id }

        # Get the rule first for confirmation message and PassThru
        $existing = $null
        try {
            $existing = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId"
        }
        catch {
            Write-Error "Sync Rule not found: $ruleId"
            return
        }

        if ($Force -or $PSCmdlet.ShouldProcess($existing.name, "Delete Sync Rule")) {
            Write-Verbose "Deleting Sync Rule: $ruleId"

            # The reason is supplied as a query parameter because HTTP DELETE bodies are awkward for clients.
            $deleteEndpoint = "/api/v1/synchronisation/sync-rules/$ruleId"
            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $deleteEndpoint += "?changeReason=$([System.Uri]::EscapeDataString($ChangeReason))"
            }

            try {
                Invoke-JIMApi -Endpoint $deleteEndpoint -Method 'DELETE'

                Write-Verbose "Deleted Sync Rule: $ruleId"

                if ($PassThru) {
                    $existing
                }
            }
            catch {
                Write-Error "Failed to delete Sync Rule: $_"
            }
        }
    }
}
