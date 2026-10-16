# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMFeature {
    <#
    .SYNOPSIS
        Gets JIM's feature flags.

    .DESCRIPTION
        Retrieves feature flags: JIM's homegrown, off-by-default flags for features being rolled
        out gradually. Preview-tier flags are always returned; In Development flags (never shown
        in the portal, on only in development and the integration harness) are included only when
        -IncludeInDevelopment is specified.

    .PARAMETER Name
        The key of the one flag to return, such as "Features.SomeInDevelopmentFeature".

    .PARAMETER IncludeInDevelopment
        Also returns In Development flags. Intended for development and the integration harness;
        enabling one outside Development still requires -AllowInDevelopment on Enable-JIMFeature.

    .OUTPUTS
        Each feature flag.

    .EXAMPLE
        Get-JIMFeature

        Gets every Preview-tier feature flag.

    .EXAMPLE
        Get-JIMFeature -IncludeInDevelopment

        Gets every feature flag, including In Development ones (integration harness setup).

    .EXAMPLE
        Get-JIMFeature -Name "Features.SomeInDevelopmentFeature" -IncludeInDevelopment

        Gets a single In Development flag by key.

    .LINK
        Enable-JIMFeature
        Disable-JIMFeature
    #>
    [CmdletBinding()]
    [OutputType('JIM.FeatureFlagDto')]
    param(
        [Parameter(Position = 0)]
        [string]$Name,

        [switch]$IncludeInDevelopment
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting feature flags (IncludeInDevelopment: $IncludeInDevelopment)"
        $endpoint = "/api/v1/features?includeInDevelopment=$($IncludeInDevelopment.IsPresent.ToString().ToLower())"
        $response = Invoke-JIMApi -Endpoint $endpoint

        foreach ($flag in $response) {
            if ($Name -and $flag.key -ne $Name) {
                continue
            }
            $flag
        }
    }
}
