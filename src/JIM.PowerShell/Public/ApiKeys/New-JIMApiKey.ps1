# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMApiKey {
    <#
    .SYNOPSIS
        Creates a new API Key in JIM.

    .DESCRIPTION
        Creates a new API Key for non-interactive authentication. The full key value
        is returned only in the response from this cmdlet - store it securely as it
        cannot be retrieved again.

    .PARAMETER Name
        The name the API Key is shown by.

    .PARAMETER Description
        Optional description of what the API Key is for.

    .PARAMETER RoleIds
        The IDs of the Roles the API Key has.

    .PARAMETER ExpiresAt
        When the API Key expires. Omit it for a key that does not expire.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the API Key's
        Configuration Change History.

    .PARAMETER PassThru
        Returns the API Key created, including the key itself.

    .OUTPUTS
        With -PassThru, the API Key created, with the key itself in Key; otherwise nothing. This is the only
        time JIM returns the key, so store it securely: without -PassThru it cannot be retrieved.

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
    [OutputType('JIM.ApiKeyCreateResponseDto')]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [string]$Description,

        [int[]]$RoleIds = @(),

        [datetime]$ExpiresAt,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

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

            if ($ChangeReason) {
                $body.changeReason = $ChangeReason
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
