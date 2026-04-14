# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMMetaverseObjectRole {
    <#
    .SYNOPSIS
        Gets the security Roles a Metaverse Object is a member of.

    .DESCRIPTION
        Retrieves the list of security Roles that a Metaverse Object is statically
        assigned to. Returns an empty list if the object is not a member of any Role.

    .PARAMETER Id
        The unique identifier (GUID) of the Metaverse Object whose Roles to retrieve.

    .OUTPUTS
        PSCustomObject representing Role(s) the Metaverse Object is a member of.

    .EXAMPLE
        Get-JIMMetaverseObjectRole -Id "12345678-1234-1234-1234-123456789abc"

        Lists the Roles that the specified Metaverse Object is a member of.

    .EXAMPLE
        Get-JIMMetaverseObject -AttributeName 'Account Name' -AttributeValue 'jsmith' | Get-JIMMetaverseObjectRole

        Finds a Metaverse Object by account name and lists the Roles it is a member of.

    .LINK
        Get-JIMMetaverseObject
        Get-JIMRole
        Get-JIMRoleMember
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Guid]$Id
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        Write-Verbose "Getting roles of metaverse object: $Id"

        try {
            $response = Invoke-JIMApi -Endpoint "/api/v1/security/metaverse-objects/$Id/roles"

            foreach ($role in $response) {
                $role
            }
        }
        catch {
            Write-Error "Failed to get metaverse object roles: $_"
        }
    }
}
