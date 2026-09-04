# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMRunProfile {
    <#
    .SYNOPSIS
        Gets Run Profiles from JIM.

    .DESCRIPTION
        Retrieves Run Profile configurations for a Connected System from JIM.
        Run Profiles define the synchronisation operations (Full Import, Delta Import,
        Full Sync, Delta Sync, Export) that can be executed against a Connected System.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System to get Run Profiles for.

    .PARAMETER ConnectedSystemName
        The name of the Connected System to get Run Profiles for. Must be an exact match.

    .PARAMETER Name
        Filter Run Profiles by name. Supports wildcards (e.g., "Full*").

    .OUTPUTS
        PSCustomObject representing Run Profile(s), with these properties:
        - id, name, connectedSystemId, runType, pageSize
        - partitionName: the partition targeted, or null when the Run Profile follows every selected partition
        - targetsDeselectedPartition: true when the targeted partition is no longer selected on the Connected
          System. Such a Run Profile is inoperable; a deselected partition is not managed by JIM, so
          Start-JIMRunProfile refuses it rather than reading scope that has been withdrawn.
        - filePath, verifyImportContentHashes
        - safeguards: the Run Profile Safeguards limits (maxCreates, maxUpdates, maxDeletes), always
          present; each member is null when that limit is not set
        - ConnectedSystemId (added for pipeline chaining)

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemId 1

        Gets all Run Profiles for Connected System ID 1.

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemName 'Contoso AD'

        Gets all Run Profiles for the Connected System named 'Contoso AD'.

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemId 1 -Name "Full*"

        Gets Run Profiles with names starting with "Full" for Connected System ID 1.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "HR*" | Get-JIMRunProfile

        Gets all Run Profiles for Connected Systems with names starting with "HR".

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemId 1 | Where-Object targetsDeselectedPartition

        Lists the Run Profiles left inoperable by the current partition selections, so an operator can repoint or
        remove them before a scheduled run fails.

    .LINK
        Start-JIMRunProfile
        New-JIMRunProfile
        Get-JIMConnectedSystem
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$ConnectedSystemName,

        [Parameter(ParameterSetName = 'ById')]
        [Parameter(ParameterSetName = 'ByName')]
        [SupportsWildcards()]
        [string]$Name
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

        Write-Verbose "Getting Run Profiles for Connected System ID: $ConnectedSystemId"
        $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/run-profiles"

        # Filter by name if specified
        if ($Name) {
            Write-Verbose "Filtering by name pattern: $Name"
            $result = $result | Where-Object { $_.name -like $Name }
        }

        # Output each profile individually for pipeline support
        foreach ($profile in $result) {
            # Add ConnectedSystemId to the output for pipeline chaining
            $profile | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
        }
    }
}
