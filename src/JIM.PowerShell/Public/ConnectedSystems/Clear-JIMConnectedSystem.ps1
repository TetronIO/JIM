# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Clear-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Empties a Connected System's Connector Space.

    .DESCRIPTION
        Deletes every Connected System Object in a Connected System's Connector Space, with their attribute values,
        Pending Exports and deferred references, usually before importing everything again. Metaverse Objects are
        kept; only their joins to the deleted objects are broken.

        Change history is deleted too, unless -KeepChangeHistory is given. The clear runs as a worker task, tracked
        by an Activity: the command returns once it is queued, or with -Wait once it has finished.

    .PARAMETER Id
        The ID of the Connected System to clear.

    .PARAMETER InputObject
        A Connected System from Get-JIMConnectedSystem, through the pipeline.

    .PARAMETER KeepChangeHistory
        Keeps the deleted objects' change history, no longer linked to them.

    .PARAMETER Wait
        Waits for the clear to finish before returning. Without it, an import run straight afterwards races the
        clear.

    .PARAMETER Timeout
        How many seconds -Wait waits before reporting an error; the clear carries on regardless. Without it, -Wait
        waits until the clear finishes.

    .PARAMETER Force
        Suppresses the confirmation prompt.

    .OUTPUTS
        The IDs of the Activity and worker task carrying out the clear.

    .EXAMPLE
        Clear-JIMConnectedSystem -Id 1

        Queues a clear of all objects from the Connected System with ID 1, including
        change history (prompts for confirmation).

    .EXAMPLE
        Clear-JIMConnectedSystem -Id 1 -Force

        Queues a clear of all objects from the Connected System with ID 1 without prompting.

    .EXAMPLE
        Clear-JIMConnectedSystem -Id 1 -KeepChangeHistory

        Queues a clear that preserves the change history audit trail.

    .EXAMPLE
        Clear-JIMConnectedSystem -Id 1 -Force -Wait

        Queues a clear and waits for it to finish before returning, showing progress.

    .EXAMPLE
        $clear = Clear-JIMConnectedSystem -Id 1 -Force
        Get-JIMActivity -Id $clear.ActivityId

        Queues a clear, capturing the tracking object, then retrieves the Activity to
        monitor its progress.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "HR*" | Clear-JIMConnectedSystem -Force

        Clears all objects from all Connected Systems with names starting with "HR".

    .LINK
        Get-JIMConnectedSystem
        Remove-JIMConnectedSystem
        Get-JIMActivity
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.ConnectorSpaceClearResponse')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$KeepChangeHistory,

        [switch]$Wait,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$Timeout,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Get the ID from InputObject if provided
        $systemId = if ($PSCmdlet.ParameterSetName -eq 'ByInputObject') {
            $InputObject.id
        } else {
            $Id
        }

        # Get system name for confirmation message
        $systemName = if ($PSCmdlet.ParameterSetName -eq 'ByInputObject') {
            $InputObject.name
        } else {
            try {
                $system = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId"
                $system.name
            } catch {
                "ID $systemId"
            }
        }

        # Confirm clearing
        if (Confirm-JIMAction -Cmdlet $PSCmdlet -Target $systemName -Action "Clear all objects from Connected System" -Force:$Force) {
            Write-Verbose "Queueing connector space clear for Connected System: $systemName (ID: $systemId)"

            try {
                $deleteChangeHistory = if ($KeepChangeHistory) { 'false' } else { 'true' }
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$systemId/clear?deleteChangeHistory=$deleteChangeHistory" -Method 'POST'

                Write-Verbose "Connector space clear queued for Connected System: $systemName (ID: $systemId); Activity: $($result.ActivityId)"

                if ($Wait) {
                    $waitParams = @{
                        ActivityId    = "$($result.ActivityId)"
                        ActivityLabel = "Clearing Connector Space for '$systemName'"
                    }
                    if ($PSBoundParameters.ContainsKey('Timeout')) { $waitParams.Timeout = $Timeout }
                    $clearStatus = Wait-JIMActivityCompletion @waitParams

                    if (-not $clearStatus) {
                        Write-Error ("The Connector Space clear for '$systemName' had not finished after ${Timeout}s. " +
                            "It continues on the server. Activity: $($result.ActivityId).")
                    }
                    elseif ($clearStatus -notin @('Complete', 'CompleteWithWarning')) {
                        Write-Error ("The Connector Space clear for '$systemName' ended with status '$clearStatus'. " +
                            "Activity: $($result.ActivityId).")
                    }
                    else {
                        Write-Verbose "Connector space cleared for Connected System: $systemName (ID: $systemId)"
                    }
                }

                $result
            }
            catch {
                Write-Error "Failed to clear Connected System '$systemName': $_"
            }
        }
    }
}
