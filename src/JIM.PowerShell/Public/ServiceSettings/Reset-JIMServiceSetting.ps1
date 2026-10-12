# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Reset-JIMServiceSetting {
    <#
    .SYNOPSIS
        Reverts a service setting to its default value in JIM.

    .DESCRIPTION
        Clears any override on a service setting, restoring it to the default value.
        Read-only settings cannot be reverted through this cmdlet.

        An audit activity is created for each revert operation.

    .PARAMETER Key
        The key of the setting to revert, such as "ChangeTracking.CsoChanges.Enabled".

    .PARAMETER ChangeReason
        Optional reason for the revert, recorded on the audit Activity and shown in the setting's
        Configuration Change History.

    .PARAMETER PassThru
        Returns the setting as reverted.

    .OUTPUTS
        With -PassThru, the setting as reverted; otherwise nothing.

    .EXAMPLE
        Reset-JIMServiceSetting -Key "ChangeTracking.CsoChanges.Enabled"

        Reverts the CSO change tracking setting to its default value.

    .EXAMPLE
        Get-JIMServiceSetting | Where-Object { $_.isOverridden } | ForEach-Object {
            Reset-JIMServiceSetting -Key $_.key
        }

        Reverts all overridden settings to their defaults.

    .LINK
        Get-JIMServiceSetting
        Set-JIMServiceSetting
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.ServiceSettingDto')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)]
        [string]$Key,

        [Parameter()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess($Key, "Revert service setting to default")) {
            Write-Verbose "Reverting service setting to default: $Key"

            try {
                $endpoint = "/api/v1/service-settings/$Key"
                if ($ChangeReason) {
                    $endpoint += "?changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                $response = Invoke-JIMApi -Endpoint $endpoint -Method 'DELETE'

                Write-Verbose "Reverted service setting: $Key"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to revert service setting '$Key': $_"
            }
        }
    }
}
