# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMConfigurationTestCase {
    <#
    .SYNOPSIS
        Deletes a saved configuration test case.

    .DESCRIPTION
        Deletes a configuration test case. The deletion is recorded in the Configuration History of what owned it.
        Nothing the case covered changes.

    .PARAMETER Id
        The ID of the test case to delete.

    .PARAMETER Force
        Suppresses the confirmation prompt.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMConfigurationTestCase -Id 4

        Deletes test case 4, asking first.

    .EXAMPLE
        Get-JIMConfigurationTestCase -SyncRuleId 12 -Name 'Draft*' | Remove-JIMConfigurationTestCase -Force

        Deletes every test case covering Synchronisation Rule 12 whose name starts with "Draft", without asking. Run
        the Get-JIMConfigurationTestCase part alone first to see which cases match.

    .LINK
        Get-JIMConfigurationTestCase
        New-JIMConfigurationTestCase
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([void])]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [switch]$Force
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if (Confirm-JIMAction -Cmdlet $PSCmdlet -Target "Configuration test case $Id" -Action 'Remove' -Force:$Force) {
            Write-Verbose "Removing configuration test case $Id"

            try {
                Invoke-JIMApi -Endpoint "/api/v1/configuration-tests/$Id" -Method 'DELETE'
            }
            catch {
                Write-Error "Failed to remove configuration test case ${Id}: $_"
            }
        }
    }
}
