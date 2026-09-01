# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Clear-JIMConnectedSystem {
    <#
    .SYNOPSIS
        Clears all objects from a Connected System's connector space.

    .DESCRIPTION
        Removes all Connected System Objects (CSOs) and their related data from a
        Connected System's connector space. This is typically used before re-importing
        data from the source system.

        The operation deletes CSOs, attribute values, pending exports, and deferred
        references. Metaverse Objects are not deleted — only the link between the CSO
        and MVO is severed.

        By default, change history is also deleted (recommended for re-import scenarios).
        Use -KeepChangeHistory to preserve the audit trail.

        The clear runs as a queued background task, tracked by an Activity, exactly like
        the portal: the cmdlet returns as soon as the task is queued, and the clear itself
        happens asynchronously. Use -Wait to block until it has finished.

    .PARAMETER Id
        The unique identifier of the Connected System to clear.

    .PARAMETER InputObject
        A Connected System object to clear. Accepts pipeline input.

    .PARAMETER KeepChangeHistory
        If specified, preserves change history records. The CSO foreign key on change
        records is nulled rather than the records being deleted.

        By default (without this switch), change history is deleted along with the CSOs.

    .PARAMETER Wait
        Waits for the queued clear to finish before returning. Without it the cmdlet
        returns as soon as the clear is queued, and a caller that immediately re-imports,
        or reads the Connector Space back, races the clear task.

    .PARAMETER Timeout
        Maximum seconds to wait when -Wait is supplied. Omit to wait indefinitely. A clear
        that has not finished by the timeout is reported as an error; it continues on the
        server regardless.

    .PARAMETER Force
        Suppresses confirmation prompts.

    .OUTPUTS
        A PSCustomObject tracking the queued clear:
        - ActivityId: the clear Activity's id (a GUID); monitor it with Get-JIMActivity
        - TaskId: the queued Worker Task's id (a GUID)
        - Message: a human-readable confirmation naming the Connected System

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
    [OutputType([PSCustomObject])]
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
        if ($Force -or $PSCmdlet.ShouldProcess($systemName, "Clear all objects from Connected System")) {
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
