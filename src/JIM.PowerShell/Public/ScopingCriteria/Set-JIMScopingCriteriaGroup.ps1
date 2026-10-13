# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMScopingCriteriaGroup {
    <#
    .SYNOPSIS
        Changes a scoping criteria group.

    .DESCRIPTION
        Changes how a scoping criteria group combines what it contains, or where it sits among the groups beside it.
        Give -Type, -Position or both; what you leave out stays as it is.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule.

    .PARAMETER GroupId
        The ID of the group to change.

    .PARAMETER Type
        How the group combines what it contains: All is met when every criterion and child group in it is met, Any
        when at least one is.

    .PARAMETER Position
        Where the group sits among the groups beside it, used to order them. It does not change which objects are in
        scope.

    .PARAMETER PassThru
        Returns the group as changed.

    .OUTPUTS
        With -PassThru, the group as changed, with what it contains; otherwise nothing.

        JIM.ScopingCriteriaGroup
        SyncRuleId (int): The ID of the Synchronisation Rule the group belongs to, so the group can be piped to the
            other scoping criteria cmdlets.

    .EXAMPLE
        Set-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 10 -Type Any

        Changes the group logic from AND to OR.

    .EXAMPLE
        Set-JIMScopingCriteriaGroup -SyncRuleId 5 -GroupId 10 -Position 1 -PassThru

        Updates the position and returns the updated group.

    .LINK
        Get-JIMScopingCriteria
        New-JIMScopingCriteriaGroup
        Remove-JIMScopingCriteriaGroup
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.ScopingCriteriaGroup')]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$GroupId,

        [Parameter()]
        [ValidateSet('All', 'Any')]
        [string]$Type,

        [Parameter()]
        [int]$Position,

        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{}

        if ($PSBoundParameters.ContainsKey('Type')) {
            $body.type = $Type
        }

        if ($PSBoundParameters.ContainsKey('Position')) {
            $body.position = $Position
        }

        if ($body.Count -eq 0) {
            Write-Warning "No properties specified to update."
            return
        }

        if ($PSCmdlet.ShouldProcess("Scoping Criteria Group $GroupId", "Update")) {
            Write-Verbose "Updating scoping criteria group $GroupId for Synchronisation Rule $SyncRuleId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/$GroupId" -Method 'PUT' -Body $body

                Write-Verbose "Updated scoping criteria group $GroupId"

                if ($PassThru) {
                    $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
                }
            }
            catch {
                Write-Error "Failed to update scoping criteria group: $_"
            }
        }
    }
}
