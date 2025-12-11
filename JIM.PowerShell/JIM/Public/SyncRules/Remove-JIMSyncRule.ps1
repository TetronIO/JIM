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
        Remove-JIMSyncRule -Id 1 -Force

        Removes the Sync Rule without confirmation.

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

        [switch]$Force,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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

            try {
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId" -Method 'DELETE'

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
