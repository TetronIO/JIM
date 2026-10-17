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
        The Connected System the Run Profile belongs to.

    .PARAMETER ConnectedSystemName
        The name of the Connected System the Run Profile belongs to. Must be an exact match.

    .PARAMETER RunProfileId
        The Run Profile to update.

    .PARAMETER InputObject
        The Run Profile to update, as Get-JIMRunProfile returns it.

    .PARAMETER Name
        The new name.

    .PARAMETER PageSize
        How many items to process in one batch.

    .PARAMETER PartitionId
        The partition the Run Profile targets, for a Connector with partitions.

    .PARAMETER FilePath
        The file a Full Import reads, for a file-based Connector. Set it to an empty string to read the file the Connected System's
        File Path setting names.

    .PARAMETER VerifyImportContentHashes
        Whether Verification Mode is enabled: $true to enable it, $false to disable it. Only a Full Import Run
        Profile accepts $true.

    .PARAMETER AllowObjectTypeChanges
        Whether this Full Import carries out an Object Type change rather than refusing it: $true to allow it,
        $false to go back to refusing it. Only a Full Import Run Profile accepts $true.

    .PARAMETER MaxCreates
        A Safeguard: the most creates that may be pending for an Export run to attempt any of them. 0 refuses
        creates outright, and $null clears the limit. Only valid on an Export Run Profile. The Safeguards you do not
        give keep their values.

    .PARAMETER MaxUpdates
        A Safeguard: the most updates that may be pending for an Export run to attempt any of them, under the same
        rules as -MaxCreates.

    .PARAMETER MaxDeletes
        A Safeguard: the most deletes that may be pending for an Export run to attempt any of them, under the same
        rules as -MaxCreates.

    .PARAMETER MaxDetectedDeletions
        A Safeguard: the most Connected System Objects an import run may newly mark as deleted: those a Full Import
        no longer finds, or those a Delta Import's Connector reports deleted. 0 refuses to mark any, and $null
        clears the limit. Only valid on a Full Import or Delta Import Run Profile. The Safeguards you do not give
        keep their values.

    .PARAMETER MaxDetectedDeletionsPercent
        A Safeguard: the most Connected System Objects an import run may newly mark as deleted, as a share (0 to
        100) of those in the run's scope not already marked as deleted, before it saves anything, under the same
        rules as -MaxDetectedDeletions.

    .PARAMETER PassThru
        Returns the updated Run Profile.

    .OUTPUTS
        With -PassThru, the updated Run Profile; otherwise nothing.

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
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 3 -AllowObjectTypeChanges $true

        Lets Full Import Run Profile 3 carry out the Object Type changes it would otherwise
        refuse. If that Run Profile runs on a Schedule, set it back to $false once the change has
        been imported, so later runs refuse again.

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 3 -AllowObjectTypeChanges $false

        Returns the Full Import to refusing Object Type changes, keeping each existing Connected
        System Object and reporting the change as an error instead.

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

    .EXAMPLE
        Set-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 8 -MaxDetectedDeletions 50

        Limits Delta Import Run Profile 8 to applying at most 50 deletions the Connected System
        reports in one run; a run that reports more applies none of them.

    .LINK
        Get-JIMRunProfile
        New-JIMRunProfile
        Remove-JIMRunProfile
        Start-JIMRunProfile
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.RunProfileDto')]
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
        [bool]$AllowObjectTypeChanges,

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

        # As above: -AllowObjectTypeChanges $false is sent (turns it off); an unbound parameter is
        # omitted, which the API reads as "leave unchanged".
        if ($PSBoundParameters.ContainsKey('AllowObjectTypeChanges')) {
            $body.allowObjectTypeChanges = $AllowObjectTypeChanges
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
