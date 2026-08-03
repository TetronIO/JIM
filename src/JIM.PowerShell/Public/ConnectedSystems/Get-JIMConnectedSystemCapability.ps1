# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemCapability {
    <#
    .SYNOPSIS
        Gets the Connector-detected capabilities for a Connected System in JIM.

    .DESCRIPTION
        Retrieves the human-readable facts a Connected System's Connector has detected about the target
        system, e.g. an LDAP directory's type, vendor, DNS host name, and paging support. These are
        discovered from the target during a previous connection and persisted by JIM; calling this cmdlet
        does not trigger a new connection.

        Returns an empty result when the Connector does not detect any capabilities, or when nothing has
        been detected yet (for example, before the first successful connection or schema import).

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .OUTPUTS
        PSCustomObject representing each detected capability, with Name and Value properties.

    .EXAMPLE
        Get-JIMConnectedSystemCapability -ConnectedSystemId 1

        Gets the detected capabilities for Connected System 1.

    .EXAMPLE
        Get-JIMConnectedSystem -Name "Active Directory" | Get-JIMConnectedSystemCapability

        Gets the detected capabilities for the Connected System named "Active Directory".

    .LINK
        Get-JIMConnectedSystem
        Import-JIMConnectedSystemHierarchy
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

        Write-Verbose "Getting detected capabilities for Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/capabilities"

            # Output each capability individually for pipeline support
            foreach ($capability in $result) {
                $capability
            }
        }
        catch {
            Write-Error "Failed to get Connected System capabilities: $_"
        }
    }
}
