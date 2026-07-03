# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Test-JIMFileSystemPath {
    <#
    .SYNOPSIS
        Checks whether a file system path is accessible to JIM.

    .DESCRIPTION
        Validates whether a given path falls within the JIM Container's allowed root
        directories. Use this before configuring a connector with a file path.

    .PARAMETER Path
        The path to validate.

    .OUTPUTS
        Boolean. $true if the path is within an allowed root, $false otherwise.

    .EXAMPLE
        Test-JIMFileSystemPath -Path "/data/imports/users.csv"

        Checks whether the given path is accessible.

    .LINK
        Get-JIMFileSystemItem
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)]
        [string]$Path
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Validating file system path: $Path"
        $result = Invoke-JIMApi -Endpoint "/api/v1/filesystem/validate?path=$([System.Uri]::EscapeDataString($Path))"
        $result.isAllowed
    }
}
