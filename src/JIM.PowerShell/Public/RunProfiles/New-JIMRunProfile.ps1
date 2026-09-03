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

    .PARAMETER VerifyImportContentHashes
        Enables Verification Mode. Only valid when -RunType is FullImport; the API rejects it
        otherwise. When enabled, the Full Import performs no content-hash skips and instead
        compares each object's stored import content hash against the freshly computed incoming
        hash, raising a diagnostic error for any disagreement the skip optimisation would
        otherwise have missed. Use temporarily to validate the skip optimisation; leave off for
        normal, faster Full Imports.

    .PARAMETER MaxCreates
        Run Profile Safeguards: the maximum number of creates an Export run may attempt. Only
        valid when -RunType is Export; the API rejects it otherwise. Omit for no limit. 0 is a
        valid limit ("attempt none of these"). When the limit is reached, the remaining creates
        stay pending for the next run and the run completes with a warning.

    .PARAMETER MaxUpdates
        Run Profile Safeguards: the maximum number of updates an Export run may attempt. Only
        valid when -RunType is Export; the API rejects it otherwise. Omit for no limit. 0 is a
        valid limit ("attempt none of these").

    .PARAMETER MaxDeletes
        Run Profile Safeguards: the maximum number of deletes an Export run may attempt. Only
        valid when -RunType is Export; the API rejects it otherwise. Omit for no limit. 0 is a
        valid limit ("attempt none of these"). Recommended for Export Run Profiles against
        production directories: a small share of the target's population catches a mass
        deprovisioning before it completes.

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

    .EXAMPLE
        New-JIMRunProfile -ConnectedSystemId 1 -Name "Export" -RunType Export -MaxDeletes 100

        Creates an Export Run Profile that stops after 100 deletes, leaving the remainder pending
        for the next run.

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

        [Parameter()]
        [switch]$VerifyImportContentHashes,

        [Parameter()]
        [Nullable[int]]$MaxCreates,

        [Parameter()]
        [Nullable[int]]$MaxUpdates,

        [Parameter()]
        [Nullable[int]]$MaxDeletes,

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

        # Run Profile Safeguards: validated here (not via ValidateRange, which misbehaves with
        # $null) so a negative value fails fast rather than reaching the API as a 400.
        foreach ($safeguard in @('MaxCreates', 'MaxUpdates', 'MaxDeletes')) {
            if ($PSBoundParameters.ContainsKey($safeguard)) {
                $value = $PSBoundParameters[$safeguard]
                if ($null -ne $value -and $value -lt 0) {
                    Write-Error "$safeguard cannot be negative."
                    return
                }
            }
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

            if ($VerifyImportContentHashes) {
                $body.verifyImportContentHashes = $true
            }

            # Run Profile Safeguards: send the whole safeguards object when any limit is bound;
            # an unbound parameter is $null, which is exactly "no limit" for that member.
            if ($PSBoundParameters.ContainsKey('MaxCreates') -or $PSBoundParameters.ContainsKey('MaxUpdates') -or $PSBoundParameters.ContainsKey('MaxDeletes')) {
                $body.safeguards = @{
                    maxCreates = $MaxCreates
                    maxUpdates = $MaxUpdates
                    maxDeletes = $MaxDeletes
                }
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
