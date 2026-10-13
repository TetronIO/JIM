# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMScopingCriteriaGroup {
    <#
    .SYNOPSIS
        Creates a scoping criteria group on a Synchronisation Rule.

    .DESCRIPTION
        Creates a scoping criteria group on a Synchronisation Rule, at the top level or inside another group. A
        top-level group is an alternative to the rule's other top-level groups: an object is in scope when any one
        of them is met.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER ParentGroupId
        The ID of the group to create this one inside. Without it, the group is created at the top level.

    .PARAMETER Type
        How the group combines what it contains: All is met when every criterion and child group in it is met, Any
        when at least one is.

    .PARAMETER Position
        Where the group sits among the groups beside it, used to order them. It does not change which objects are in
        scope.

    .PARAMETER PassThru
        Returns the group created.

    .OUTPUTS
        With -PassThru, the group created; otherwise nothing.

        JIM.ScopingCriteriaGroup
        SyncRuleId (int): The ID of the Synchronisation Rule the group belongs to, so the group can be piped to the
            other scoping criteria cmdlets.

    .EXAMPLE
        New-JIMScopingCriteriaGroup -SyncRuleId 5 -Type All

        Creates a root-level criteria group with AND logic.

    .EXAMPLE
        New-JIMScopingCriteriaGroup -SyncRuleId 5 -ParentGroupId 10 -Type Any -PassThru

        Creates a child group with OR logic nested under group 10.

    .LINK
        Get-JIMScopingCriteria
        Set-JIMScopingCriteriaGroup
        Remove-JIMScopingCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.ScopingCriteriaGroup')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter()]
        [int]$ParentGroupId,

        [Parameter()]
        [ValidateSet('All', 'Any')]
        [string]$Type = 'All',

        [Parameter()]
        [int]$Position = 0,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{
            type = $Type
            position = $Position
        }

        $endpoint = if ($PSBoundParameters.ContainsKey('ParentGroupId')) {
            "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/$ParentGroupId/child-groups"
        }
        else {
            "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria"
        }

        $target = if ($PSBoundParameters.ContainsKey('ParentGroupId')) {
            "Synchronisation Rule $SyncRuleId (under group $ParentGroupId)"
        }
        else {
            "Synchronisation Rule $SyncRuleId (root level)"
        }

        if ($PSCmdlet.ShouldProcess($target, "Create Scoping Criteria Group ($Type)")) {
            Write-Verbose "Creating scoping criteria group for Synchronisation Rule $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint $endpoint -Method 'POST' -Body $body

                Write-Verbose "Created scoping criteria group ID: $($result.id)"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to create scoping criteria group: $_"
            }
        }
    }
}
