# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function ConvertTo-JIMCausalConsequenceRows {
    <#
    .SYNOPSIS
        Flattens an execution item's consequences into one row per cohort, by level.

    .DESCRIPTION
        The consequences endpoint returns cohorts that each carry what their members went on to cause, a recursive
        shape that is awkward to read or filter from the console. This walks it depth-first and emits one row per
        cohort, with Level 1 for what the item directly caused and one more per step on, and a Level 0 row for each
        reason the item itself led nowhere.

        Ending says where a cohort's branch stopped: the cohort's own ending (for example WaitingForExport), else the
        counts of members that led nowhere further (for example "NoFurtherConsequences (2)"), else FurtherThanShown
        where the walk's read bound was reached. Empty where every member carries on.

    .PARAMETER Consequences
        The consequences response.

    .OUTPUTS
        PSCustomObject with ExecutionItemId, Level, HopKind, EdgeType, ReasonCode, Count, ObjectType, Attribute,
        ConnectedSystem, SyncRule, OutcomeSummary, PendingExportStatus, Ending, FirstOccurred, LastOccurred, DueAt,
        Members and CohortKey.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [object]$Consequences
    )

    $executionItemId = [guid]$Consequences.RunProfileExecutionItemId
    foreach ($ending in @($Consequences.Endings | Where-Object { $null -ne $_ })) {
        New-JIMCausalConsequenceRow -ExecutionItemId $executionItemId -Level 0 -Ending $ending.Ending -ConnectedSystem $ending.ConnectedSystemName
    }

    ConvertTo-JIMCausalConsequenceCohortRows -Cohorts $Consequences.Cohorts -ExecutionItemId $executionItemId -Level 1
}

function ConvertTo-JIMCausalConsequenceCohortRows {
    # Internal to ConvertTo-JIMCausalConsequenceRows: emits one row per cohort, then its consequences one level on.
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [AllowEmptyCollection()]
        [AllowNull()]
        [object[]]$Cohorts,

        [guid]$ExecutionItemId,

        [int]$Level
    )

    foreach ($cohort in $Cohorts) {
        if ($null -eq $cohort) {
            continue
        }

        $ending = if ($cohort.Ending) {
            $cohort.Ending
        }
        elseif ($cohort.Endings -and @($cohort.Endings).Count -gt 0) {
            (@($cohort.Endings | ForEach-Object { "$($_.Ending) ($($_.MemberCount))" }) -join ', ')
        }
        elseif ($cohort.IsFurtherThanShown) {
            'FurtherThanShown'
        }
        else {
            ''
        }

        [PSCustomObject]@{
            ExecutionItemId     = $ExecutionItemId
            Level               = $Level
            HopKind             = $cohort.HopKind
            EdgeType            = $cohort.EdgeType
            ReasonCode          = $cohort.ReasonCode
            Count               = $cohort.MemberCount
            ObjectType          = if ($cohort.MemberCount -eq 1) { $cohort.ObjectTypeName } else { $cohort.ObjectTypePluralName ?? $cohort.ObjectTypeName }
            Attribute           = $cohort.AttributeName
            ConnectedSystem     = $cohort.ConnectedSystemName
            SyncRule            = $cohort.SyncRuleName
            OutcomeSummary      = $cohort.OutcomeSummary
            PendingExportStatus = $cohort.PendingExportStatus
            Ending              = $ending
            FirstOccurred       = $cohort.FirstOccurred
            LastOccurred        = $cohort.LastOccurred
            DueAt               = $cohort.DueAt
            Members             = @($cohort.Members | Where-Object { $null -ne $_ } | ForEach-Object { $_.DisplayName })
            CohortKey           = $cohort.CohortKey
        }

        if ($cohort.Consequences -and @($cohort.Consequences).Count -gt 0) {
            ConvertTo-JIMCausalConsequenceCohortRows -Cohorts $cohort.Consequences -ExecutionItemId $ExecutionItemId -Level ($Level + 1)
        }
    }
}

function New-JIMCausalConsequenceRow {
    # Internal to ConvertTo-JIMCausalConsequenceRows: the row for the item's own ending, in the same shape as a cohort.
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [guid]$ExecutionItemId,

        [int]$Level,

        [string]$Ending,

        [string]$ConnectedSystem
    )

    [PSCustomObject]@{
        ExecutionItemId     = $ExecutionItemId
        Level               = $Level
        HopKind             = $null
        EdgeType            = $null
        ReasonCode          = $null
        Count               = 1
        ObjectType          = $null
        Attribute           = $null
        ConnectedSystem     = $ConnectedSystem
        SyncRule            = $null
        OutcomeSummary      = $null
        PendingExportStatus = $null
        Ending              = $Ending
        FirstOccurred       = $null
        LastOccurred        = $null
        DueAt               = $null
        Members             = @()
        CohortKey           = $null
    }
}
