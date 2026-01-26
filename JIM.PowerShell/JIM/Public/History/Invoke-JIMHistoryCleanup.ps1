function Invoke-JIMHistoryCleanup {
    <#
    .SYNOPSIS
        Manually triggers change history cleanup based on retention policy.

    .DESCRIPTION
        Deletes expired CSO changes, MVO changes, and Activities older than the configured
        retention period. The cleanup is limited by the configured batch size to prevent
        long-running transactions.

        For large volumes of data, call this cmdlet multiple times or rely on the automatic
        housekeeping cleanup that runs every 60 seconds.

        This operation creates an Activity record to audit the cleanup.

    .PARAMETER PassThru
        If specified, returns the cleanup result object with deletion statistics.

    .OUTPUTS
        If -PassThru is specified, returns a PSCustomObject with cleanup statistics:
        - csoChangesDeleted: Number of CSO change records deleted
        - mvoChangesDeleted: Number of MVO change records deleted
        - activitiesDeleted: Number of Activity records deleted
        - oldestRecordDeleted: Oldest record timestamp deleted
        - newestRecordDeleted: Newest record timestamp deleted
        - cutoffDate: Records older than this date were deleted
        - retentionPeriodDays: Configured retention period
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

    .LINK
        Get-JIMActivity
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter()]
        [switch]$PassThru
    )

    process {
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
