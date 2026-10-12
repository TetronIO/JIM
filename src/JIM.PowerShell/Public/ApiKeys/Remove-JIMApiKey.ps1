# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMApiKey {
    <#
    .SYNOPSIS
        Removes an API Key from JIM.

    .DESCRIPTION
        Permanently deletes an API Key. Any requests using this key will fail immediately
        after deletion.

    .PARAMETER Id
        The unique identifier (GUID) of the API Key to delete.

    .PARAMETER InputObject
        The API Key to remove, as Get-JIMApiKey returns it.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the API Key's
        Configuration Change History.

    .PARAMETER Force
        Suppresses the confirmation prompt.

    .PARAMETER PassThru
        Returns the API Key as it was before it was removed.

    .OUTPUTS
        With -PassThru, the API Key as it was before it was removed; otherwise nothing.

    .EXAMPLE
        Remove-JIMApiKey -Id $keyId

        Removes the API Key with the specified ID (prompts for confirmation).

    .EXAMPLE
        Remove-JIMApiKey -Id $keyId -Force

        Removes the API Key without confirmation.

    .EXAMPLE
        Get-JIMApiKey | Where-Object { $_.name -like "Test*" } | Remove-JIMApiKey -Force

        Removes all API Keys with names starting with "Test".

    .LINK
        Get-JIMApiKey
        New-JIMApiKey
        Set-JIMApiKey
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.ApiKeyDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$Force,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $keyId = if ($InputObject) { $InputObject.id } else { $Id }

        # Get the key first for confirmation message and PassThru
        $existing = $null
        try {
            $existing = Invoke-JIMApi -Endpoint "/api/v1/apikeys/$keyId"
        }
        catch {
            Write-Error "API Key not found: $keyId"
            return
        }

        $confirmMessage = "Delete API Key '$($existing.name)' ($keyId)?"

        if (Confirm-JIMAction -Cmdlet $PSCmdlet -Target $existing.name -Action "Delete API Key" -Force:$Force) {
            Write-Verbose "Deleting API Key: $keyId"

            try {
                $endpoint = "/api/v1/apikeys/$keyId"
                if ($ChangeReason) {
                    $endpoint += "?changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                Invoke-JIMApi -Endpoint $endpoint -Method 'DELETE'

                Write-Verbose "Deleted API Key: $keyId"

                if ($PassThru) {
                    $existing
                }
            }
            catch {
                Write-Error "Failed to delete API Key: $_"
            }
        }
    }
}
