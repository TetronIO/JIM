# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMExampleDataSet {
    <#
    .SYNOPSIS
        Updates an Example Data Set in JIM.

    .DESCRIPTION
        Updates the name, culture, and/or values of an existing Example Data Set.
        Built-in Example Data Sets cannot be updated.

    .PARAMETER Id
        The unique identifier of the Example Data Set to update.

    .PARAMETER Name
        The new name for the Example Data Set.

    .PARAMETER Culture
        The new .NET culture the values are in, e.g. "en-GB".

    .PARAMETER Values
        When specified, replaces the entire set of values.

    .PARAMETER PassThru
        If specified, returns the updated Example Data Set object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Example Data Set object.

    .EXAMPLE
        Set-JIMExampleDataSet -Id 5 -Name "UK Cities (Extended)"

        Renames the Example Data Set.

    .EXAMPLE
        Set-JIMExampleDataSet -Id 5 -Values "London", "Manchester", "Bristol", "Leeds" -PassThru

        Replaces the values in the Example Data Set.

    .LINK
        Get-JIMExampleDataSet
        New-JIMExampleDataSet
        Remove-JIMExampleDataSet
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter()]
        [string]$Name,

        [Parameter()]
        [string]$Culture,

        [Parameter()]
        [string[]]$Values,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess($Id, "Update Example Data Set")) {
            Write-Verbose "Updating Example Data Set: $Id"

            $body = @{}
            if ($Name) { $body.name = $Name }
            if ($Culture) { $body.culture = $Culture }
            if ($PSBoundParameters.ContainsKey('Values')) { $body.values = @($Values) }

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/example-data-sets/$Id" -Method 'PUT' -Body $body
                Write-Verbose "Updated Example Data Set: $Id"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to update Example Data Set: $_"
            }
        }
    }
}
