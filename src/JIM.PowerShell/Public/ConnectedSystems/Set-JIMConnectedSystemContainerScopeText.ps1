# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemContainerScopeText {
    <#
    .SYNOPSIS
        States a Connected System's Container Scope as text (Advanced Mode).

    .DESCRIPTION
        Replaces the whole of a Connected System's Container Scope with what the text states, one
        statement per line: include (or +) or exclude (or -), an optional one-level, then the
        Container's path. Blank lines and lines beginning with # are ignored.

        The text states the whole of Container Scope rather than a change to it, so a Container the text
        does not name states nothing: empty text clears every selection and exclusion. Partition
        selection is left alone, except that naming a Container selects the partition holding it.

        It is applied all-or-nothing. A path naming no Container, a Container named twice, and a
        statement an ancestor already makes are each refused with the line that caused them, and nothing
        is changed.

        This is a synchronisation-affecting change: taking a Container out of scope obsoletes the
        objects imported through it on the next Full Import, and the synchronisation after that
        disconnects them. Preview what it would cost first with New-JIMConfigurationChangePreview.

    .PARAMETER ConnectedSystemId
        The unique identifier of the Connected System.

    .PARAMETER Text
        The Container Scope to apply. Empty text clears every selection and exclusion.

    .PARAMETER PassThru
        If specified, returns the canonical text for the scope now in force.

    .OUTPUTS
        If -PassThru is specified, returns the canonical Container Scope text as a string.

    .EXAMPLE
        Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 1 -Text @"
        include OU=Corp,DC=example,DC=com
        exclude OU=Service Accounts,OU=Corp,DC=example,DC=com
        include OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=com
        "@

        Manages the corporate tree, carves the service accounts out of it, and brings one branch of
        those service accounts back into scope.

    .EXAMPLE
        Get-Content ./scope.txt -Raw | Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 1

        Applies a Container Scope held in a file, which is how a scope kept under version control is
        deployed.

    .EXAMPLE
        Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 1 -Text 'include one-level OU=Corp,DC=example,DC=com' -PassThru

        Manages only the objects held directly in OU=Corp, leaving the Containers beneath it to be
        selected in their own right, and reports back the canonical text now in force.

    .EXAMPLE
        Set-JIMConnectedSystemContainerScopeText -ConnectedSystemId 1 -Text ''

        Clears the whole Container Scope. Every object imported through those Containers is obsoleted
        on the next Full Import and disconnected by the synchronisation after it; run
        New-JIMConfigurationChangePreview first to see how many objects that is.

    .LINK
        Get-JIMConnectedSystemContainerScopeText
        New-JIMConfigurationChangePreview
        Set-JIMConnectedSystemContainer
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        # Not ValidateNotNullOrEmpty: empty text is the instruction to clear the scope, not a missing value.
        [Parameter(Mandatory, ValueFromPipeline)]
        [AllowEmptyString()]
        [string]$Text,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{ text = $Text }

        if ($PSCmdlet.ShouldProcess("Connected System $ConnectedSystemId", "Replace Container Scope")) {
            Write-Verbose "Applying Container Scope text to Connected System: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/container-scope-text" -Method 'PUT' -Body $body

                Write-Verbose "Applied Container Scope text to Connected System: $ConnectedSystemId"

                if ($PassThru) {
                    $result.Text
                }
            }
            catch {
                Write-Error "Failed to apply Container Scope text: $_"
            }
        }
    }
}
