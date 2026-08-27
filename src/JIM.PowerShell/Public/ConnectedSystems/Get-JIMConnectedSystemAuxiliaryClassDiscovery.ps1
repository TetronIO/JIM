# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemAuxiliaryClassDiscovery {
    <#
    .SYNOPSIS
        Gets the last auxiliary class discovery run for a Connected System.

    .DESCRIPTION
        Returns the most recent run, whatever its outcome. A cancelled run keeps the partial results
        it gathered, because a class it did observe is genuinely in use; the ones it never reached
        are simply unknown.

        Nothing is returned when no discovery run has been started for the Connected System.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .OUTPUTS
        PSCustomObject describing the run. Properties: Id, Scope, SampleSizePerObjectType, Status,
        Started, Completed, EntriesRead, ActivityId, InitiatedByName, ErrorMessage, and Results,
        each of which carries StructuralObjectTypeId, AuxiliaryClassName and EntryCount.

        Status is InProgress, Complete, Cancelled or Failed. Completed is $null while the run is
        still going.

    .EXAMPLE
        Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1

        Shows the last run, its scope, its outcome and how many entries it read.

    .EXAMPLE
        (Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1).Results |
            Sort-Object -Property EntryCount -Descending |
            Format-Table AuxiliaryClassName, EntryCount

        Ranks the auxiliary classes the last run observed by how widely they are used.

    .EXAMPLE
        $run = Get-JIMConnectedSystemAuxiliaryClassDiscovery -ConnectedSystemId 1
        if ($run.Status -eq 'InProgress') { Get-JIMActivity -Id $run.ActivityId }

        Reads the Activity of a run that is still going.

    .LINK
        Start-JIMConnectedSystemAuxiliaryClassDiscovery
        Get-JIMConnectedSystemAuxiliaryClass
        Get-JIMActivity
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting the latest auxiliary class discovery run for Connected System: $ConnectedSystemId"

        try {
            Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/auxiliary-class-discovery"
        }
        catch {
            # Never having run discovery is an ordinary state, not a failure, and the API says so
            # with a 404. Returning nothing lets a caller test the result rather than trap an error.
            if ($_.Exception.Response.StatusCode -eq 404) {
                Write-Verbose "No auxiliary class discovery run has been started for Connected System: $ConnectedSystemId"
                return
            }

            Write-Error "Failed to get the auxiliary class discovery run: $_"
        }
    }
}
