function Export-JIMCertificate {
    <#
    .SYNOPSIS
        Exports a certificate from JIM's trusted certificate store.

    .DESCRIPTION
        Downloads the raw certificate data in DER format. The certificate can
        be converted to PEM format using standard tools like OpenSSL if needed.

    .PARAMETER Id
        The unique identifier (GUID) of the certificate to export.

    .PARAMETER Path
        The file path to save the certificate to.

    .PARAMETER Force
        Overwrite the file if it already exists.

    .PARAMETER PassThru
        If specified, returns the certificate bytes instead of saving to a file.

    .OUTPUTS
        If -PassThru is specified, returns the certificate as a byte array.
        Otherwise, creates a file at the specified path.

    .EXAMPLE
        Export-JIMCertificate -Id $certId -Path "./my-cert.cer"

        Exports the certificate to a file.

    .EXAMPLE
        Export-JIMCertificate -Id $certId -Path "./my-cert.cer" -Force

        Exports the certificate, overwriting if the file exists.

    .EXAMPLE
        $bytes = Export-JIMCertificate -Id $certId -PassThru
        [System.IO.File]::WriteAllBytes("./cert.der", $bytes)

        Gets the certificate bytes for custom processing.

    .LINK
        Get-JIMCertificate
        Add-JIMCertificate
        Test-JIMCertificate
    #>
    [CmdletBinding(DefaultParameterSetName = 'ToFile')]
    [OutputType([byte[]])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ToFile')]
        [string]$Path,

        [Parameter(ParameterSetName = 'ToFile')]
        [switch]$Force,

        [Parameter(Mandatory, ParameterSetName = 'PassThru')]
        [switch]$PassThru
    )

    process {
        Write-Verbose "Exporting certificate: $Id"

        # Check if connection exists
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        try {
            $uri = "$($script:JIMConnection.Url)/api/v1/certificates/$Id/download"

            $headers = @{
                'X-API-Key' = $script:JIMConnection.ApiKey
            }

            # Use Invoke-WebRequest to get binary data
            $response = Invoke-WebRequest -Uri $uri -Headers $headers -Method Get

            if ($PassThru) {
                return $response.Content
            }

            # Save to file
            $fullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)

            if ((Test-Path $fullPath) -and -not $Force) {
                Write-Error "File already exists: $fullPath. Use -Force to overwrite."
                return
            }

            [System.IO.File]::WriteAllBytes($fullPath, $response.Content)
            Write-Verbose "Certificate exported to: $fullPath"
        }
        catch {
            Write-Error "Failed to export certificate: $_"
        }
    }
}
