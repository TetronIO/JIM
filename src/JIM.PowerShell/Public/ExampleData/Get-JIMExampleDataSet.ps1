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
        The Example Data Set to get, with its values.

    .PARAMETER Page
        The page of Example Data Sets to return.

    .PARAMETER PageSize
        How many Example Data Sets per page.

    .OUTPUTS
        A list returns each Example Data Set's header, and -Id the Example Data Set with its values.

    .EXAMPLE
        Get-JIMExampleDataSet

        Gets all example data sets.

    .EXAMPLE
        Get-JIMExampleDataSet -Id 5

        Gets the Example Data Set with ID 5, including its values.

    .EXAMPLE
        Get-JIMExampleDataSet | Select-Object Name, Culture, ValueCount

        Gets all example data sets with specific properties.

    .LINK
        New-JIMExampleDataSet
        Set-JIMExampleDataSet
        Remove-JIMExampleDataSet
        Get-JIMExampleDataTemplate
        Invoke-JIMExampleDataTemplate
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.ExampleDataSetHeader', ParameterSetName = 'List')]
    [OutputType('JIM.ExampleDataSetDto', ParameterSetName = 'ById')]
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
