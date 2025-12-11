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
        API Key object to delete (from pipeline).

    .PARAMETER Force
        Suppresses confirmation prompts.

    .PARAMETER PassThru
        If specified, returns the deleted API Key object.

    .OUTPUTS
        If -PassThru is specified, returns the deleted API Key object.

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
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [switch]$Force,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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

        if ($Force -or $PSCmdlet.ShouldProcess($existing.name, "Delete API Key")) {
            Write-Verbose "Deleting API Key: $keyId"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/apikeys/$keyId" -Method 'DELETE'

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
