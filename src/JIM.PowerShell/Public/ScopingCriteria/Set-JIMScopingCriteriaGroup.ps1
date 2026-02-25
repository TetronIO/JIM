function Set-JIMScopingCriteriaGroup {
    <#
    .SYNOPSIS
        Updates a scoping criteria group.

    .DESCRIPTION
        Updates the type (All/Any) or position of an existing scoping criteria group.

    .PARAMETER SyncRuleId
        The unique identifier of the sync rule.

    .PARAMETER GroupId
        The unique identifier of the criteria group to update.

    .PARAMETER Type
        Optional. The new logical operator for this group: 'All' (AND) or 'Any' (OR).

    .PARAMETER Position
        Optional. The new position/order for this group.

    .PARAMETER PassThru
        If specified, returns the updated group object.

    .OUTPUTS
        If -PassThru is specified, returns the updated scoping criteria group.

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
    [OutputType([PSCustomObject])]
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
            Write-Error "Not connected to JIM. Use Connect-JIM first."
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
            Write-Verbose "Updating scoping criteria group $GroupId for sync rule $SyncRuleId"

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
