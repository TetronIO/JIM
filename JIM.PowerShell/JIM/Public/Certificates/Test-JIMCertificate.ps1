function Test-JIMCertificate {
    <#
    .SYNOPSIS
        Validates a certificate in JIM's trusted certificate store.

    .DESCRIPTION
        Performs validation checks on a certificate including expiry date,
        chain validation, and other certificate properties.

    .PARAMETER Id
        The unique identifier (GUID) of the certificate to validate.

    .OUTPUTS
        PSCustomObject representing the validation result with any warnings or errors.

    .EXAMPLE
        Test-JIMCertificate -Id $certId

        Validates the certificate and returns the result.

    .EXAMPLE
        Get-JIMCertificate | Test-JIMCertificate

        Validates all certificates.

    .EXAMPLE
        Get-JIMCertificate -EnabledOnly | Test-JIMCertificate | Where-Object { -not $_.isValid }

        Finds all enabled certificates that fail validation.

    .LINK
        Get-JIMCertificate
        Add-JIMCertificate
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id
    )

    process {
        Write-Verbose "Validating certificate: $Id"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/certificates/$Id/validate"
            $result
        }
        catch {
            Write-Error "Failed to validate certificate: $_"
        }
    }
}
