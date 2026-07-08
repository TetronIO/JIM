# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMSyncRuleMatchingRule {
    <#
    .SYNOPSIS
        Gets Object Matching Rules from a Synchronisation Rule (advanced mode).

    .DESCRIPTION
        Retrieves Object Matching Rules defined on a specific Synchronisation Rule.
        This is used in advanced mode where matching rules are per-Synchronisation Rule
        rather than per-object type.

    .PARAMETER SyncRuleId
        The unique identifier of the Synchronisation Rule.

    .PARAMETER Id
        The unique identifier of a specific Matching Rule to retrieve.

    .OUTPUTS
        PSCustomObject representing Matching Rule(s).

    .EXAMPLE
        Get-JIMSyncRuleMatchingRule -SyncRuleId 5

        Gets all Matching Rules for Synchronisation Rule ID 5.

    .EXAMPLE
        Get-JIMSyncRuleMatchingRule -SyncRuleId 5 -Id 12

        Gets the specific Matching Rule with ID 12 from Synchronisation Rule ID 5.

    .LINK
        New-JIMSyncRuleMatchingRule
        Set-JIMSyncRuleMatchingRule
        Remove-JIMSyncRuleMatchingRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'BySyncRule')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(ParameterSetName = 'ById')]
        [int]$Id
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSBoundParameters.ContainsKey('Id')) {
            Write-Verbose "Getting Matching Rule ID: $Id for Synchronisation Rule ID: $SyncRuleId"
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/matching-rules/$Id"
            $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
        }
        else {
            Write-Verbose "Getting Matching Rules for Synchronisation Rule ID: $SyncRuleId"
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/matching-rules"

            # Output each rule individually for pipeline support
            foreach ($rule in $result) {
                $rule | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
            }
        }
    }
}
