function Reset-JIMServiceSetting {
    <#
    .SYNOPSIS
        Reverts a service setting to its default value in JIM.

    .DESCRIPTION
        Clears any override on a service setting, restoring it to the default value.
        Read-only settings cannot be reverted through this cmdlet.

        An audit activity is created for each revert operation.

    .PARAMETER Key
        The unique setting key using dot notation (e.g., "ChangeTracking.CsoChanges.Enabled").

    .PARAMETER PassThru
        If specified, returns the reverted service setting object.

    .OUTPUTS
        If -PassThru is specified, returns the reverted service setting object.

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
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string]$Key,

        [switch]$PassThru
    )

    process {
        if ($PSCmdlet.ShouldProcess($Key, "Revert service setting to default")) {
            Write-Verbose "Reverting service setting to default: $Key"

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/service-settings/$Key" -Method 'DELETE'

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
