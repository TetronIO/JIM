function Get-JIMHistoryCount {
    <#
    .SYNOPSIS
        Gets the count of change history records for a connected system.

    .DESCRIPTION
        Retrieves the number of CSO change records stored for a specific connected system.
        This can help assess how much history will be affected by deleting a connected system
        or clearing its connector space.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ConnectedSystemName
        The name of the Connected System. Must be an exact match.

    .OUTPUTS
        PSCustomObject containing:
        - connectedSystemId: The connected system ID
        - connectedSystemName: The connected system name
        - changeRecordCount: Number of CSO change records

    .EXAMPLE
        Get-JIMHistoryCount -ConnectedSystemId 1

        Gets the change record count for connected system ID 1.

    .EXAMPLE
        Get-JIMHistoryCount -ConnectedSystemName 'Contoso AD'

        Gets the change record count for the 'Contoso AD' connected system.

    .EXAMPLE
        Get-JIMConnectedSystem | ForEach-Object {
            Get-JIMHistoryCount -ConnectedSystemId $_.id
        } | Format-Table

        Gets change record counts for all connected systems and displays as a table.

    .LINK
        Get-JIMConnectedSystem
        Invoke-JIMHistoryCleanup
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$ConnectedSystemName
    )

    process {
        # If using name, resolve to ID first
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            Write-Verbose "Looking up Connected System by name: $ConnectedSystemName"
            $systems = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems"
            $matchingSystem = $systems | Where-Object { $_.name -eq $ConnectedSystemName }

            if ($matchingSystem.Count -eq 0) {
                Write-Error "Connected System '$ConnectedSystemName' not found."
                return
            }

            if ($matchingSystem.Count -gt 1) {
                Write-Error "Multiple Connected Systems found with name '$ConnectedSystemName'. Use -ConnectedSystemId to specify the exact system."
                return
            }

            $ConnectedSystemId = $matchingSystem[0].id
        }

        Write-Verbose "Getting history count for Connected System ID: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/history/connected-systems/$ConnectedSystemId/count"
            $result
        }
        catch {
            Write-Error "Failed to get history count for Connected System ID ${ConnectedSystemId}: $_"
            throw
        }
    }
}
