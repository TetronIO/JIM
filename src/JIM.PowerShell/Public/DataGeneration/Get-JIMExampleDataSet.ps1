function Get-JIMExampleDataSet {
    <#
    .SYNOPSIS
        Gets example data sets from JIM.

    .DESCRIPTION
        Retrieves example data sets that can be used for testing and demonstration
        purposes. These contain pre-defined identity data.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page. Defaults to 100.

    .OUTPUTS
        PSCustomObject representing example data set(s).

    .EXAMPLE
        Get-JIMExampleDataSet

        Gets all example data sets.

    .EXAMPLE
        Get-JIMExampleDataSet | Select-Object Name, Description

        Gets all example data sets with specific properties.

    .LINK
        Get-JIMDataGenerationTemplate
        Invoke-JIMDataGenerationTemplate
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [ValidateRange(1, 1000)]
        [int]$PageSize = 100
    )

    process {
        Write-Verbose "Getting example data sets"

        $queryParams = @(
            "page=$Page",
            "pageSize=$PageSize"
        )
        $queryString = $queryParams -join '&'

        $response = Invoke-JIMApi -Endpoint "/api/v1/data-generation/example-data-sets?$queryString"

        # Handle paginated response
        $dataSets = if ($response.items) { $response.items } else { $response }

        # Output each data set individually for pipeline support
        foreach ($dataSet in $dataSets) {
            $dataSet
        }
    }
}
