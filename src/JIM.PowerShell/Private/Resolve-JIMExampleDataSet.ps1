# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Resolve-JIMExampleDataSet {
    <#
    .SYNOPSIS
        Resolves an Example Data Set name to its object.

    .DESCRIPTION
        Internal helper function that looks up an Example Data Set by name and returns the object.
        Throws an error if not found or if multiple matches exist.

    .PARAMETER Name
        The name of the Example Data Set to resolve.

    .OUTPUTS
        PSCustomObject representing the Example Data Set.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    Write-Verbose "Resolving Example Data Set name: $Name"

    $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/example-data-sets"

    # Handle paginated response
    $dataSets = if ($response.items) { $response.items } else { $response }

    # Find by name (exact match)
    $matches = @($dataSets | Where-Object { $_.name -eq $Name })

    if ($matches.Count -eq 0) {
        throw "Example Data Set not found: '$Name'"
    }

    if ($matches.Count -gt 1) {
        throw "Multiple Example Data Sets found with name '$Name'. Use the data set's id to specify the exact set."
    }

    $matches[0]
}
