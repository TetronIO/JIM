# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMScopingCriteriaGroup {
    <#
    .SYNOPSIS
        Deletes a scoping criteria group and everything in it.

    .DESCRIPTION
        Deletes a scoping criteria group, with every criterion and child group inside it.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER GroupId
        The ID of the group to delete.

    .PARAMETER SaveAnyway
        Saves even though the change stops configuration test cases passing. Without it, or
        -AcceptNewResult, such a change is refused, nothing is saved, and the error names each case.
        Configuration History records the cases saved past, by name.

    .PARAMETER AcceptNewResult
        Takes what the configuration now gives as what each configuration test case that stopped
        passing expects, rewriting those cases, and saves. A case whose new result cannot be
        expected (it needs review, could not run, or its Scope cannot be determined) still refuses
        the change unless -SaveAnyway is given too.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 10

        Removes the scoping criteria group with ID 10.

    .EXAMPLE
        Get-JIMScopingCriteria -SyncRuleId 5 | Remove-JIMScopingCriteriaGroup

        Removes all scoping criteria groups from Synchronisation Rule 5.

    .LINK
        Get-JIMScopingCriteria
        New-JIMScopingCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [OutputType([void])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$GroupId,

        [switch]$SaveAnyway,

        [switch]$AcceptNewResult
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess("Scoping Criteria Group $GroupId", "Remove")) {
            Write-Verbose "Removing scoping criteria group $GroupId from Synchronisation Rule $SyncRuleId"

            try {
                # The save gate (#2055 Phase 4b): what to do if the change stops configuration test cases passing.
                $gate = @()
                if ($SaveAnyway) { $gate += 'saveAnyway=true' }
                if ($AcceptNewResult) { $gate += 'acceptNewResults=true' }
                $query = if ($gate.Count -gt 0) { '?' + ($gate -join '&') } else { '' }
                Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/$GroupId$query" -Method 'DELETE'

                Write-Verbose "Removed scoping criteria group $GroupId"
            }
            catch {
                Write-Error "Failed to remove scoping criteria group: $_"
            }
        }
    }
}
