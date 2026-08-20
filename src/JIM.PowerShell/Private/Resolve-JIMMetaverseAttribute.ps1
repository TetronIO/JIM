# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Resolve-JIMMetaverseAttribute {
    <#
    .SYNOPSIS
        Resolves a Metaverse Attribute name to its object.

    .DESCRIPTION
        Internal helper function that looks up a Metaverse Attribute by name and returns the object.
        Throws an error if not found or if multiple matches exist.

    .PARAMETER Name
        The name of the Metaverse Attribute to resolve.

    .OUTPUTS
        PSCustomObject representing the Metaverse Attribute.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    Write-Verbose "Resolving Metaverse Attribute name: $Name"

    # The list endpoint is paginated with a server-side default page size, so read every page;
    # a name beyond the first page could otherwise never resolve (#894).
    $attributes = Get-JIMPagedItems -Endpoint "/api/v1/metaverse/attributes"

    # Find by name (exact match)
    $matches = @($attributes | Where-Object { $_.name -eq $Name })

    if ($matches.Count -eq 0) {
        throw "Metaverse Attribute not found: '$Name'"
    }

    if ($matches.Count -gt 1) {
        throw "Multiple Metaverse Attributes found with name '$Name'. Use -Id to specify the exact attribute."
    }

    $matches[0]
}
