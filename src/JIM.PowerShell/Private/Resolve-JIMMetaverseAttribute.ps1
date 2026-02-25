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

    $response = Invoke-JIMApi -Endpoint "/api/v1/metaverse/attributes"

    # Handle paginated response
    $attributes = if ($response.items) { $response.items } else { $response }

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
