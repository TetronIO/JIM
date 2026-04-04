function Get-JIMServiceSetting {
    <#
    .SYNOPSIS
        Gets service settings from JIM.

    .DESCRIPTION
        Retrieves service setting information from JIM. Can retrieve all settings or a
        specific setting by key. Settings control service-wide behaviour such as change
        tracking, sync page sizes, history retention, and other operational parameters.

    .PARAMETER Key
        The unique setting key using dot notation (e.g., "ChangeTracking.CsoChanges.Enabled").

    .OUTPUTS
        PSCustomObject representing service setting(s).

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
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ByKey', Position = 0)]
        [string]$Key
    )

    process {
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
