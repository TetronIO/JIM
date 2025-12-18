function Get-JIMConnectedSystemPartition {
    <#
    .SYNOPSIS
        Gets partitions for a Connected System in JIM.

    .DESCRIPTION
        Retrieves partitions from a Connected System. Partitions represent logical divisions
        within a connected system (e.g., LDAP naming contexts). Each partition contains
        containers that can be selected for import operations.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .OUTPUTS
        PSCustomObject representing the partitions with their containers.

    .EXAMPLE
        Get-JIMConnectedSystemPartition -ConnectedSystemId 1

        Gets all partitions for Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Samba AD*" | ForEach-Object {
            Get-JIMConnectedSystemPartition -ConnectedSystemId $_.id
        }

        Gets partitions for all Connected Systems matching "Samba AD*".

    .LINK
        Set-JIMConnectedSystemPartition
        Set-JIMConnectedSystemContainer
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

        Write-Verbose "Getting partitions for Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/partitions"

            # Output each partition individually for pipeline support
            foreach ($partition in $result) {
                $partition
            }
        }
        catch {
            Write-Error "Failed to get partitions: $_"
        }
    }
}
