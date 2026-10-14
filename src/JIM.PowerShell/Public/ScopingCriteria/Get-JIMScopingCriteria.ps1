# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMScopingCriteria {
    <#
    .SYNOPSIS
        Gets a Synchronisation Rule's scoping criteria.

    .DESCRIPTION
        Gets the scoping criteria of a Synchronisation Rule: its top-level groups, each with the criteria and child
        groups inside it, or one group by ID. Import and export Synchronisation Rules both have them: an import
        rule's criteria test Connected System attributes, and an export rule's test Metaverse attributes.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER GroupId
        The ID of one group to get, at any depth. Without it, every top-level group comes back.

    .OUTPUTS
        By default, each top-level group, with the criteria and child groups inside it. With -GroupId, that group
        and what it contains.

        JIM.ScopingCriteriaGroup
        SyncRuleId (int): The ID of the Synchronisation Rule the group belongs to, so the group can be piped to the
            other scoping criteria cmdlets.

    .EXAMPLE
        Get-JIMScopingCriteria -SyncRuleId 5

        Returns all scoping criteria groups for Synchronisation Rule ID 5.

    .EXAMPLE
        Get-JIMScopingCriteria -SyncRuleId 5 -GroupId 10

        Returns the specific scoping criteria group with ID 10.

    .LINK
        New-JIMScopingCriteriaGroup
        Remove-JIMScopingCriteriaGroup
        New-JIMScopingCriterion
    #>
    [CmdletBinding()]
    [OutputType('JIM.ScopingCriteriaGroup')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$SyncRuleId,

        [Parameter()]
        [int]$GroupId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        try {
            if ($PSBoundParameters.ContainsKey('GroupId')) {
                Write-Verbose "Getting scoping criteria group $GroupId for Synchronisation Rule $SyncRuleId"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/$GroupId"
                $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
            }
            else {
                Write-Verbose "Getting all scoping criteria groups for Synchronisation Rule $SyncRuleId"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria"
                foreach ($group in $result) {
                    $group | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
                }
            }
        }
        catch {
            Write-Error "Failed to get scoping criteria: $_"
        }
    }
}
