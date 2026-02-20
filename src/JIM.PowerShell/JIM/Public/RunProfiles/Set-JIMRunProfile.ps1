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

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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
