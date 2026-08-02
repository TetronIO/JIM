# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemDirectoryServer {
    <#
    .SYNOPSIS
        Discovers the domain controllers in a Connected System's directory.

    .DESCRIPTION
        Lists the domain controllers in an Active Directory or Samba AD forest, with the Active
        Directory Site each belongs to, using the Connected System's currently saved connectivity
        settings. Only Connected Systems using the LDAP connector against an AD-family directory
        support this; other connectors, and non-AD-family LDAP directories (OpenLDAP, Generic), return
        an error naming why.

        Purely informational: this never writes anything. It only helps you find a value for the
        Preferred Domain Controller setting; setting it is a separate call to Set-JIMConnectedSystem.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .OUTPUTS
        PSCustomObject per discovered domain controller: hostName (its FQDN) and site (the Active
        Directory Site it belongs to, or $null for directories without Sites).

    .EXAMPLE
        Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 1

        Lists the domain controllers discovered for Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 1 | Where-Object { $_.site -eq 'London' }

        Lists only the domain controllers in the London Active Directory Site.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Corp AD" | Get-JIMConnectedSystemDirectoryServer

        Discovers domain controllers for the Connected System named "Corp AD".

    .LINK
        Get-JIMConnectedSystem
        Set-JIMConnectedSystem
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Discovering directory servers for Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/directory-servers"

            # Output each directory server individually for pipeline support
            foreach ($directoryServer in $result) {
                $directoryServer
            }
        }
        catch {
            Write-Error "Failed to discover directory servers: $_"
        }
    }
}
