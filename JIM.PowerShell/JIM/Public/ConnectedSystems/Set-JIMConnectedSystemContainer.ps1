function Set-JIMConnectedSystemContainer {
    <#
    .SYNOPSIS
        Updates properties of a Connected System Container in JIM.

    .DESCRIPTION
        Updates properties of a container within a Connected System.
        Use this to select containers for import operations.
        When a container is selected, objects within it will be imported during sync.
        Note: The parent partition must also be selected for the container selection to take effect.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER ContainerId
        The unique identifier of the Container to update.

    .PARAMETER Selected
        Whether the container should be selected for import operations.
        When set to $true, JIM will import objects from this container.

    .PARAMETER PassThru
        If specified, returns the updated container.

    .OUTPUTS
        If -PassThru is specified, returns the updated Container object.

    .EXAMPLE
        Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Selected $true

        Selects the container for import operations.

    .EXAMPLE
        Get-JIMConnectedSystemPartition -ConnectedSystemId 1 |
            ForEach-Object { $_.containers } |
            Where-Object { $_.name -eq "Users" } |
            ForEach-Object { Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId $_.id -Selected $true }

        Selects the "Users" container from all partitions.

    .LINK
        Get-JIMConnectedSystemPartition
        Set-JIMConnectedSystemPartition
        Get-JIMConnectedSystem
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ContainerId,

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

        if ($PSCmdlet.ShouldProcess("Container $ContainerId in Connected System $ConnectedSystemId", "Update")) {
            Write-Verbose "Updating Container: $ContainerId in Connected System: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/containers/$ContainerId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Container: $ContainerId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Container: $_"
            }
        }
    }
}
