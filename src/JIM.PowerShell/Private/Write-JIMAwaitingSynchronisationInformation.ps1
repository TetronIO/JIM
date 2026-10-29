# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Write-JIMAwaitingSynchronisationInformation {
    <#
    .SYNOPSIS
        Says which Connected System Object Types a save made the next synchronisation process in full (#2615).

    .DESCRIPTION
        Saving a change to an import Synchronisation Rule, an Attribute Flow or an Object Matching Rule can alter what an
        import produces for objects that already exist. JIM records the change as awaiting synchronisation, and the
        Connected System's next synchronisation, Delta or Full, then processes every object of the Connected System Object
        Type rather than only the changed ones. The API lists each such type in the response's AwaitingSynchronisation, with
        how many objects that is and, where the Connected System has completed a Full Synchronisation, about how long it
        takes at that run's speed; this writes one line per type to the information stream.

        It is information, not a warning: nothing is wrong, and the save has already been made. It never prompts and never
        blocks. The line is shown with -InformationAction Continue, and the same facts are on the response's
        AwaitingSynchronisation property for a caller that asks the cmdlet to return the saved object.

    .PARAMETER AwaitingSynchronisation
        The response's AwaitingSynchronisation list. Nothing is written when it is empty or absent.

    .OUTPUTS
        None. Writes to the information stream only.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]
        $AwaitingSynchronisation
    )

    foreach ($entry in @($AwaitingSynchronisation | Where-Object { $_ })) {
        $count = [int]$entry.ObjectCount
        $objects = if ($count -eq 1) { '1 object' } else { "$($count.ToString('N0', [cultureinfo]::InvariantCulture)) objects" }

        $duration = ''
        if ($null -ne $entry.EstimatedDuration -and "$($entry.EstimatedDuration)" -ne '') {
            $span = [TimeSpan]::Zero
            $parsed = if ($entry.EstimatedDuration -is [TimeSpan]) {
                $span = $entry.EstimatedDuration
                $true
            }
            else {
                [TimeSpan]::TryParse("$($entry.EstimatedDuration)", [cultureinfo]::InvariantCulture, [ref]$span)
            }

            if ($parsed) {
                $minutes = [int][Math]::Round($span.TotalMinutes, [MidpointRounding]::AwayFromZero)
                $howLong = if ($span -lt [TimeSpan]::FromMinutes(1)) { 'under a minute' }
                           elseif ($minutes -eq 1) { 'about 1 minute' }
                           else { "about $($minutes.ToString('N0', [cultureinfo]::InvariantCulture)) minutes" }
                $duration = ", $howLong at the speed of its last Full Synchronisation"
            }
        }

        Write-Information ("Awaiting synchronisation: the next synchronisation of $($entry.ConnectedSystemName), Delta or Full, " +
            "processes every $($entry.ObjectTypeName) object, not only the changed ones: $objects$duration.")
    }
}
