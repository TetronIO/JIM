function Get-JIMCertificate {
    <#
    .SYNOPSIS
        Gets trusted certificates from JIM.

    .DESCRIPTION
        Retrieves trusted certificate information from JIM's certificate store.
        Can retrieve all certificates, enabled certificates only, or a specific
        certificate by ID.

    .PARAMETER Id
        The unique identifier (GUID) of a specific certificate to retrieve.

    .PARAMETER EnabledOnly
        If specified, returns only enabled certificates.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page. Defaults to 100.

    .OUTPUTS
        PSCustomObject representing certificate(s).

    .EXAMPLE
        Get-JIMCertificate

        Gets all certificates.

    .EXAMPLE
        Get-JIMCertificate -EnabledOnly

        Gets only enabled certificates.

    .EXAMPLE
        Get-JIMCertificate -Id "12345678-1234-1234-1234-123456789abc"

        Gets a specific certificate by ID.

    .LINK
        Add-JIMCertificate
        Set-JIMCertificate
        Remove-JIMCertificate
        Test-JIMCertificate
        Export-JIMCertificate
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(ParameterSetName = 'Enabled')]
        [switch]$EnabledOnly,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 1000)]
        [int]$PageSize = 100
    )

    process {
        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting certificate with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/certificates/$Id"
                $result
            }

            'Enabled' {
                Write-Verbose "Getting enabled certificates"
                $response = Invoke-JIMApi -Endpoint "/api/v1/certificates/enabled"

                # Output each certificate individually for pipeline support
                foreach ($cert in $response) {
                    $cert
                }
            }

            'List' {
                Write-Verbose "Getting all certificates"
                $queryParams = @(
                    "page=$Page",
                    "pageSize=$PageSize"
                )
                $queryString = $queryParams -join '&'
                $response = Invoke-JIMApi -Endpoint "/api/v1/certificates?$queryString"

                # Handle paginated response
                $certs = if ($response.items) { $response.items } else { $response }

                # Output each certificate individually for pipeline support
                foreach ($cert in $certs) {
                    $cert
                }
            }
        }
    }
}
