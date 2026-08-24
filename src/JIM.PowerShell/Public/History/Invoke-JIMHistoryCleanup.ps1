# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Invoke-JIMHistoryCleanup {
    <#
    .SYNOPSIS
        Manually triggers change history cleanup based on retention policy.

    .DESCRIPTION
        Deletes history that has had the retention period set for its kind: Connected System Object
        changes, Metaverse Object changes, configuration change previews, Activities, initial-password
        records, and Pending Password Changes that reached a terminal state. Each class of record has
        its own retention Service Setting, and every trim is limited by the configured batch size to
        prevent long-running transactions.

        Records still being worked are never removed, however old: a Pending Password Change still owed
        to a Connected System, and an initial-password record still being retried, both survive.

        This runs on its own anyway, daily, on the built-in "History Retention Cleanup" Schedule. Use
        this cmdlet to run a pass on demand, or to drain a large backlog faster by calling it in a loop.

        This operation creates an Activity record to audit the cleanup.

    .PARAMETER PassThru
        If specified, returns the cleanup result object with deletion statistics.

    .OUTPUTS
        If -PassThru is specified, returns a PSCustomObject with cleanup statistics:
        - csoChangesDeleted: Number of Connected System Object change records deleted
        - mvoChangesDeleted: Number of Metaverse Object change records deleted
        - activitiesDeleted: Number of general Activity records deleted
        - configurationChangeActivitiesDeleted: Configuration change Activities deleted, at their own cutoff
        - securityEventActivitiesDeleted: Security event Activities deleted, at their own cutoff
        - initialPasswordWorkRecordsDeleted: Terminal initial-password records deleted
        - passwordEventActivitiesDeleted: Password Synchronisation Activities deleted, at their own cutoff
        - passwordQueueRecordsDeleted: Terminal Pending Password Changes deleted
        - oldestRecordDeleted: Oldest record timestamp deleted
        - newestRecordDeleted: Newest record timestamp deleted
        - cutoffDate: Records older than this date were deleted, under the general retention period
        - retentionPeriodDays: Configured general retention period
        - configurationChangeRetentionPeriodDays: Configured configuration change retention period
        - securityEventRetentionPeriodDays: Configured security event retention period
        - initialPasswordRetentionPeriodDays: Configured initial-password record retention period
        - passwordEventRetentionPeriodDays: Configured Password Synchronisation retention period
        - batchSize: Maximum records deleted per type in this batch

    .EXAMPLE
        Invoke-JIMHistoryCleanup

        Triggers a manual cleanup operation using the configured retention policy.

    .EXAMPLE
        Invoke-JIMHistoryCleanup -PassThru

        Triggers cleanup and returns the statistics.

    .EXAMPLE
        $result = Invoke-JIMHistoryCleanup -PassThru
        Write-Host "Deleted: CSO=$($result.csoChangesDeleted), MVO=$($result.mvoChangesDeleted), Activities=$($result.activitiesDeleted)"

        Triggers cleanup and displays deletion counts.

    .EXAMPLE
        # Clean up in batches until no more records to delete
        do {
            $result = Invoke-JIMHistoryCleanup -PassThru
            $totalDeleted = $result.csoChangesDeleted + $result.mvoChangesDeleted + $result.activitiesDeleted
            Write-Host "Batch deleted $totalDeleted records"
            Start-Sleep -Seconds 2
        } while ($totalDeleted -gt 0)

        Runs cleanup in batches with 2-second pauses until all expired records are deleted.

    .EXAMPLE
        Invoke-JIMHistoryCleanup -PassThru |
            Select-Object passwordEventActivitiesDeleted, passwordQueueRecordsDeleted

        Shows what the pass removed from Password Synchronisation history. The queue count is also the
        number of encrypted passwords JIM stopped holding.

    .LINK
        Get-JIMActivity

    .LINK
        Get-JIMServiceSetting
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter()]
        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Triggering manual history cleanup"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/history/cleanup" -Method 'POST'

            $totalDeleted = $result.csoChangesDeleted + $result.mvoChangesDeleted + $result.activitiesDeleted

            if ($totalDeleted -eq 0) {
                Write-Verbose "No expired records found to delete"
            } else {
                Write-Verbose "Cleanup completed: CSO changes=$($result.csoChangesDeleted), MVO changes=$($result.mvoChangesDeleted), Activities=$($result.activitiesDeleted)"
            }

            if ($PassThru) {
                $result
            }
        }
        catch {
            Write-Error "Failed to execute history cleanup: $_"
            throw
        }
    }
}
