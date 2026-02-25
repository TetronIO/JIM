function Resolve-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Resolves a Connected System name to its ID.

    .DESCRIPTION
        Internal helper function to resolve a Connected System name to its ID.
        Throws an error if no matching system is found or if multiple matches exist.

    .PARAMETER Name
        The name of the Connected System to resolve. Must be an exact match.

    .OUTPUTS
        The Connected System object including its ID.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    Write-Verbose "Resolving Connected System name: $Name"

    $response = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems"
    $systems = if ($response.items) { $response.items } else { $response }

    # Exact match only for resolution
    $matches = @($systems | Where-Object { $_.name -eq $Name })

    if ($matches.Count -eq 0) {
        throw "Connected System not found: '$Name'"
    }

    if ($matches.Count -gt 1) {
        throw "Multiple Connected Systems found with name '$Name'. Use -ConnectedSystemId to specify the exact system."
    }

    $matches[0]
}
