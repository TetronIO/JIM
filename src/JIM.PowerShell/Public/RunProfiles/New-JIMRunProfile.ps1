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
        Run Profile Safeguards: the most creates that may be pending for a single Export run to
        attempt any of them. If more are pending than this when the run starts, JIM attempts NONE
        of them; there is no partial attempt. Only valid when -RunType is Export; the API rejects
        it otherwise. Omit for no limit. 0 refuses creates outright. A run that withholds anything
        stays pending and completes with a warning naming what to do next: raise or clear the
        limit, or run an Export Run Profile without one.

    .PARAMETER MaxUpdates
        Run Profile Safeguards: the most updates that may be pending for a single Export run to
        attempt any of them. Same all-or-nothing behaviour as -MaxCreates. Only valid when
        -RunType is Export; the API rejects it otherwise. Omit for no limit. 0 refuses updates
        outright.

    .PARAMETER MaxDeletes
        Run Profile Safeguards: the most deletes that may be pending for a single Export run to
        attempt any of them. Same all-or-nothing behaviour as -MaxCreates. Only valid when
        -RunType is Export; the API rejects it otherwise. Omit for no limit. 0 refuses deletes
        outright. Recommended for Export Run Profiles against production directories: a small
        share of the target's population means a broken filter or rule change withholds the
        whole deprovisioning attempt and warns you, rather than working through the directory.

    .PARAMETER MaxDetectedDeletions
        Run Profile Safeguards: the most Connected System Objects a single Full Import run may
        newly mark as deleted. If more would be newly marked than this when the run finishes
        reading the Connected System, JIM marks NONE of them; there is no partial marking. Only
        valid when -RunType is FullImport; the API rejects it otherwise. Omit for no limit. 0
        refuses to mark anything as deleted. Objects the import did see are still created and
        updated as normal. A run that withholds anything completes with a warning naming what to
        do next, and does not count as a successful Full Import for the post-clear reconciliation
        gate (#1605).

    .PARAMETER MaxDetectedDeletionsPercent
        Run Profile Safeguards: the most Connected System Objects a single Full Import run may
        newly mark as deleted, as a share (0 to 100) of the Connected System Objects in the run's
        scope when it starts. Same all-or-nothing behaviour as -MaxDetectedDeletions, and either
        limit tripping withholds the whole detection. Only valid when -RunType is FullImport; the
        API rejects it otherwise. Omit for no limit. Recommended for a Full Import against a large
        Connected System, where a broken filter or base DN dropping a plausible-looking fraction
        of the population is easier to catch as a share than as a raw count.

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

        Creates an Export Run Profile that attempts no deletes at all on a run where more than 100
        are pending, leaving every one of them pending instead.

    .EXAMPLE
        New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import" -RunType FullImport -MaxDetectedDeletionsPercent 10

        Creates a Full Import Run Profile that marks nothing as deleted on a run where deletion
        detection would newly mark more than 10% of the Connected System's objects as deleted.

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

        [Parameter()]
        [Nullable[int]]$MaxDetectedDeletions,

        [Parameter()]
        [Nullable[int]]$MaxDetectedDeletionsPercent,

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
        foreach ($safeguard in @('MaxCreates', 'MaxUpdates', 'MaxDeletes', 'MaxDetectedDeletions')) {
            if ($PSBoundParameters.ContainsKey($safeguard)) {
                $value = $PSBoundParameters[$safeguard]
                if ($null -ne $value -and $value -lt 0) {
                    Write-Error "$safeguard cannot be negative."
                    return
                }
            }
        }

        if ($PSBoundParameters.ContainsKey('MaxDetectedDeletionsPercent')) {
            if ($null -ne $MaxDetectedDeletionsPercent -and ($MaxDetectedDeletionsPercent -lt 0 -or $MaxDetectedDeletionsPercent -gt 100)) {
                Write-Error "MaxDetectedDeletionsPercent must be between 0 and 100."
                return
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
            if ($PSBoundParameters.ContainsKey('MaxCreates') -or $PSBoundParameters.ContainsKey('MaxUpdates') -or $PSBoundParameters.ContainsKey('MaxDeletes') -or
                $PSBoundParameters.ContainsKey('MaxDetectedDeletions') -or $PSBoundParameters.ContainsKey('MaxDetectedDeletionsPercent')) {
                $body.safeguards = @{
                    maxCreates = $MaxCreates
                    maxUpdates = $MaxUpdates
                    maxDeletes = $MaxDeletes
                    maxDetectedDeletions = $MaxDetectedDeletions
                    maxDetectedDeletionsPercent = $MaxDetectedDeletionsPercent
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
