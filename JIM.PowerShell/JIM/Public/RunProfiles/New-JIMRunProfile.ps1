function New-JIMRunProfile {
    <#
    .SYNOPSIS
        Creates a new Run Profile for a Connected System in JIM.

    .DESCRIPTION
        Creates a new Run Profile that defines a synchronisation operation (Full Import,
        Delta Import, Full Sync, Delta Sync, or Export) for a Connected System.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System to create the Run Profile for.

    .PARAMETER Name
        The name for the Run Profile.

    .PARAMETER RunType
        The type of synchronisation operation:
        - FullImport: Full import from the Connected System
        - DeltaImport: Delta/incremental import from the Connected System
        - FullSynchronisation: Full synchronisation with the Metaverse
        - DeltaSynchronisation: Delta synchronisation with the Metaverse
        - Export: Export changes to the Connected System

    .PARAMETER PageSize
        How many items to process in one batch. Defaults to 100.

    .PARAMETER PartitionId
        Optional partition ID if the connector supports partitions.

    .PARAMETER FilePath
        Optional file path for file-based connectors.

    .PARAMETER PassThru
        If specified, returns the created Run Profile object.

    .OUTPUTS
        If -PassThru is specified, returns the created Run Profile object.

    .EXAMPLE
        New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import" -RunType FullImport

        Creates a Full Import Run Profile for Connected System 1.

    .EXAMPLE
        New-JIMRunProfile -ConnectedSystemId 1 -Name "Delta Import" -RunType DeltaImport -PageSize 500 -PassThru

        Creates a Delta Import Run Profile with custom page size and returns it.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "CSV*" | ForEach-Object {
            New-JIMRunProfile -ConnectedSystemId $_.id -Name "Full Import" -RunType FullImport -FilePath "C:\Data\import.csv"
        }

        Creates Run Profiles for all CSV-based Connected Systems.

    .LINK
        Get-JIMRunProfile
        Set-JIMRunProfile
        Remove-JIMRunProfile
        Start-JIMRunProfile
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory)]
        [ValidateSet('FullImport', 'DeltaImport', 'FullSynchronisation', 'DeltaSynchronisation', 'Export')]
        [string]$RunType,

        [Parameter()]
        [ValidateRange(1, 10000)]
        [int]$PageSize = 100,

        [Parameter()]
        [int]$PartitionId,

        [Parameter()]
        [string]$FilePath,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Run Profile")) {
            Write-Verbose "Creating Run Profile: $Name for Connected System $ConnectedSystemId"

            # Map RunType string to API enum value
            $runTypeValue = switch ($RunType) {
                'FullImport' { 1 }
                'DeltaImport' { 2 }
                'FullSynchronisation' { 3 }
                'DeltaSynchronisation' { 4 }
                'Export' { 5 }
            }

            $body = @{
                name = $Name
                runType = $runTypeValue
                pageSize = $PageSize
            }

            if ($PSBoundParameters.ContainsKey('PartitionId')) {
                $body.partitionId = $PartitionId
            }

            if ($FilePath) {
                $body.filePath = $FilePath
            }

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/run-profiles" -Method 'POST' -Body $body

                Write-Verbose "Created Run Profile: $($result.id) ($($result.name))"

                if ($PassThru) {
                    # Add ConnectedSystemId for pipeline chaining
                    $result | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to create Run Profile: $_"
            }
        }
    }
}
