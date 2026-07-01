# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMExampleDataSet {
    <#
    .SYNOPSIS
        Gets example data sets from JIM.

    .DESCRIPTION
        Retrieves example data sets that can be used for testing and demonstration
        purposes. These contain pre-defined identity data.

    .PARAMETER Id
        The unique identifier of a specific Example Data Set to retrieve, including its values.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1. Not applicable when -Id is specified.

    .PARAMETER PageSize
        Number of items per page. Defaults to 100. Not applicable when -Id is specified.

    .OUTPUTS
        PSCustomObject representing example data set(s).

    .EXAMPLE
        Get-JIMExampleDataSet

        Gets all example data sets.

    .EXAMPLE
        Get-JIMExampleDataSet -Id 5

        Gets the Example Data Set with ID 5, including its values.

    .EXAMPLE
        Get-JIMExampleDataSet | Select-Object Name, Description

        Gets all example data sets with specific properties.

    .LINK
        New-JIMExampleDataSet
        Set-JIMExampleDataSet
        Remove-JIMExampleDataSet
        Get-JIMExampleDataTemplate
        Invoke-JIMExampleDataTemplate
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 1000)]
        [int]$PageSize = 100
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'ById') {
            Write-Verbose "Getting example data set $Id"
            Invoke-JIMApi -Endpoint "/api/v1/example-data/example-data-sets/$Id"
            return
        }

        Write-Verbose "Getting example data sets"

        $queryParams = @(
            "page=$Page",
            "pageSize=$PageSize"
        )
        $queryString = $queryParams -join '&'

        $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/example-data-sets?$queryString"

        # Handle paginated response
        $dataSets = if ($response.items) { $response.items } else { $response }

        # Output each data set individually for pipeline support
        foreach ($dataSet in $dataSets) {
            $dataSet
        }
    }
}
