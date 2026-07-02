# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMExampleDataSet {
    <#
    .SYNOPSIS
        Removes an Example Data Set from JIM.

    .DESCRIPTION
        Deletes an Example Data Set. Built-in Example Data Sets cannot be removed.
        This action cannot be undone.

    .PARAMETER Id
        The unique identifier of the Example Data Set to remove.

    .PARAMETER Force
        Bypasses confirmation prompts.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMExampleDataSet -Id 5

        Removes the specified Example Data Set (with confirmation).

    .EXAMPLE
        Remove-JIMExampleDataSet -Id 5 -Force

        Removes the specified Example Data Set without confirmation.

    .LINK
        Get-JIMExampleDataSet
        New-JIMExampleDataSet
        Set-JIMExampleDataSet
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
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

        # Get data set name for confirmation message
        $dataSetName = $Id
        try {
            $dataSet = Invoke-JIMApi -Endpoint "/api/v1/example-data/example-data-sets/$Id"
            $dataSetName = $dataSet.name
        }
        catch {
            # Continue with ID if we can't get the name
        }

        if ($Force -or $PSCmdlet.ShouldProcess($dataSetName, "Remove Example Data Set")) {
            Write-Verbose "Removing Example Data Set: $Id ($dataSetName)"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/example-data/example-data-sets/$Id" -Method 'DELETE'
                Write-Verbose "Removed Example Data Set: $Id"
            }
            catch {
                Write-Error "Failed to remove Example Data Set: $_"
            }
        }
    }
}
