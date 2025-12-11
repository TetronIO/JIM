function Remove-JIMCertificate {
    <#
    .SYNOPSIS
        Removes a certificate from JIM's trusted certificate store.

    .DESCRIPTION
        Permanently deletes a certificate from the trusted certificate store.

    .PARAMETER Id
        The unique identifier (GUID) of the certificate to delete.

    .PARAMETER InputObject
        Certificate object to delete (from pipeline).

    .PARAMETER Force
        Suppresses confirmation prompts.

    .PARAMETER PassThru
        If specified, returns the deleted certificate object.

    .OUTPUTS
        If -PassThru is specified, returns the deleted certificate object.

    .EXAMPLE
        Remove-JIMCertificate -Id $certId

        Removes the certificate (prompts for confirmation).

    .EXAMPLE
        Remove-JIMCertificate -Id $certId -Force

        Removes the certificate without confirmation.

    .EXAMPLE
        Get-JIMCertificate | Where-Object { $_.name -like "Test*" } | Remove-JIMCertificate -Force

        Removes all certificates with names starting with "Test".

    .LINK
        Get-JIMCertificate
        Add-JIMCertificate
        Set-JIMCertificate
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

        $certId = if ($InputObject) { $InputObject.id } else { $Id }

        # Get the certificate first for confirmation message and PassThru
        $existing = $null
        try {
            $existing = Invoke-JIMApi -Endpoint "/api/v1/certificates/$certId"
        }
        catch {
            Write-Error "Certificate not found: $certId"
            return
        }

        if ($Force -or $PSCmdlet.ShouldProcess($existing.name, "Delete Certificate")) {
            Write-Verbose "Deleting certificate: $certId"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/certificates/$certId" -Method 'DELETE'

                Write-Verbose "Deleted certificate: $certId"

                if ($PassThru) {
                    $existing
                }
            }
            catch {
                Write-Error "Failed to delete certificate: $_"
            }
        }
    }
}
