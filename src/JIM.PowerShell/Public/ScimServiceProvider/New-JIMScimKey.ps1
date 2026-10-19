# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMScimKey {
    <#
    .SYNOPSIS
        Creates a key for an identity provider to call a Connected System's SCIM endpoint with.

    .DESCRIPTION
        Creates a key scoped to the SCIM 2.0 Service Provider endpoint of one Connected System. The key carries no
        Roles, so it opens nothing else in JIM, and always expires: a year from now unless -ExpiresAt says otherwise,
        and at most two years. A Connected System can have at most two usable keys, so that a key can be rotated with
        an overlap: create the new key, give it to the identity provider, then disable or remove the old one.

        The secret is returned only by this cmdlet, in the Key property; JIM stores only its hash. Store it securely,
        or give it straight to the identity provider.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System, which must use the SCIM 2.0 Service Provider Connector.

    .PARAMETER Name
        The name the key is shown by, typically naming the identity provider that will hold it.

    .PARAMETER Description
        Optional description of what the key is for.

    .PARAMETER ExpiresAt
        When the key stops working. Omit it for a year from now; it can be at most two years away.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the key's Configuration Change
        History.

    .OUTPUTS
        The key created, with its secret in Key. This is the only time JIM returns the secret.

    .EXAMPLE
        $key = New-JIMScimKey -ConnectedSystemId 12 -Name "Entra provisioning 2026"
        $key.Key

        Creates a key that works for a year and shows its secret, to paste into the identity provider.

    .EXAMPLE
        New-JIMScimKey -ConnectedSystemId 12 -Name "Entra provisioning trial" -ExpiresAt (Get-Date).AddDays(30)

        Creates a key that works for 30 days.

    .LINK
        Get-JIMScimKey
        Set-JIMScimKey
        Remove-JIMScimKey
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType('JIM.ApiKeyCreateResponseDto')]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [string]$Description,

        [datetime]$ExpiresAt,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if (-not $PSCmdlet.ShouldProcess("Connected System $ConnectedSystemId", "Create SCIM key '$Name'")) {
            return
        }

        $body = @{ name = $Name }
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
            $created = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/scim/keys" -Method 'POST' -Body $body
            Write-Verbose "Created SCIM key $($created.id) for Connected System $ConnectedSystemId"
            $created
        }
        catch {
            Write-Error "Failed to create the SCIM key: $_"
        }
    }
}
