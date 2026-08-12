// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// Which <see cref="ActivityStatDimension"/>s a Run Profile execution's stat counters can be recomputed from the
/// persisted Run Profile Execution Items, and are therefore finalisation's to replace.
/// </summary>
/// <remarks>
/// Finalisation replaces an Activity's counters with an exact aggregation over the item tables, which means
/// deleting what is there first. That was a blanket delete while every dimension came from those tables. It cannot
/// stay one: <see cref="ActivityStatDimension.ExcludedContainer"/> counts entries an import read and threw away,
/// which by definition produced no item to aggregate, so a blanket delete would silently drop the count at the
/// moment the Activity completed. Naming the recomputable dimensions here rather than excluding the exception at
/// the delete keeps the rule stated positively: finalisation owns what it can derive, and nothing else.
/// </remarks>
public static class RunProfileExecutionStatsDimensions
{
    /// <summary>
    /// The dimensions aggregated from the Run Profile Execution Item and sync outcome tables.
    /// </summary>
    public static readonly IReadOnlyList<ActivityStatDimension> RecomputedFromExecutionItems =
    [
        ActivityStatDimension.ObjectChangeType,
        ActivityStatDimension.ObjectTypeName,
        ActivityStatDimension.ErrorType,
        ActivityStatDimension.NoChangeReason,
        ActivityStatDimension.OutcomeType
    ];
}
