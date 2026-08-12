# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemContainerScopeText {
    <#
    .SYNOPSIS
        Reads a Connected System's Container Scope as text (Advanced Mode).

    .DESCRIPTION
        Returns the Containers a Connected System manages as one statement per line, in hierarchy order:
        include or exclude, an optional one-level, then the Container's path.

        This is the canonical form, so text read here and passed straight back to
        Set-JIMConnectedSystemContainerScopeText leaves the scope exactly as it was. That makes it the
        way to review, diff, version-control or copy a Container Scope of any size, where reading it a
        Container at a time through Get-JIMConnectedSystemPartition is impractical.

        A Connected System with nothing selected returns empty text.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .OUTPUTS
        System.String. The Container Scope, one statement per line.

    .EXAMPLE
        Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 1

        Reads the Container Scope, for example:

            include OU=Corp,DC=example,DC=com
            exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
            include OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com

    .EXAMPLE
        Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 1 | Set-Content ./scope.txt

        Saves the Container Scope to a file, so it can be reviewed, kept under version control, or
        edited and applied back with Set-JIMConnectedSystemContainerScopeText.

    .EXAMPLE
        Get-JIMConnectedSystemContainerScopeText -ConnectedSystemId 1 |
            Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 2

        Copies the Container Scope from one Connected System to another. Every path has to name a
        Container the target system has discovered, or nothing is applied.

    .LINK
        Set-JIMConnectedSystemContainerScopeText
        Get-JIMConnectedSystemPartition
        Set-JIMConnectedSystemContainer
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        Write-Verbose "Retrieving Container Scope text for Connected System: $ConnectedSystemId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/container-scope-text" -Method 'GET'

            # The text itself, not the object carrying it, so it pipes straight into Set-Content or into
            # Set-JIMConnectedSystemContainerScopeText.
            $result.Text
        }
        catch {
            Write-Error "Failed to retrieve Container Scope text: $_"
        }
    }
}
