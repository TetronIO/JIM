function Add-JIMCertificate {
    <#
    .SYNOPSIS
        Adds a certificate to JIM's trusted certificate store.

    .DESCRIPTION
        Adds a certificate from either a file path or raw certificate data.
        Both PEM and DER formats are supported.

    .PARAMETER Name
        The name for the certificate in the store.

    .PARAMETER Path
        Path to a certificate file (PEM or DER format). The path should be
        accessible from the JIM server (e.g., in the connector-files volume).

    .PARAMETER CertificateData
        Raw certificate data as a byte array.

    .PARAMETER CertificateBase64
        Certificate data as a Base64-encoded string.

    .PARAMETER Notes
        Optional notes about the certificate.

    .PARAMETER PassThru
        If specified, returns the created certificate object.

    .OUTPUTS
        If -PassThru is specified, returns the created certificate object.

    .EXAMPLE
        Add-JIMCertificate -Name "LDAP CA" -Path "/connector-files/ldap-ca.pem" -PassThru

        Adds a certificate from a file path.

    .EXAMPLE
        $certData = [System.IO.File]::ReadAllBytes("./cert.der")
        Add-JIMCertificate -Name "My Cert" -CertificateData $certData -PassThru

        Adds a certificate from raw byte data.

    .EXAMPLE
        $base64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes("./cert.der"))
        Add-JIMCertificate -Name "My Cert" -CertificateBase64 $base64 -PassThru

        Adds a certificate from Base64-encoded data.

    .LINK
        Get-JIMCertificate
        Set-JIMCertificate
        Remove-JIMCertificate
        Test-JIMCertificate
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'FromFile')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'FromFile')]
        [string]$Path,

        [Parameter(Mandatory, ParameterSetName = 'FromData')]
        [byte[]]$CertificateData,

        [Parameter(Mandatory, ParameterSetName = 'FromBase64')]
        [string]$CertificateBase64,

        [string]$Notes,

        [switch]$PassThru
    )

    process {
        if ($PSCmdlet.ShouldProcess($Name, "Add Certificate")) {
            Write-Verbose "Adding certificate: $Name"

            try {
                $response = $null

                switch ($PSCmdlet.ParameterSetName) {
                    'FromFile' {
                        $body = @{
                            name = $Name
                            filePath = $Path
                        }
                        if ($Notes) { $body.notes = $Notes }

                        $response = Invoke-JIMApi -Endpoint "/api/v1/certificates/file" -Method 'POST' -Body $body
                    }

                    'FromData' {
                        $base64 = [Convert]::ToBase64String($CertificateData)
                        $body = @{
                            name = $Name
                            certificateDataBase64 = $base64
                        }
                        if ($Notes) { $body.notes = $Notes }

                        $response = Invoke-JIMApi -Endpoint "/api/v1/certificates/upload" -Method 'POST' -Body $body
                    }

                    'FromBase64' {
                        $body = @{
                            name = $Name
                            certificateDataBase64 = $CertificateBase64
                        }
                        if ($Notes) { $body.notes = $Notes }

                        $response = Invoke-JIMApi -Endpoint "/api/v1/certificates/upload" -Method 'POST' -Body $body
                    }
                }

                Write-Verbose "Added certificate with ID: $($response.id)"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to add certificate: $_"
            }
        }
    }
}
