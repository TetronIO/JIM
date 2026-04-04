function Set-JIMServiceSetting {
    <#
    .SYNOPSIS
        Updates a service setting value in JIM.

    .DESCRIPTION
        Sets the value of a configurable service setting. Read-only settings (mirrored
        from environment variables) cannot be modified through this cmdlet.

        An audit activity is created for each update.

    .PARAMETER Key
        The unique setting key using dot notation (e.g., "ChangeTracking.CsoChanges.Enabled").

    .PARAMETER Value
        The new value for the setting as a string.

    .PARAMETER PassThru
        If specified, returns the updated service setting object.

    .OUTPUTS
        If -PassThru is specified, returns the updated service setting object.

    .EXAMPLE
        Set-JIMServiceSetting -Key "ChangeTracking.CsoChanges.Enabled" -Value "false"

        Disables CSO change tracking.

    .EXAMPLE
        Set-JIMServiceSetting -Key "Sync.PageSize" -Value "1000" -PassThru

        Sets the sync page size to 1000 and returns the updated setting.

    .LINK
        Get-JIMServiceSetting
        Reset-JIMServiceSetting
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string]$Key,

        [Parameter(Mandatory, Position = 1)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Value,

        [switch]$PassThru
    )

    process {
        if ($PSCmdlet.ShouldProcess($Key, "Update service setting to '$Value'")) {
            Write-Verbose "Updating service setting: $Key = $Value"

            try {
                $body = @{ value = $Value }
                $response = Invoke-JIMApi -Endpoint "/api/v1/service-settings/$Key" -Method 'PUT' -Body $body

                Write-Verbose "Updated service setting: $Key"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to update service setting '$Key': $_"
            }
        }
    }
}
