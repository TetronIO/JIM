# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

        Each partition carries: id, name, externalId, selected, connectedSystemId, containers.

        Each container carries: id, name, externalId, description, hidden, selected, excluded,
        scope, partitionId, connectedSystemId, childContainers, and the object counts read from
        the Connected System when the hierarchy was last retrieved:

          objectCount        How many objects sit directly in this Container.
          subtreeObjectCount That count plus every descendant Container's, which is what a
                             Subtree statement over the Container reaches.

        Both are $null where the Connector cannot report counts, or the hierarchy has not been
        retrieved since counting was introduced. Zero and $null mean different things: zero is a
        Container that was searched and found empty, $null is one nobody has counted.

    .EXAMPLE
        Get-JIMConnectedSystemPartition -ConnectedSystemId 1

        Gets all partitions for Connected System 1.

    .EXAMPLE
        (Get-JIMConnectedSystemPartition -ConnectedSystemId 1).containers |
            Where-Object { $_.objectCount -gt 0 } |
            Select-Object name, objectCount, subtreeObjectCount |
            Sort-Object subtreeObjectCount -Descending

        Lists the Containers holding objects, largest branch first, to decide which are worth
        managing before selecting any of them.

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
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
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
