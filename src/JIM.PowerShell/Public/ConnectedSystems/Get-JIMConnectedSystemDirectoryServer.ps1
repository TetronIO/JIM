# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemDirectoryServer {
    <#
    .SYNOPSIS
        Discovers the directory servers in a Connected System's directory.

    .DESCRIPTION
        Lists the servers in a Connected System's directory, using its currently saved connectivity
        settings, for a setting the connector fills from discovery. For the LDAP connector's
        Preferred Domain Controller these are the domain controllers in an Active Directory or Samba AD
        forest, with the Active Directory Site each belongs to. Connectors that do not support
        discovery, and LDAP directories that are not AD-family (OpenLDAP, 389 Directory Server,
        Generic), return an error naming why.

        Purely informational: this never writes anything. It only helps you find a value for the
        setting; setting it is a separate call to Set-JIMConnectedSystem.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER Setting
        The name of the setting to discover servers for, when the connector discovers servers for more than one.
        Leave it out for the connector's first (Preferred Domain Controller, for the LDAP connector).

    .OUTPUTS
        One entry per server found: its HostName, the Active Directory Site it belongs to (null for directories
        that have no Sites), its Version where the directory records one (null for a domain controller), and the
        id and name of the setting its host name is a value for.

    .EXAMPLE
        Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 1

        Lists the directory servers discovered for Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 1 | Where-Object { $_.site -eq 'London' }

        Lists only the domain controllers in the London Active Directory Site.

    .EXAMPLE
        Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 1 -Setting 'Preferred Domain Controller'

        Lists the servers discovered for the Preferred Domain Controller setting, by name.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Corp AD" | Get-JIMConnectedSystemDirectoryServer

        Discovers domain controllers for the Connected System named "Corp AD".

    .EXAMPLE
        $server = Get-JIMConnectedSystemDirectoryServer -ConnectedSystemId 1 | Where-Object { $_.Site -eq 'London' } | Select-Object -First 1
        Set-JIMConnectedSystem -Id 1 -SettingValues @{ $server.TargetSettingId = @{ stringValue = $server.HostName } }

        Discovers a server in the London Site and sets it as the value of the setting discovery fills (Preferred
        Domain Controller, for the LDAP connector), without needing to know that setting's id.

    .LINK
        Get-JIMConnectedSystem
        Set-JIMConnectedSystem
    #>
    [CmdletBinding()]
    [OutputType('JIM.ConnectedSystemDirectoryServerDto')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$Setting
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Discovering directory servers for Connected System: $ConnectedSystemId"

        try {
            $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/directory-servers"
            if ($PSBoundParameters.ContainsKey('Setting')) {
                $endpoint += "?setting=$([Uri]::EscapeDataString($Setting))"
            }
            $result = Invoke-JIMApi -Endpoint $endpoint

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
