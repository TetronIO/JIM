function Set-JIMConnectedSystemPartition {
    <#
    .SYNOPSIS
        Updates properties of a Connected System Partition in JIM.

    .DESCRIPTION
        Updates properties of a partition within a Connected System.
        Use this to select partitions for import operations.
        When a partition is selected, objects within it (and its selected containers)
        will be imported during sync.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER PartitionId
        The unique identifier of the Partition to update.

    .PARAMETER Selected
        Whether the partition should be selected for import operations.
        When set to $true, JIM will import objects from this partition.

    .PARAMETER PassThru
        If specified, returns the updated partition.

    .OUTPUTS
        If -PassThru is specified, returns the updated Partition object.

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
    [OutputType([PSCustomObject])]
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
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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
