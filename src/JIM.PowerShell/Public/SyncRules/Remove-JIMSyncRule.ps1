# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMSyncRule {
    <#
    .SYNOPSIS
        Removes a Synchronisation Rule from JIM.

    .DESCRIPTION
        Permanently deletes a Synchronisation Rule.

    .PARAMETER Id
        The unique identifier of the Synchronisation Rule to delete.

    .PARAMETER InputObject
        Synchronisation Rule object to delete (from pipeline).

    .PARAMETER ChangeReason
        An optional reason for the deletion, recorded against the change history.

    .PARAMETER Force
        Suppresses confirmation prompts.

    .PARAMETER PassThru
        If specified, returns the deleted Synchronisation Rule object.

    .OUTPUTS
        If -PassThru is specified, returns the deleted Synchronisation Rule object.

    .EXAMPLE
        Remove-JIMSyncRule -Id 1

        Removes the Synchronisation Rule with ID 1 (prompts for confirmation).

    .EXAMPLE
        Remove-JIMSyncRule -Id 1 -Force -ChangeReason "Decommissioned (CHG0123)"

        Removes the Synchronisation Rule without confirmation and records a reason against the change history.

    .EXAMPLE
        Get-JIMSyncRule | Where-Object { $_.name -like "Test*" } | Remove-JIMSyncRule -Force

        Removes all Synchronisation Rules with names starting with "Test".

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
            Write-Error "Synchronisation Rule not found: $ruleId"
            return
        }

        if ($Force -or $PSCmdlet.ShouldProcess($existing.name, "Delete Synchronisation Rule")) {
            Write-Verbose "Deleting Synchronisation Rule: $ruleId"

            # The reason is supplied as a query parameter because HTTP DELETE bodies are awkward for clients.
            $deleteEndpoint = "/api/v1/synchronisation/sync-rules/$ruleId"
            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $deleteEndpoint += "?changeReason=$([System.Uri]::EscapeDataString($ChangeReason))"
            }

            try {
                Invoke-JIMApi -Endpoint $deleteEndpoint -Method 'DELETE'

                Write-Verbose "Deleted Synchronisation Rule: $ruleId"

                if ($PassThru) {
                    $existing
                }
            }
            catch {
                Write-Error "Failed to delete Synchronisation Rule: $_"
            }
        }
    }
}
