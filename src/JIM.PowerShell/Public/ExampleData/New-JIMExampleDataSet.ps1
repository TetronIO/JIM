# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMExampleDataSet {
    <#
    .SYNOPSIS
        Creates a new Example Data Set in JIM.

    .DESCRIPTION
        Creates a new Example Data Set: a named pool of string values (e.g. a list of
        cities, or first names) that Data Generation Templates can draw from when
        generating test identity data.

    .PARAMETER Name
        The name for the Example Data Set.

    .PARAMETER Culture
        The .NET culture the values are in, e.g. "en-GB".

    .PARAMETER Values
        The string values that make up this Example Data Set.

    .PARAMETER PassThru
        If specified, returns the created Example Data Set object.

    .OUTPUTS
        If -PassThru is specified, returns the created Example Data Set object.

    .EXAMPLE
        New-JIMExampleDataSet -Name "UK Cities" -Culture "en-GB" -Values "London", "Manchester", "Bristol" -PassThru

        Creates a new Example Data Set of UK city names.

    .LINK
        Get-JIMExampleDataSet
        Set-JIMExampleDataSet
        Remove-JIMExampleDataSet
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
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

        if ($PSCmdlet.ShouldProcess($Name, "Create Example Data Set")) {
            Write-Verbose "Creating Example Data Set: $Name"

            $body = @{
                name    = $Name
                culture = $Culture
            }
            if ($Values) {
                $body.values = @($Values)
            }

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/example-data-sets" -Method 'POST' -Body $body
                Write-Verbose "Created Example Data Set: $($response.id)"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to create Example Data Set: $_"
            }
        }
    }
}
