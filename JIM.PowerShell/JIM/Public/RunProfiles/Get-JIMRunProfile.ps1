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

    .OUTPUTS
        PSCustomObject representing Run Profile(s).

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemId 1

        Gets all Run Profiles for Connected System ID 1.

    .EXAMPLE
        Get-JIMRunProfile -ConnectedSystemName 'Contoso AD'

        Gets all Run Profiles for the Connected System named 'Contoso AD'.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "HR*" | Get-JIMRunProfile

        Gets all Run Profiles for Connected Systems with names starting with "HR".

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
        [string]$ConnectedSystemName
    )

    process {
        # Resolve ConnectedSystemName to ConnectedSystemId if specified
        if ($PSBoundParameters.ContainsKey('ConnectedSystemName')) {
            $connectedSystem = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
            $ConnectedSystemId = $connectedSystem.id
        }

        Write-Verbose "Getting Run Profiles for Connected System ID: $ConnectedSystemId"
        $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/run-profiles"

        # Output each profile individually for pipeline support
        foreach ($profile in $result) {
            # Add ConnectedSystemId to the output for pipeline chaining
            $profile | Add-Member -NotePropertyName 'ConnectedSystemId' -NotePropertyValue $ConnectedSystemId -PassThru -Force
        }
    }
}
