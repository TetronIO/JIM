# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMLogEntry {
    <#
    .SYNOPSIS
        Gets JIM service log entries.

    .DESCRIPTION
        Retrieves log entries from JIM's services (Web, Worker, Scheduler), with optional
        filtering by service, date, level, and search text. Also exposes the available
        log levels and service names, so callers can discover valid -Level/-Service values.

    .PARAMETER Service
        The service whose entries to return: web, worker, scheduler or database. Omit it for every service.

    .PARAMETER Date
        The day whose entries to return. Omit it for today (UTC).

    .PARAMETER Level
        The log levels to include: Verbose, Debug, Information, Warning, Error or Fatal. Omit it for every
        level.

    .PARAMETER Search
        Text the message or its exception must contain, ignoring case.

    .PARAMETER Limit
        The most entries to return.

    .PARAMETER Offset
        The number of entries to skip, for paging.

    .PARAMETER ListLevels
        Returns the log level names instead of log entries.

    .PARAMETER ListServices
        Returns the service names instead of log entries.

    .OUTPUTS
        Each log entry; -ListLevels and -ListServices return each name as a string.

    .EXAMPLE
        Get-JIMLogEntry

        Gets today's log entries across all services.

    .EXAMPLE
        Get-JIMLogEntry -Service worker -Level Warning, Error -Search "timeout"

        Gets Warning and Error log entries from the worker service mentioning "timeout".

    .EXAMPLE
        Get-JIMLogEntry -ListLevels

        Lists the available log level names.

    .LINK
        Get-JIMLogFile
    #>
    [CmdletBinding(DefaultParameterSetName = 'Entries')]
    [OutputType('JIM.LogEntryDto', ParameterSetName = 'Entries')]
    [OutputType([string], ParameterSetName = 'Levels')]
    [OutputType([string], ParameterSetName = 'Services')]
    param(
        [Parameter(ParameterSetName = 'Entries')]
        [string]$Service,

        [Parameter(ParameterSetName = 'Entries')]
        [datetime]$Date,

        [Parameter(ParameterSetName = 'Entries')]
        [string[]]$Level,

        [Parameter(ParameterSetName = 'Entries')]
        [string]$Search,

        [Parameter(ParameterSetName = 'Entries')]
        [ValidateRange(1, 5000)]
        [int]$Limit = 500,

        [Parameter(ParameterSetName = 'Entries')]
        [ValidateRange(0, [int]::MaxValue)]
        [int]$Offset = 0,

        [Parameter(Mandatory, ParameterSetName = 'Levels')]
        [switch]$ListLevels,

        [Parameter(Mandatory, ParameterSetName = 'Services')]
        [switch]$ListServices
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'Levels' {
                Write-Verbose "Getting available log levels"
                Invoke-JIMApi -Endpoint "/api/v1/logs/levels"
                return
            }

            'Services' {
                Write-Verbose "Getting available log services"
                Invoke-JIMApi -Endpoint "/api/v1/logs/services"
                return
            }
        }

        Write-Verbose "Getting log entries (Service: $Service, Limit: $Limit, Offset: $Offset)"

        $queryParams = @(
            "limit=$Limit",
            "offset=$Offset"
        )
        if ($Service) { $queryParams += "service=$([System.Uri]::EscapeDataString($Service))" }
        if ($Date) { $queryParams += "date=$($Date.ToString('o'))" }
        if ($Search) { $queryParams += "search=$([System.Uri]::EscapeDataString($Search))" }
        if ($Level) {
            foreach ($l in $Level) {
                $queryParams += "levels=$([System.Uri]::EscapeDataString($l))"
            }
        }

        $endpoint = "/api/v1/logs?" + ($queryParams -join '&')
        $response = Invoke-JIMApi -Endpoint $endpoint
        foreach ($entry in $response) {
            $entry
        }
    }
}
