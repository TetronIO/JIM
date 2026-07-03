# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Watch-JIMLog {
    <#
    .SYNOPSIS
        Streams JIM service log entries to the console.

    .DESCRIPTION
        Continuously polls JIM's Logs API and displays new log entries as they arrive,
        colour-coded by level (red for Error/Fatal, yellow for Warning, dark grey for
        Debug/Verbose). The first poll establishes a baseline; subsequent polls display
        only entries newer than the last one seen.

        Runs until interrupted with Ctrl+C (or until -MaxPolls cycles have completed).
        Transient API failures are reported as warnings and polling continues.

        Output is formatted for the console. For structured log entry objects suitable
        for the pipeline, use Get-JIMLogEntry instead.

    .PARAMETER Service
        Filter by service name (web, worker, scheduler). Omit for all services.

    .PARAMETER Level
        Specific log levels to include (Verbose, Debug, Information, Warning, Error, Fatal).
        Omit for all levels.

    .PARAMETER Search
        Text to search for in the log message (case-insensitive).

    .PARAMETER IntervalSeconds
        Number of seconds to wait between polls (1-300). Defaults to 2.

    .PARAMETER MaxPolls
        Maximum number of poll cycles to run before stopping. Omit to run until
        interrupted with Ctrl+C. Useful for bounded watches in scripts.

    .OUTPUTS
        None. Log entries are written to the console as formatted, colour-coded lines.

    .EXAMPLE
        Watch-JIMLog

        Streams log entries from all services until interrupted with Ctrl+C.

    .EXAMPLE
        Watch-JIMLog -Service worker -Level Error, Fatal

        Streams only Error and Fatal entries from the worker service.

    .EXAMPLE
        Watch-JIMLog -IntervalSeconds 5

        Streams log entries, polling the API every 5 seconds.

    .LINK
        Get-JIMLogEntry

    .LINK
        Get-JIMLogFile
    #>
    [CmdletBinding()]
    [OutputType([void])]
    param(
        [string]$Service,

        [string[]]$Level,

        [string]$Search,

        [ValidateRange(1, 300)]
        [int]$IntervalSeconds = 2,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$MaxPolls
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Build the query string once; the filters do not change between polls.
        # Entries come back newest first, so a fixed limit gives us the most recent
        # window on every poll.
        $queryParams = @(
            "limit=200",
            "offset=0"
        )
        if ($Service) { $queryParams += "service=$([System.Uri]::EscapeDataString($Service))" }
        if ($Search) { $queryParams += "search=$([System.Uri]::EscapeDataString($Search))" }
        if ($Level) {
            foreach ($l in $Level) {
                $queryParams += "levels=$([System.Uri]::EscapeDataString($l))"
            }
        }
        $endpoint = "/api/v1/logs?" + ($queryParams -join '&')

        Write-Host "Watching JIM logs (polling every $IntervalSeconds seconds). Press Ctrl+C to stop." -ForegroundColor Cyan

        $lastSeen = [datetime]::MinValue
        $baselineEstablished = $false
        $pollIndex = 0

        while ($true) {
            $pollIndex++

            try {
                $entries = @(Invoke-JIMApi -Endpoint $endpoint)

                if (-not $baselineEstablished) {
                    # First successful poll: record the newest timestamp without
                    # displaying anything, so only entries logged from now on appear.
                    foreach ($entry in $entries) {
                        if ($entry.Timestamp -gt $lastSeen) { $lastSeen = $entry.Timestamp }
                    }
                    $baselineEstablished = $true
                }
                else {
                    $newEntries = @($entries | Where-Object { $_.Timestamp -gt $lastSeen } | Sort-Object Timestamp)
                    foreach ($entry in $newEntries) {
                        $levelLabel = if ($entry.LevelShort) { $entry.LevelShort } else { $entry.Level }
                        $line = "[{0:yyyy-MM-dd HH:mm:ss}] [{1}] [{2}] {3}" -f $entry.Timestamp, $levelLabel, $entry.Service, $entry.Message
                        $colour = Get-JIMLogLevelColour -Level $entry.Level
                        if ($colour) {
                            Write-Host $line -ForegroundColor $colour
                        }
                        else {
                            Write-Host $line
                        }
                        if ($entry.Exception) {
                            Write-Host $entry.Exception -ForegroundColor DarkGray
                        }
                        if ($entry.Timestamp -gt $lastSeen) { $lastSeen = $entry.Timestamp }
                    }
                }
            }
            catch {
                # Transient API failures must not end the watch; warn and keep polling.
                Write-Warning "Failed to retrieve log entries: $($_.Exception.Message). Retrying in $IntervalSeconds seconds."
            }

            if ($MaxPolls -gt 0 -and $pollIndex -ge $MaxPolls) { break }
            Start-Sleep -Seconds $IntervalSeconds
        }
    }
}
