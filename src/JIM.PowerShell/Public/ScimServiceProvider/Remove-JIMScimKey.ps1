# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMScimKey {
    <#
    .SYNOPSIS
        Removes a key an identity provider calls a Connected System's SCIM endpoint with.

    .DESCRIPTION
        Permanently deletes a SCIM key. An identity provider still using it is refused from that moment. To keep a
        record of the key, disable it with Set-JIMScimKey instead.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System the key is scoped to.

    .PARAMETER Id
        The ID of the key.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity.

    .PARAMETER Force
        Suppresses the confirmation prompt.

    .PARAMETER PassThru
        Returns the key as it was before it was removed.

    .OUTPUTS
        With -PassThru, the key as it was before it was removed; otherwise nothing.

    .EXAMPLE
        Remove-JIMScimKey -ConnectedSystemId 12 -Id $oldKeyId

        Removes the key, asking for confirmation first.

    .EXAMPLE
        Get-JIMScimKey -ConnectedSystemId 12 | Where-Object { -not $_.IsEnabled } | Remove-JIMScimKey -Force

        Removes every disabled key of Connected System 12 without asking. Run it without -Force first to see which
        keys it would remove.

    .LINK
        Get-JIMScimKey
        New-JIMScimKey
        Set-JIMScimKey
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType('JIM.ApiKeyDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('ScopeConnectedSystemId')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$Force,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/scim/keys/$Id"
        try {
            $existing = Invoke-JIMApi -Endpoint $endpoint
        }
        catch {
            Write-Error "SCIM key $Id was not found on Connected System ${ConnectedSystemId}: $_"
            return
        }

        if (-not (Confirm-JIMAction -Cmdlet $PSCmdlet -Target "SCIM key '$($existing.name)'" -Action "Delete" -Force:$Force)) {
            return
        }

        if ($ChangeReason) {
            $endpoint += "?changeReason=$([uri]::EscapeDataString($ChangeReason))"
        }

        try {
            Invoke-JIMApi -Endpoint $endpoint -Method 'DELETE'
            Write-Verbose "Deleted SCIM key $Id"
            if ($PassThru) {
                $existing
            }
        }
        catch {
            Write-Error "Failed to delete the SCIM key: $_"
        }
    }
}
