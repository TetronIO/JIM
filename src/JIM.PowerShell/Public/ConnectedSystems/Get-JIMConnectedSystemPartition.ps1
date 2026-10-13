# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemPartition {
    <#
    .SYNOPSIS
        Gets a Connected System's partitions and containers.

    .DESCRIPTION
        Gets a Connected System's partitions, such as an LDAP directory's naming contexts, each with its containers.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .OUTPUTS
        The partitions, each with its tree of containers. Every container carries the same properties as the
        containers Set-JIMConnectedSystemContainer returns. A container's ObjectCount of 0 means it was searched and
        found empty; $null means nobody has counted it.

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
    [OutputType('JIM.ConnectedSystemPartitionDto')]
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
