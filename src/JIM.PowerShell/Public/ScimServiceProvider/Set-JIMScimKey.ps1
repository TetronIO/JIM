# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMScimKey {
    <#
    .SYNOPSIS
        Updates a key an identity provider calls a Connected System's SCIM endpoint with.

    .DESCRIPTION
        Renames, re-describes, re-dates, disables or re-enables a SCIM key; only what is given changes. Disabling the
        old key is the last step of a rotation. A new expiry can be at most two years away, and re-enabling a key is
        refused while the Connected System already has two usable keys. The secret cannot be changed: to replace it,
        create a new key with New-JIMScimKey.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System the key is scoped to.

    .PARAMETER Id
        The ID of the key.

    .PARAMETER Name
        The key's new name.

    .PARAMETER Description
        The key's new description. $null or an empty string clears it.

    .PARAMETER ExpiresAt
        When the key stops working, at most two years from now.

    .PARAMETER Enable
        Lets the key be used again.

    .PARAMETER Disable
        Stops the key working, keeping it on record.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the key's Configuration Change
        History.

    .PARAMETER PassThru
        Returns the key as updated.

    .OUTPUTS
        With -PassThru, the key as updated; otherwise nothing.

    .EXAMPLE
        Set-JIMScimKey -ConnectedSystemId 12 -Id $oldKeyId -Disable -ChangeReason "Rotated to the 2026 key"

        Disables the old key once the identity provider is using the new one.

    .EXAMPLE
        Get-JIMScimKey -ConnectedSystemId 12 -Name "Entra provisioning 2025" | Set-JIMScimKey -Disable

        Disables the key with that name, finding it by name first.

    .LINK
        Get-JIMScimKey
        New-JIMScimKey
        Remove-JIMScimKey
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType('JIM.ApiKeyDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('ScopeConnectedSystemId')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [string]$Description,

        [datetime]$ExpiresAt,

        [switch]$Enable,

        [switch]$Disable,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($Enable -and $Disable) {
            Write-Error "Specify -Enable or -Disable, not both."
            return
        }

        if (-not $PSCmdlet.ShouldProcess("SCIM key $Id of Connected System $ConnectedSystemId", "Update SCIM key")) {
            return
        }

        # Only what was given is sent; the API leaves everything omitted as it is.
        $body = @{}
        if ($PSBoundParameters.ContainsKey('Name')) {
            $body.name = $Name
        }
        if ($PSBoundParameters.ContainsKey('Description')) {
            # $null binds as '' for a [string]; either clears the description.
            $body.description = $Description
        }
        if ($PSBoundParameters.ContainsKey('ExpiresAt')) {
            $body.expiresAt = $ExpiresAt.ToUniversalTime().ToString('o')
        }
        if ($Enable) {
            $body.isEnabled = $true
        }
        elseif ($Disable) {
            $body.isEnabled = $false
        }
        if ($ChangeReason) {
            $body.changeReason = $ChangeReason
        }

        try {
            $updated = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/scim/keys/$Id" -Method 'PUT' -Body $body
            Write-Verbose "Updated SCIM key $Id"
            if ($PassThru) {
                $updated
            }
        }
        catch {
            Write-Error "Failed to update the SCIM key: $_"
        }
    }
}
