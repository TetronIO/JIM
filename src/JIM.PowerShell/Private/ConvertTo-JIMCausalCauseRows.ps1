# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function ConvertTo-JIMCausalCauseRows {
    <#
    .SYNOPSIS
        Flattens an execution item's causes into one row per cohort, by level.

    .DESCRIPTION
        The causes endpoint returns cohorts whose members each carry their own causes, a recursive shape that is
        awkward to read or filter from the console. This walks it depth-first and emits one row per cohort, with
        Level 1 for the item's direct causes and one more per step back.

    .PARAMETER Cohorts
        The cohorts to flatten (the response's Cohorts, or a member's Causes).

    .PARAMETER ExecutionItemId
        The execution item the walk started from, carried on every row.

    .PARAMETER Level
        Internal. The level of the cohorts passed in; callers should omit this.

    .OUTPUTS
        PSCustomObject with ExecutionItemId, Level, EdgeType, SourceImportChangeType, MetaverseChangeType,
        ReasonCode, Count, ObjectType, Attribute, ConnectedSystem, SyncRule, Members and Resolution.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowNull()]
        [object[]]$Cohorts,

        [Parameter(Mandatory)]
        [guid]$ExecutionItemId,

        [int]$Level = 1
    )

    foreach ($cohort in $Cohorts) {
        if ($null -eq $cohort) {
            continue
        }

        $members = @($cohort.Members | Where-Object { $null -ne $_ })
        [PSCustomObject]@{
            ExecutionItemId        = $ExecutionItemId
            Level                  = $Level
            EdgeType               = $cohort.EdgeType
            SourceImportChangeType = $cohort.SourceImportChangeType
            MetaverseChangeType    = $cohort.MetaverseChangeType
            ReasonCode             = $cohort.ReasonCode
            Count                  = $cohort.MemberCount
            ObjectType             = if ($cohort.MemberCount -eq 1) { $cohort.ObjectTypeName } else { $cohort.ObjectTypePluralName ?? $cohort.ObjectTypeName }
            Attribute              = $cohort.AttributeName
            ConnectedSystem        = $cohort.ConnectedSystemName
            SyncRule               = $cohort.SyncRuleName
            Members                = @($members | ForEach-Object { $_.DisplayName })
            # Why the walk stopped at these members, where it did; Resolved members carry on to the next level.
            Resolution             = (@($members | ForEach-Object { $_.Resolution } | Where-Object { $_ -and $_ -ne 'Resolved' } |
                Select-Object -Unique) -join ', ')
        }

        foreach ($member in $members) {
            if ($member.Causes -and @($member.Causes).Count -gt 0) {
                ConvertTo-JIMCausalCauseRows -Cohorts $member.Causes -ExecutionItemId $ExecutionItemId -Level ($Level + 1)
            }
        }
    }
}
