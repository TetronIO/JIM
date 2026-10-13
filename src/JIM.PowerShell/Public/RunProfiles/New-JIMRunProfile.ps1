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
        The Connected System to create the Run Profile on.

    .PARAMETER ConnectedSystemName
        The name of the Connected System to create the Run Profile on. Must be an exact match.

    .PARAMETER Name
        The name for the Run Profile.

    .PARAMETER RunType
        The operation the Run Profile carries out: an import from the Connected System, a synchronisation with the
        Metaverse, or an export to the Connected System.

    .PARAMETER PageSize
        How many items to process in one batch.

    .PARAMETER PartitionId
        The partition the Run Profile targets, for a Connector with partitions. Omit it and the Run Profile follows
        every selected partition.

    .PARAMETER FilePath
        The file to read or write, for a file-based Connector.

    .PARAMETER VerifyImportContentHashes
        Enables Verification Mode: the Full Import skips no object whose content looks unchanged, and raises an
        error for any object whose stored content hash disagrees with what was read. Only valid when -RunType is
        FullImport. Use it to check the skip, and leave it off for normal, faster Full Imports.

    .PARAMETER AllowObjectTypeChanges
        Lets this Full Import carry out an Object Type change rather than refusing it: an object that arrives under
        a different Object Type is imported as a new Connected System Object, and the old one is retired. Only valid
        when -RunType is FullImport, and only has an effect for a Connector that can tell an Object Type change from
        a new object, as the LDAP Connector can.

    .PARAMETER MaxCreates
        A Safeguard: the most creates that may be pending for an Export run to attempt any of them. If more are
        pending when the run starts, it attempts none of them; 0 refuses creates outright. Only valid when -RunType
        is Export; omit it for no limit.

    .PARAMETER MaxUpdates
        A Safeguard: the most updates that may be pending for an Export run to attempt any of them, under the same
        rules as -MaxCreates.

    .PARAMETER MaxDeletes
        A Safeguard: the most deletes that may be pending for an Export run to attempt any of them, under the same
        rules as -MaxCreates. Recommended for an Export to a production directory.

    .PARAMETER MaxDetectedDeletions
        A Safeguard: the most Connected System Objects a Full Import run may newly mark as deleted. If more would
        be, it marks none of them; 0 refuses to mark any. Only valid when -RunType is FullImport; omit it for no
        limit.

    .PARAMETER MaxDetectedDeletionsPercent
        A Safeguard: the most Connected System Objects a Full Import run may newly mark as deleted, as a share (0 to
        100) of those in the run's scope when it starts, under the same rules as -MaxDetectedDeletions. Either limit
        tripping withholds the whole detection.

    .PARAMETER PassThru
        Returns the created Run Profile.

    .OUTPUTS
        With -PassThru, the created Run Profile; otherwise nothing.

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

    .EXAMPLE
        New-JIMRunProfile -ConnectedSystemId 1 -Name "Full Import (apply Object Type changes)" -RunType FullImport -AllowObjectTypeChanges

        Creates a second Full Import Run Profile, to be run by hand, that carries out Object Type
        changes the Connected System's usual Full Import refuses: each object that has changed
        Object Type is imported as a new Connected System Object, and the old one is retired.

    .LINK
        Get-JIMRunProfile
        Set-JIMRunProfile
        Remove-JIMRunProfile
        Start-JIMRunProfile
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.RunProfileDto')]
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
        [switch]$AllowObjectTypeChanges,

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

            # Sent only when the switch is present, so an omitted switch leaves the API's default (off).
            if ($AllowObjectTypeChanges) {
                $body.allowObjectTypeChanges = $true
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
