function Get-JIMScopingCriteria {
    <#
    .SYNOPSIS
        Gets scoping criteria groups for a sync rule.

    .DESCRIPTION
        Retrieves the scoping criteria groups configured on an export sync rule.
        Scoping criteria define which Metaverse objects are included in the export.
        Only export sync rules support scoping criteria.

    .PARAMETER SyncRuleId
        The unique identifier of the sync rule.

    .PARAMETER GroupId
        Optional. The unique identifier of a specific criteria group to retrieve.

    .OUTPUTS
        One or more scoping criteria group objects with their nested criteria.

    .EXAMPLE
        Get-JIMScopingCriteria -SyncRuleId 5

        Returns all scoping criteria groups for sync rule ID 5.

    .EXAMPLE
        Get-JIMScopingCriteria -SyncRuleId 5 -GroupId 10

        Returns the specific scoping criteria group with ID 10.

    .LINK
        New-JIMScopingCriteriaGroup
        Remove-JIMScopingCriteriaGroup
        New-JIMScopingCriterion
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject[]])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$SyncRuleId,

        [Parameter()]
        [int]$GroupId
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        try {
            if ($PSBoundParameters.ContainsKey('GroupId')) {
                Write-Verbose "Getting scoping criteria group $GroupId for sync rule $SyncRuleId"
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/$GroupId"
                $result | Add-Member -NotePropertyName 'SyncRuleId' -NotePropertyValue $SyncRuleId -PassThru -Force
            }
            else {
                Write-Verbose "Getting all scoping criteria groups for sync rule $SyncRuleId"
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
