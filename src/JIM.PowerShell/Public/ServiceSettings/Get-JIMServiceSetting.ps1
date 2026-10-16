# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMServiceSetting {
    <#
    .SYNOPSIS
        Gets service settings from JIM.

    .DESCRIPTION
        Retrieves service setting information from JIM. Can retrieve all settings or a
        specific setting by key. Settings control service-wide behaviour such as change
        tracking, sync page sizes, history retention, and other operational parameters.

    .PARAMETER Key
        The key of the one setting to return, such as "ChangeTracking.CsoChanges.Enabled".

    .OUTPUTS
        Each service setting, with its default, its stored value and the value in effect.

    .EXAMPLE
        Get-JIMServiceSetting

        Gets all service settings.

    .EXAMPLE
        Get-JIMServiceSetting -Key "ChangeTracking.CsoChanges.Enabled"

        Gets the CSO change tracking setting.

    .EXAMPLE
        Get-JIMServiceSetting | Where-Object { $_.category -eq "Synchronisation" }

        Gets all synchronisation-related settings.

    .LINK
        Set-JIMServiceSetting
        Reset-JIMServiceSetting
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.ServiceSettingDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ByKey', Position = 0)]
        [string]$Key
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ByKey' {
                Write-Verbose "Getting service setting: $Key"
                $result = Invoke-JIMApi -Endpoint "/api/v1/service-settings/$Key"
                $result
            }

            'List' {
                Write-Verbose "Getting all service settings"
                $response = Invoke-JIMApi -Endpoint "/api/v1/service-settings"

                # Output each setting individually for pipeline support
                foreach ($setting in $response) {
                    $setting
                }
            }
        }
    }
}
