function Get-JIMActivityStats {
    <#
    .SYNOPSIS
        Gets execution statistics for a Run Profile activity.

    .DESCRIPTION
        Retrieves detailed execution statistics for a Run Profile activity, including
        counts of processed items, errors, and timing information.

        This cmdlet only works with activities that are Run Profile executions.

    .PARAMETER Id
        The unique identifier (GUID) of the Activity to get statistics for.

    .OUTPUTS
        PSCustomObject containing execution statistics.

    .EXAMPLE
        Get-JIMActivityStats -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

        Gets execution statistics for the specified activity.

    .EXAMPLE
        Get-JIMActivity | Select-Object -First 1 | Get-JIMActivityStats

        Gets statistics for the most recent activity.

    .EXAMPLE
        Start-JIMRunProfile -ConnectedSystemId 1 -RunProfileId 1 -PassThru |
            ForEach-Object { Get-JIMActivityStats -Id $_.activityId }

        Executes a Run Profile and gets its statistics.

    .LINK
        Get-JIMActivity
        Start-JIMRunProfile
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('ActivityId')]
        [guid]$Id
    )

    process {
        Write-Verbose "Getting execution statistics for Activity ID: $Id"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/activities/$Id/stats"
            $result
        }
        catch {
            if ($_.Exception.Message -match '400') {
                Write-Error "Activity $Id is not a Run Profile activity. Statistics are only available for Run Profile executions."
            }
            else {
                throw
            }
        }
    }
}
