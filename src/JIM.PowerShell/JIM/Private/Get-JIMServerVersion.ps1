function Get-JIMServerVersion {
    <#
    .SYNOPSIS
        Internal function to fetch the JIM server version.

    .DESCRIPTION
        Calls the /api/v1/health/version endpoint to retrieve the server version.
        Stores the version on the connection object for later use.
        Returns $null gracefully if the endpoint is not available (older server).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    try {
        $versionInfo = Invoke-JIMApi -Endpoint '/api/v1/health/version'
        $version = $versionInfo.version

        if ($version -and $script:JIMConnection) {
            $script:JIMConnection | Add-Member -NotePropertyName 'ServerVersion' -NotePropertyValue $version -Force
        }

        return $version
    }
    catch {
        Write-Verbose "Could not retrieve server version: $_"
        return $null
    }
}
