# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMFileSystemItem {
    <#
    .SYNOPSIS
        Browses the JIM Container's file system.

    .DESCRIPTION
        Lists files and directories within the JIM Container's allowed mount points, used
        when configuring file-based connectors (e.g. CSV) to select import/export paths.
        Only paths within the configured allowed roots are accessible.

    .PARAMETER Path
        The directory path to list. Omit to list the allowed root directories.

    .PARAMETER Roots
        If specified, returns the allowed root directory paths instead of listing a directory.

    .OUTPUTS
        PSCustomObject representing a directory listing, or a list of allowed root paths.

    .EXAMPLE
        Get-JIMFileSystemItem

        Lists the allowed root directories.

    .EXAMPLE
        Get-JIMFileSystemItem -Path "/data/imports"

        Lists the contents of the given directory.

    .EXAMPLE
        Get-JIMFileSystemItem -Roots

        Returns the allowed root directory paths.

    .LINK
        Test-JIMFileSystemPath
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(ParameterSetName = 'List')]
        [string]$Path,

        [Parameter(Mandatory, ParameterSetName = 'Roots')]
        [switch]$Roots
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'Roots') {
            Write-Verbose "Getting allowed file system roots"
            Invoke-JIMApi -Endpoint "/api/v1/filesystem/roots"
            return
        }

        Write-Verbose "Listing file system path: $(if ($Path) { $Path } else { '(root)' })"
        $endpoint = "/api/v1/filesystem/list"
        if ($Path) {
            $endpoint += "?path=$([System.Uri]::EscapeDataString($Path))"
        }

        Invoke-JIMApi -Endpoint $endpoint
    }
}
