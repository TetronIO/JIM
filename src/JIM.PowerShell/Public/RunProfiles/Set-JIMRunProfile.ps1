# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMRunProfile {
    <#
    .SYNOPSIS
        Updates an existing Run Profile in JIM.

    .DESCRIPTION
        Updates the properties of an existing Run Profile.
        Only the parameters provided will be updated.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System the Run Profile belongs to.

    .PARAMETER ConnectedSystemName
        The name of the Connected System the Run Profile belongs to. Must be an exact match.

    .PARAMETER RunProfileId
        The unique identifier of the Run Profile to update.

    .PARAMETER InputObject
        Run Profile object to update (from pipeline).

    .PARAMETER Name
        The new name for the Run Profile.

    .PARAMETER PageSize
        The new page size for the Run Profile.

    .PARAMETER PartitionId
        The partition ID to set (for connectors that support partitions).

    .PARAMETER FilePath
        The file path to set (for file-based connectors).

    .PARAMETER VerifyImportContentHashes
        When specified, sets whether Verification Mode is enabled. Pass $true to enable, $false to
        disable. Omit to leave the current state unchanged. Only valid on a Full Import Run
        Profile; the API rejects $true otherwise. When enabled, the Full Import performs no
        content-hash skips and instead compares each object's stored import content hash against
        the freshly computed incoming hash, raising a diagnostic error for any disagreement.

    .PARAMETER MaxCreates
        Run Profile Safeguards: sets the most creates that may be pending for a single Export run
        to attempt any of them. If more are pending than this when a run starts, JIM attempts NONE
        of them; there is no partial attempt. Only valid on an Export Run Profile; the API rejects
        it otherwise. Pass a number to set the limit, 0 to refuse creates outright, or $null to
        clear the limit (no limit). Omit the parameter entirely to leave the current value
        unchanged. Setting any one of -MaxCreates, -MaxUpdates, -MaxDeletes, -MaxDetectedDeletions
        or -MaxDetectedDeletionsPercent fetches the Run Profile's other four current values and
        sends all five together, so the ones you did not pass are preserved exactly.

    .PARAMETER MaxUpdates
        Run Profile Safeguards: sets the most updates that may be pending for a single Export run
        to attempt any of them. Same all-or-nothing semantics as -MaxCreates.

    .PARAMETER MaxDeletes
        Run Profile Safeguards: sets the most deletes that may be pending for a single Export run
        to attempt any of them. Same all-or-nothing semantics as -MaxCreates.

    .PARAMETER MaxDetectedDeletions
        Run Profile Safeguards: sets the most Connected System Objects a single Full Import run
        may newly mark as deleted. If more would be newly marked than this, JIM marks NONE of
        them; there is no partial marking. Only valid on a Full Import Run Profile; the API
        rejects it otherwise. Pass a number to set the limit, 0 to refuse to mark anything as
        deleted, or $null to clear the limit (no limit). Omit the parameter entirely to leave the
        current value unchanged. Setting any one of -MaxCreates, -MaxUpdates, -MaxDeletes,
        -MaxDetectedDeletions or -MaxDetectedDeletionsPercent fetches the Run Profile's other four
        current values and sends all five together, so the ones you did not pass are preserved
        exactly.

    .PARAMETER MaxDetectedDeletionsPercent
        Run Profile Safeguards: sets the most Connected System Objects a single Full Import run
        may newly mark as deleted, as a share (0 to 100) of the Connected System Objects in the
        run's scope when it starts. Same all-or-nothing semantics as -MaxDetectedDeletions.

    .PARAMETER PassThru
        If specified, returns the updated Run Profile object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Run Profile object.

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -Name "Updated Name"

        Updates the name of the Run Profile.

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemName 'Contoso AD' -RunProfileId 1 -PageSize 500

        Updates the page size of a Run Profile using the Connected System name.

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -PageSize 500 -PassThru

        Updates the page size and returns the updated object.

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemId 1 | Where-Object { $_.name -eq "Full Import" } | Set-JIMRunProfile -PageSize 1000

        Updates a Run Profile found by pipeline.

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -VerifyImportContentHashes $true

        Enables Verification Mode on a Full Import Run Profile.

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -VerifyImportContentHashes $false

        Disables Verification Mode, returning the Run Profile to normal content-hash-skip behaviour.

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 12 -MaxDeletes $null

        Clears the delete limit, leaving Max creates and Max updates exactly as they were.

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 7 -MaxDetectedDeletionsPercent 10

        Sets a 10% deletion detection limit on a Full Import Run Profile, leaving Max detected
        deletions exactly as it was.

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 7 -MaxDetectedDeletions $null -MaxDetectedDeletionsPercent $null

        Clears both deletion detection limits, so the Full Import's deletion detection is
        unrestricted again.

    .LINK
        Get-JIMRunProfile
        New-JIMRunProfile
        Remove-JIMRunProfile
        Start-JIMRunProfile
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$ConnectedSystemName,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [int]$RunProfileId,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter()]
        [ValidateRange(1, 10000)]
        [int]$PageSize,

        [Parameter()]
        [int]$PartitionId,

        [Parameter()]
        [string]$FilePath,

        [Parameter()]
        [bool]$VerifyImportContentHashes,

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

        $csId = if ($InputObject) { $InputObject.connectedSystemId } else { $ConnectedSystemId }
        $profileId = if ($InputObject) { $InputObject.id } else { $RunProfileId }

        if (-not $csId) {
            Write-Error "ConnectedSystemId is required. Provide -ConnectedSystemId parameter or pipe an object with connectedSystemId property."
            return
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

        # Build update body
        $body = @{}

        if ($Name) {
            $body.name = $Name
        }

        if ($PSBoundParameters.ContainsKey('PageSize')) {
            $body.pageSize = $PageSize
        }

        if ($PSBoundParameters.ContainsKey('PartitionId')) {
            $body.partitionId = $PartitionId
        }

        if ($PSBoundParameters.ContainsKey('FilePath')) {
            $body.filePath = $FilePath
        }

        # Checking $PSBoundParameters distinguishes "-VerifyImportContentHashes $false" (intentional)
        # from "-VerifyImportContentHashes not provided" (leave unchanged); [bool] alone cannot
        # express this (mirrors Set-JIMPredefinedSearch -IsEnabled).
        if ($PSBoundParameters.ContainsKey('VerifyImportContentHashes')) {
            $body.verifyImportContentHashes = $VerifyImportContentHashes
        }

        # Run Profile Safeguards: the update contract replaces all five members together, so
        # binding any one of -MaxCreates/-MaxUpdates/-MaxDeletes/-MaxDetectedDeletions/
        # -MaxDetectedDeletionsPercent means fetching the Run Profile's current safeguards first
        # and overwriting only the bound members with the new values; an explicit $null clears
        # that member ($PSBoundParameters.ContainsKey is true for a parameter passed $null).
        if ($PSBoundParameters.ContainsKey('MaxCreates') -or $PSBoundParameters.ContainsKey('MaxUpdates') -or $PSBoundParameters.ContainsKey('MaxDeletes') -or
            $PSBoundParameters.ContainsKey('MaxDetectedDeletions') -or $PSBoundParameters.ContainsKey('MaxDetectedDeletionsPercent')) {
            $currentProfiles = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$csId/run-profiles"
            $currentProfile = $currentProfiles | Where-Object { $_.id -eq $profileId }
            $currentSafeguards = $currentProfile.safeguards

            $body.safeguards = @{
                maxCreates = if ($PSBoundParameters.ContainsKey('MaxCreates')) { $MaxCreates } else { $currentSafeguards.maxCreates }
                maxUpdates = if ($PSBoundParameters.ContainsKey('MaxUpdates')) { $MaxUpdates } else { $currentSafeguards.maxUpdates }
                maxDeletes = if ($PSBoundParameters.ContainsKey('MaxDeletes')) { $MaxDeletes } else { $currentSafeguards.maxDeletes }
                maxDetectedDeletions = if ($PSBoundParameters.ContainsKey('MaxDetectedDeletions')) { $MaxDetectedDeletions } else { $currentSafeguards.maxDetectedDeletions }
                maxDetectedDeletionsPercent = if ($PSBoundParameters.ContainsKey('MaxDetectedDeletionsPercent')) { $MaxDetectedDeletionsPercent } else { $currentSafeguards.maxDetectedDeletionsPercent }
            }
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        $displayName = $Name ?? $profileId

        if ($PSCmdlet.ShouldProcess($displayName, "Update Run Profile")) {
            Write-Verbose "Updating Run Profile: $profileId for Connected System $csId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$csId/run-profiles/$profileId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Run Profile: $profileId"

                if ($PassThru) {
                    # Add ConnectedSystemId for pipeline chaining
                    $result | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $csId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to update Run Profile: $_"
            }
        }
    }
}
