function Get-JIMConnectedSystemObjectType {
    <#
    .SYNOPSIS
        Gets object types for a Connected System in JIM.

    .DESCRIPTION
        Retrieves object types from a Connected System's discovered schema. Object types
        represent categories of objects in the external identity store (e.g. user, group).
        Each object type contains attributes that can be selected for synchronisation.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .OUTPUTS
        PSCustomObject representing the object types and their attributes.

    .EXAMPLE
        Get-JIMConnectedSystemObjectType -ConnectedSystemId 1

        Gets all object types for Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Corporate*" | ForEach-Object {
            Get-JIMConnectedSystemObjectType -ConnectedSystemId $_.id
        }

        Gets object types for all Connected Systems matching "Corporate*".

    .LINK
        Set-JIMConnectedSystemObjectType
        Set-JIMConnectedSystemAttribute
        Get-JIMConnectedSystem
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        Write-Verbose "Getting object types for Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types"

            # Output each object type individually for pipeline support
            foreach ($objectType in $result) {
                $objectType
            }
        }
        catch {
            Write-Error "Failed to get object types: $_"
        }
    }
}
