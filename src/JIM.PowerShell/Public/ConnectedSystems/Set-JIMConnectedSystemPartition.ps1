# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemPartition {
    <#
    .SYNOPSIS
        Chooses whether JIM imports from a partition.

    .DESCRIPTION
        Chooses whether JIM imports objects from a partition of a Connected System. Objects are imported from the
        selected containers of a selected partition.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER PartitionId
        The ID of the partition to change.

    .PARAMETER Selected
        Whether JIM imports objects from the partition.

    .PARAMETER PassThru
        Returns the partition as changed.

    .OUTPUTS
        With -PassThru, the partition as changed; otherwise nothing.

    .EXAMPLE
        Set-JIMConnectedSystemPartition -ConnectedSystemId 1 -PartitionId 5 -Selected $true

        Selects the partition for import operations.

    .EXAMPLE
        Get-JIMConnectedSystemPartition -ConnectedSystemId 1 |
            Where-Object { $_.name -eq "DC=example,DC=com" } |
            ForEach-Object { Set-JIMConnectedSystemPartition -ConnectedSystemId 1 -PartitionId $_.id -Selected $true }

        Selects a specific partition by name.

    .EXAMPLE
        Get-JIMConnectedSystemPartition -ConnectedSystemId 1 |
            ForEach-Object { Set-JIMConnectedSystemPartition -ConnectedSystemId 1 -PartitionId $_.id -Selected $true -PassThru }

        Selects all partitions and returns the updated objects.

    .LINK
        Get-JIMConnectedSystemPartition
        Set-JIMConnectedSystemContainer
        Get-JIMConnectedSystem
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.ConnectedSystemPartitionDto')]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$PartitionId,

        [Parameter()]
        [bool]$Selected,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Build update body
        $body = @{}

        if ($PSBoundParameters.ContainsKey('Selected')) {
            $body.selected = $Selected
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSCmdlet.ShouldProcess("Partition $PartitionId in Connected System $ConnectedSystemId", "Update")) {
            Write-Verbose "Updating Partition: $PartitionId in Connected System: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/partitions/$PartitionId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Partition: $PartitionId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Partition: $_"
            }
        }
    }
}
