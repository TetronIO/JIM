function Remove-JIMMatchingRule {
    <#
    .SYNOPSIS
        Removes an Object Matching Rule from JIM.

    .DESCRIPTION
        Deletes an Object Matching Rule from a Connected System.
        This operation cannot be undone.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER Id
        The unique identifier of the Matching Rule to delete.

    .PARAMETER Force
        Skips the confirmation prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMMatchingRule -ConnectedSystemId 1 -Id 5

        Removes Matching Rule 5 from Connected System 1 (with confirmation).

    .EXAMPLE
        Remove-JIMMatchingRule -ConnectedSystemId 1 -Id 5 -Force

        Removes Matching Rule 5 without confirmation.

    .EXAMPLE
        Get-JIMMatchingRule -ConnectedSystemId 1 -ObjectTypeId 10 | Remove-JIMMatchingRule -Force

        Removes all Matching Rules for Object Type 10 without confirmation.

    .LINK
        Get-JIMMatchingRule
        New-JIMMatchingRule
        Set-JIMMatchingRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        $shouldProcess = $Force -or $PSCmdlet.ShouldProcess("Matching Rule $Id", "Remove")

        if ($shouldProcess) {
            Write-Verbose "Removing Matching Rule ID: $Id from Connected System ID: $ConnectedSystemId"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-rules/$Id" -Method 'DELETE'

                Write-Verbose "Removed Matching Rule ID: $Id"
            }
            catch {
                Write-Error "Failed to remove Matching Rule: $_"
            }
        }
    }
}
