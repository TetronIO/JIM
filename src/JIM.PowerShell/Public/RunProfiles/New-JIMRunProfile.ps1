# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMRunProfile {
    <#
    .SYNOPSIS
        Creates a new Run Profile for a Connected System in JIM.

    .DESCRIPTION
        Creates a new Run Profile that defines a synchronisation operation (Full Import,
        Delta Import, Full Sync, Delta Sync, or Export) for a Connected System.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System to create the Run Profile for.

    .PARAMETER ConnectedSystemName
        The name of the Connected System to create the Run Profile for. Must be an exact match.

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
        New-JIMRunProfile -ConnectedSystemName 'Contoso AD' -Name "Full Import" -RunType FullImport

        Creates a Full Import Run Profile for the 'Contoso AD' Connected System.

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
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$ConnectedSystemName,

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
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Resolve ConnectedSystemName to ConnectedSystemId if specified
        if ($PSBoundParameters.ContainsKey('ConnectedSystemName')) {
            $connectedSystem = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
            $ConnectedSystemId = $connectedSystem.id
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Run Profile")) {
            Write-Verbose "Creating Run Profile: $Name for Connected System $ConnectedSystemId"

            $body = @{
                name = $Name
                # Send the enum as its string name; -RunType is ValidateSet-constrained
                # to the exact ConnectedSystemRunType member names. The API rejects numeric
                # ordinals (JsonStringEnumConverter allowIntegerValues:false, PR #1060).
                runType = $RunType
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
