function Get-JIMActivityChildren {
    <#
    .SYNOPSIS
        Gets child Activities for a parent Activity in JIM.

    .DESCRIPTION
        Retrieves child activities that were spawned by a parent activity. For example,
        a schedule execution activity may have child activities for each individual
        run profile step.

    .PARAMETER Id
        The unique identifier (GUID) of the parent Activity.

    .OUTPUTS
        PSCustomObject representing the child Activities.

    .EXAMPLE
        Get-JIMActivityChildren -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

        Gets all child activities for the specified parent activity.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" |
            Get-JIMActivityChildren

        Gets child activities for a parent activity via pipeline.

    .LINK
        Get-JIMActivity
        Get-JIMActivityStats
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [guid]$Id
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        Write-Verbose "Getting child activities for Activity: $Id"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/activities/$Id/children"

            # Output each child activity individually for pipeline support
            foreach ($activity in $result) {
                $activity
            }
        }
        catch {
            Write-Error "Failed to get child activities: $_"
        }
    }
}
