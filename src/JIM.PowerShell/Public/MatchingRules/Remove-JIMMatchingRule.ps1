# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMMatchingRule {
    <#
    .SYNOPSIS
        Deletes an Object Matching Rule from a Connected System Object Type (simple mode).

    .DESCRIPTION
        Deletes an Object Matching Rule from a Connected System Object Type.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER Id
        The ID of the rule to delete.

    .PARAMETER Force
        Suppresses the confirmation prompt.

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
    [OutputType([void])]
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
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $shouldProcess = Confirm-JIMAction -Cmdlet $PSCmdlet -Target "Matching Rule $Id" -Action "Remove" -Force:$Force

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
