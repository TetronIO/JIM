# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Resolve-JIMMetaverseObjectType {
    <#
    .SYNOPSIS
        Resolves a Metaverse Object Type name to its object.

    .DESCRIPTION
        Internal helper function that looks up a Metaverse Object Type by name and returns the object.
        Throws an error if not found or if multiple matches exist.

    .PARAMETER Name
        The name of the Metaverse Object Type to resolve.

    .OUTPUTS
        PSCustomObject representing the Metaverse Object Type.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    Write-Verbose "Resolving Metaverse Object Type name: $Name"

    # The list endpoint is paginated with a server-side default page size, so read every page;
    # a name beyond the first page could otherwise never resolve (#894).
    $objectTypes = Get-JIMPagedItems -Endpoint "/api/v1/metaverse/object-types"

    # Find by name (exact match)
    $matches = @($objectTypes | Where-Object { $_.name -eq $Name })

    if ($matches.Count -eq 0) {
        throw "Metaverse Object Type not found: '$Name'"
    }

    if ($matches.Count -gt 1) {
        throw "Multiple Metaverse Object Types found with name '$Name'. Use -Id to specify the exact type."
    }

    $matches[0]
}
