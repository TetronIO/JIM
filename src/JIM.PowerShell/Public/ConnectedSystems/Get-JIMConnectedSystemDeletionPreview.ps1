function Get-JIMConnectedSystemDeletionPreview {
    <#
    .SYNOPSIS
        Gets a deletion impact preview for a Connected System in JIM.

    .DESCRIPTION
        Retrieves a detailed impact analysis of what would be affected if the connected system were deleted.
        This includes counts of objects, sync rules, run profiles, partitions, and metaverse objects that
        would be impacted. Use this before Remove-JIMConnectedSystem to understand the scope of the operation.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .OUTPUTS
        PSCustomObject representing the deletion impact preview.

    .EXAMPLE
        Get-JIMConnectedSystemDeletionPreview -ConnectedSystemId 1

        Gets the deletion impact preview for Connected System 1.

    .EXAMPLE
        $preview = Get-JIMConnectedSystemDeletionPreview -ConnectedSystemId 1
        if ($preview.warnings.Count -gt 0) { $preview.warnings | ForEach-Object { Write-Warning $_ } }

        Gets the deletion impact preview for Connected System 1 and displays any warnings.

    .LINK
        Remove-JIMConnectedSystem
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

        Write-Verbose "Getting deletion preview for Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/deletion-preview"
            $result
        }
        catch {
            Write-Error "Failed to get deletion preview: $_"
        }
    }
}
