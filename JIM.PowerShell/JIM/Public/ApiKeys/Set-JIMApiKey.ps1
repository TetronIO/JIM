function Set-JIMApiKey {
    <#
    .SYNOPSIS
        Updates an existing API Key in JIM.

    .DESCRIPTION
        Updates an API Key's name, description, roles, expiry, or enabled status.
        The key value itself cannot be changed.

    .PARAMETER Id
        The unique identifier (GUID) of the API Key to update.

    .PARAMETER Name
        The new name for the API Key.

    .PARAMETER Description
        The new description for the API Key.

    .PARAMETER RoleIds
        Array of Role IDs to assign to this API Key.

    .PARAMETER ExpiresAt
        New expiry date for the API Key. Use $null to remove expiry.

    .PARAMETER Enable
        Enable the API Key.

    .PARAMETER Disable
        Disable the API Key.

    .PARAMETER PassThru
        If specified, returns the updated API Key object.

    .OUTPUTS
        If -PassThru is specified, returns the updated API Key object.

    .EXAMPLE
        Set-JIMApiKey -Id $keyId -Name "New Name" -PassThru

        Updates the API Key name and returns the result.

    .EXAMPLE
        Set-JIMApiKey -Id $keyId -Disable

        Disables the API Key.

    .EXAMPLE
        Set-JIMApiKey -Id $keyId -Enable -ExpiresAt (Get-Date).AddDays(90)

        Enables the API Key and sets a new expiry date.

    .EXAMPLE
        Get-JIMApiKey | Where-Object { $_.name -like "Test*" } | Set-JIMApiKey -Disable

        Disables all API Keys with names starting with "Test".

    .LINK
        Get-JIMApiKey
        New-JIMApiKey
        Remove-JIMApiKey
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [string]$Name,

        [string]$Description,

        [int[]]$RoleIds,

        [Nullable[datetime]]$ExpiresAt,

        [switch]$Enable,

        [switch]$Disable,

        [switch]$PassThru
    )

    process {
        if ($Enable -and $Disable) {
            Write-Error "Cannot specify both -Enable and -Disable"
            return
        }

        if ($PSCmdlet.ShouldProcess($Id, "Update API Key")) {
            Write-Verbose "Updating API Key: $Id"

            # Get existing key to preserve values not being updated
            $existing = Invoke-JIMApi -Endpoint "/api/v1/apikeys/$Id"
            if (-not $existing) {
                Write-Error "API Key not found: $Id"
                return
            }

            $body = @{
                name = if ($Name) { $Name } else { $existing.name }
                description = if ($PSBoundParameters.ContainsKey('Description')) { $Description } else { $existing.description }
                roleIds = if ($PSBoundParameters.ContainsKey('RoleIds')) { $RoleIds } else { @($existing.roles | ForEach-Object { $_.id }) }
                isEnabled = $existing.isEnabled
            }

            if ($PSBoundParameters.ContainsKey('ExpiresAt')) {
                $body.expiresAt = if ($ExpiresAt) { $ExpiresAt.ToUniversalTime().ToString('o') } else { $null }
            } else {
                $body.expiresAt = $existing.expiresAt
            }

            if ($Enable) {
                $body.isEnabled = $true
            } elseif ($Disable) {
                $body.isEnabled = $false
            }

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/apikeys/$Id" -Method 'PUT' -Body $body

                Write-Verbose "Updated API Key: $Id"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to update API Key: $_"
            }
        }
    }
}
