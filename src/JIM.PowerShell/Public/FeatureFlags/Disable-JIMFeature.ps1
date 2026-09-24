# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Disable-JIMFeature {
    <#
    .SYNOPSIS
        Disables a JIM feature flag.

    .DESCRIPTION
        Switches off a feature flag by key. Disabling never requires acknowledgement, whatever the
        flag's tier. An audit activity is created for each change.

        Supports ShouldProcess; use -WhatIf or -Confirm to preview or confirm the operation.

    .PARAMETER Name
        The flag's key, e.g. "Features.UniqueValueGeneration".

    .PARAMETER PassThru
        If specified, returns the updated feature flag object.

    .OUTPUTS
        If -PassThru is specified, returns the updated feature flag object.

    .EXAMPLE
        Disable-JIMFeature -Name "Features.UniqueValueGeneration"

        Disables the named feature flag.

    .EXAMPLE
        Get-JIMFeature -IncludeInDevelopment | Disable-JIMFeature

        Disables every feature flag currently returned (Preview and In Development).

    .LINK
        Get-JIMFeature
        Enable-JIMFeature
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)]
        [Alias('Key')]
        [string]$Name,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess($Name, "Disable feature flag")) {
            Write-Verbose "Disabling feature flag: $Name"

            try {
                $body = @{ enabled = $false }
                $response = Invoke-JIMApi -Endpoint "/api/v1/features/$Name" -Method 'PUT' -Body $body

                Write-Verbose "Disabled feature flag: $Name"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to disable feature flag '$Name': $_"
            }
        }
    }
}
