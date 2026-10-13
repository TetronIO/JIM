# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

    .PARAMETER Name
        Filters certificates by name, such as "Contoso*".

    .PARAMETER EnabledOnly
        Returns only the certificates that are enabled.

    .PARAMETER Page
        The page of results to return.

    .PARAMETER PageSize
        The number of certificates on each page.

    .OUTPUTS
        Listing returns each certificate's summary; -Id returns the certificate in full.

    .EXAMPLE
        Get-JIMCertificate

        Gets all certificates.

    .EXAMPLE
        Get-JIMCertificate -Name "Contoso*"

        Gets all certificates with names starting with "Contoso".

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
    [OutputType('JIM.TrustedCertificateHeader', ParameterSetName = 'List')]
    [OutputType('JIM.TrustedCertificateHeader', ParameterSetName = 'Enabled')]
    [OutputType('JIM.TrustedCertificateDetailDto', ParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Guid]$Id,

        [Parameter(ParameterSetName = 'List')]
        [Parameter(ParameterSetName = 'Enabled')]
        [SupportsWildcards()]
        [string]$Name,

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
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting certificate with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/certificates/$Id"
                $result
            }

            'Enabled' {
                Write-Verbose "Getting enabled certificates"
                $response = Invoke-JIMApi -Endpoint "/api/v1/certificates/enabled"

                # Filter by name if specified
                if ($Name) {
                    Write-Verbose "Filtering by name pattern: $Name"
                    $response = $response | Where-Object { $_.name -like $Name }
                }

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

                # Handle paginated response. Test for the 'items' property, not its truthiness:
                # an empty page has items = @() (falsy), and falling through to $response would
                # emit the pagination envelope itself as if it were a certificate.
                $certs = if ($response.PSObject.Properties.Name -contains 'items') { $response.items } else { $response }

                # Filter by name if specified
                if ($Name) {
                    Write-Verbose "Filtering by name pattern: $Name"
                    $certs = $certs | Where-Object { $_.name -like $Name }
                }

                # Output each certificate individually for pipeline support
                foreach ($cert in $certs) {
                    $cert
                }
            }
        }
    }
}
