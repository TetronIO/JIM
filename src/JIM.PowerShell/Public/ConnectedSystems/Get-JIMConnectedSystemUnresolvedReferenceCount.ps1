# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemUnresolvedReferenceCount {
    <#
    .SYNOPSIS
        Counts the unresolved reference values in a Connected System's Connector Space.

    .DESCRIPTION
        Counts the reference values across a Connected System's Connected System Objects that JIM could not resolve
        to another Connected System Object: a group's member that is outside Container Scope, has not been imported
        yet, or does not exist. Run it after an import to check every reference resolved before synchronising.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .OUTPUTS
        How many reference values are unresolved; 0 when every one resolved.

    .EXAMPLE
        Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId 1

        Returns the number of unresolved references in Connected System 1.

    .EXAMPLE
        $count = Get-JIMConnectedSystemUnresolvedReferenceCount -ConnectedSystemId 1
        if ($count -gt 0) {
            Write-Warning "Found $count unresolved references - check container scope configuration"
        }

        Checks for unresolved references and warns if any are found.

    .LINK
        Get-JIMConnectedSystem
        Invoke-JIMRunProfile
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Getting unresolved reference count for Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/connector-space/unresolved-references/count"
            [int]$result
        }
        catch {
            Write-Error "Failed to get unresolved reference count: $_"
        }
    }
}
