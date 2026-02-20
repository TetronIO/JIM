function New-JIMScopingCriteriaGroup {
    <#
    .SYNOPSIS
        Creates a new scoping criteria group for a sync rule.

    .DESCRIPTION
        Creates a new scoping criteria group on an export sync rule.
        Groups can be created at the root level or nested within an existing group.
        The group type determines how criteria within it are evaluated:
        - All: All criteria must match (AND logic)
        - Any: At least one criterion must match (OR logic)

    .PARAMETER SyncRuleId
        The unique identifier of the sync rule.

    .PARAMETER ParentGroupId
        Optional. The ID of an existing group to nest this group within.
        If not specified, creates a root-level group.

    .PARAMETER Type
        The logical operator for this group: 'All' (AND) or 'Any' (OR).
        Defaults to 'All'.

    .PARAMETER Position
        Optional position/order for this group. Defaults to 0.

    .PARAMETER PassThru
        If specified, returns the created group object.

    .OUTPUTS
        If -PassThru is specified, returns the created scoping criteria group.

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
    [OutputType([PSCustomObject])]
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
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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
            "Sync Rule $SyncRuleId (under group $ParentGroupId)"
        }
        else {
            "Sync Rule $SyncRuleId (root level)"
        }

        if ($PSCmdlet.ShouldProcess($target, "Create Scoping Criteria Group ($Type)")) {
            Write-Verbose "Creating scoping criteria group for sync rule $SyncRuleId"

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
