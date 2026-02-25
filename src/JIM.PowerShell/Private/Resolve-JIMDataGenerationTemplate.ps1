function Resolve-JIMDataGenerationTemplate {
    <#
    .SYNOPSIS
        Resolves a Data Generation Template name to its object.

    .DESCRIPTION
        Internal helper function that looks up a Data Generation Template by name and returns the object.
        Throws an error if not found or if multiple matches exist.

    .PARAMETER Name
        The name of the Data Generation Template to resolve.

    .OUTPUTS
        PSCustomObject representing the Data Generation Template.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    Write-Verbose "Resolving Data Generation Template name: $Name"

    $response = Invoke-JIMApi -Endpoint "/api/v1/data-generation/templates"

    # Handle paginated response
    $templates = if ($response.items) { $response.items } else { $response }

    # Find by name (exact match)
    $matches = @($templates | Where-Object { $_.name -eq $Name })

    if ($matches.Count -eq 0) {
        throw "Data Generation Template not found: '$Name'"
    }

    if ($matches.Count -gt 1) {
        throw "Multiple Data Generation Templates found with name '$Name'. Use -Id to specify the exact template."
    }

    $matches[0]
}
