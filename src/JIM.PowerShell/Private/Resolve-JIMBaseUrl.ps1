# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Resolve-JIMBaseUrl {
    <#
    .SYNOPSIS
        Resolves a base URL for anonymous API calls.

    .DESCRIPTION
        Internal helper function that resolves the JIM base URL to use for anonymous
        endpoints. If a URL is provided directly, it is returned as-is. Otherwise,
        falls back to the URL from the current Connect-JIM session. Throws an error
        if neither is available.

    .PARAMETER Url
        An explicit base URL. If provided, takes precedence over the session URL.

    .OUTPUTS
        The resolved base URL string.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [string]$Url
    )

    if ($Url) {
        return $Url
    }

    if ($script:JIMConnection -and $script:JIMConnection.Url) {
        return $script:JIMConnection.Url
    }

    Write-Error "No URL specified. Provide -Url or connect first with Connect-JIM."
    return $null
}
