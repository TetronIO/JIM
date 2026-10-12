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
        The Connected System whose Run Profiles to get.

    .PARAMETER ConnectedSystemName
        The name of the Connected System whose Run Profiles to get. Must be an exact match.

    .PARAMETER Name
        Return only the Run Profiles whose name matches, for example "Full*".

    .OUTPUTS
        Each Run Profile on the Connected System, or each one whose name matches -Name.

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

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemId 1 | Where-Object allowObjectTypeChanges

        Lists the Full Import Run Profiles that carry out Object Type changes rather than refusing them, so an
        operator can check none of them runs on a Schedule.

    .LINK
        Start-JIMRunProfile
        New-JIMRunProfile
        Get-JIMConnectedSystem
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType('JIM.RunProfileDto')]
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
