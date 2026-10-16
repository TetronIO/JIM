# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMLogFile {
    <#
    .SYNOPSIS
        Lists the JIM service log files available on the server.

    .DESCRIPTION
        Retrieves metadata (service, date, size) for every log file JIM currently has on
        disk, across all services (Web, Worker, Scheduler).

    .OUTPUTS
        Each log file's service, date and size.

    .EXAMPLE
        Get-JIMLogFile

        Lists all available log files.

    .EXAMPLE
        Get-JIMLogFile | Where-Object Service -eq 'worker'

        Lists log files for the worker service only.

    .LINK
        Get-JIMLogEntry
    #>
    [CmdletBinding()]
    [OutputType('JIM.LogFileDto')]
    param()

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting log files"
        $response = Invoke-JIMApi -Endpoint "/api/v1/logs/files"
        foreach ($file in $response) {
            $file
        }
    }
}
