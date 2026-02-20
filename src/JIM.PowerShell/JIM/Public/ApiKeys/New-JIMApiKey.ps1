function New-JIMApiKey {
    <#
    .SYNOPSIS
        Creates a new API Key in JIM.

    .DESCRIPTION
        Creates a new API Key for non-interactive authentication. The full key value
        is returned only in the response from this cmdlet - store it securely as it
        cannot be retrieved again.

    .PARAMETER Name
        The name for the API Key.

    .PARAMETER Description
        Optional description for the API Key.

    .PARAMETER RoleIds
        Array of Role IDs to assign to this API Key.

    .PARAMETER ExpiresAt
        Optional expiry date for the API Key.

    .PARAMETER PassThru
        If specified, returns the created API Key object (including the full key value).

    .OUTPUTS
        If -PassThru is specified, returns the created API Key object with the full key.

    .EXAMPLE
        New-JIMApiKey -Name "CI/CD Pipeline" -PassThru

        Creates a new API Key and returns the result (including the full key).

    .EXAMPLE
        New-JIMApiKey -Name "Temp Key" -ExpiresAt (Get-Date).AddDays(30) -PassThru

        Creates an API Key that expires in 30 days.

    .EXAMPLE
        New-JIMApiKey -Name "Admin Key" -RoleIds @(1, 2) -Description "For admin scripts" -PassThru

        Creates an API Key with specific roles and description.

    .LINK
        Get-JIMApiKey
        Set-JIMApiKey
        Remove-JIMApiKey
        Get-JIMRole
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [string]$Description,

        [int[]]$RoleIds = @(),

        [datetime]$ExpiresAt,

        [switch]$PassThru
    )

    process {
        if ($PSCmdlet.ShouldProcess($Name, "Create API Key")) {
            Write-Verbose "Creating API Key: $Name"

            $body = @{
                name = $Name
                roleIds = $RoleIds
            }

            if ($Description) {
                $body.description = $Description
            }

            if ($PSBoundParameters.ContainsKey('ExpiresAt')) {
                $body.expiresAt = $ExpiresAt.ToUniversalTime().ToString('o')
            }

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/apikeys" -Method 'POST' -Body $body

                Write-Verbose "Created API Key with ID: $($response.id)"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to create API Key: $_"
            }
        }
    }
}
