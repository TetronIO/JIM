# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Enable-JIMFeature {
    <#
    .SYNOPSIS
        Enables a JIM feature flag.

    .DESCRIPTION
        Switches on a feature flag by key. Enabling an In Development flag (never shown in the
        portal; on only in development and the integration harness) requires -AllowInDevelopment;
        without it the server refuses the request. An audit activity is created for each change.

        Supports ShouldProcess; use -WhatIf or -Confirm to preview or confirm the operation.

    .PARAMETER Name
        The flag's key, e.g. "Features.UniqueValueGeneration".

    .PARAMETER AllowInDevelopment
        Required to enable an In Development flag. Used by the integration harness in scenario
        setup; a Preview-tier flag needs no such acknowledgement.

    .PARAMETER PassThru
        If specified, returns the updated feature flag object.

    .OUTPUTS
        If -PassThru is specified, returns the updated feature flag object.

    .EXAMPLE
        Enable-JIMFeature -Name "Features.SomePreviewFeature"

        Enables a Preview-tier feature flag.

    .EXAMPLE
        Enable-JIMFeature -Name "Features.UniqueValueGeneration" -AllowInDevelopment

        Enables an In Development feature flag, as the integration harness does in scenario setup.

    .LINK
        Get-JIMFeature
        Disable-JIMFeature
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)]
        [Alias('Key')]
        [string]$Name,

        [switch]$AllowInDevelopment,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess($Name, "Enable feature flag")) {
            Write-Verbose "Enabling feature flag: $Name"

            try {
                $body = @{
                    enabled            = $true
                    allowInDevelopment = $AllowInDevelopment.IsPresent
                }
                $response = Invoke-JIMApi -Endpoint "/api/v1/features/$Name" -Method 'PUT' -Body $body

                Write-Verbose "Enabled feature flag: $Name"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to enable feature flag '$Name': $_"
            }
        }
    }
}
