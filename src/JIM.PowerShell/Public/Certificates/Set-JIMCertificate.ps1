function Set-JIMCertificate {
    <#
    .SYNOPSIS
        Updates a certificate in JIM's trusted certificate store.

    .DESCRIPTION
        Updates a certificate's editable properties including name, notes,
        and enabled status. The certificate data itself cannot be changed.

    .PARAMETER Id
        The unique identifier (GUID) of the certificate to update.

    .PARAMETER Name
        New name for the certificate.

    .PARAMETER Notes
        New notes for the certificate.

    .PARAMETER Enable
        Enable the certificate.

    .PARAMETER Disable
        Disable the certificate.

    .PARAMETER PassThru
        If specified, returns the updated certificate object.

    .OUTPUTS
        If -PassThru is specified, returns the updated certificate object.

    .EXAMPLE
        Set-JIMCertificate -Id $certId -Name "New Name"

        Updates the certificate name.

    .EXAMPLE
        Set-JIMCertificate -Id $certId -Disable

        Disables the certificate.

    .EXAMPLE
        Get-JIMCertificate | Where-Object { $_.name -like "Test*" } | Set-JIMCertificate -Disable

        Disables all certificates with names starting with "Test".

    .LINK
        Get-JIMCertificate
        Add-JIMCertificate
        Remove-JIMCertificate
        Test-JIMCertificate
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [string]$Name,

        [string]$Notes,

        [switch]$Enable,

        [switch]$Disable,

        [switch]$PassThru
    )

    process {
        if ($Enable -and $Disable) {
            Write-Error "Cannot specify both -Enable and -Disable"
            return
        }

        if ($PSCmdlet.ShouldProcess($Id, "Update Certificate")) {
            Write-Verbose "Updating certificate: $Id"

            $body = @{}

            if ($Name) {
                $body.name = $Name
            }

            if ($PSBoundParameters.ContainsKey('Notes')) {
                $body.notes = $Notes
            }

            if ($Enable) {
                $body.isEnabled = $true
            } elseif ($Disable) {
                $body.isEnabled = $false
            }

            try {
                Invoke-JIMApi -Endpoint "/api/v1/certificates/$Id" -Method 'PATCH' -Body $body

                Write-Verbose "Updated certificate: $Id"

                if ($PassThru) {
                    # Fetch and return the updated certificate
                    Invoke-JIMApi -Endpoint "/api/v1/certificates/$Id"
                }
            }
            catch {
                Write-Error "Failed to update certificate: $_"
            }
        }
    }
}
