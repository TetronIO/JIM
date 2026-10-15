// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Models.Logic;

/// <summary>
/// What a stored Scoping Criterion compares, and against what (#2054): read untracked on the save path, before anything
/// flushes, to tell a criterion the save changed from one it leaves as it was. As for Attribute Flow expressions
/// (#2055 Phase 3), saving refuses any problem with a new or changed criterion and only warns about one it leaves as it
/// was, so a criterion that stopped being valid after it was saved (an attribute's type changed, or an attribute an
/// expression reads was renamed) never stops the rule being saved, disabled or tidied.
/// </summary>
public sealed record SyncRuleScopingCriterionState(
    int? MetaverseAttributeId,
    int? ConnectedSystemAttributeId,
    string? Expression,
    AttributeDataType? CompareAs,
    SearchComparisonType ComparisonType,
    bool CaseSensitive,
    DateCriteriaValueMode ValueMode,
    int? RelativeCount,
    RelativeDateUnit? RelativeUnit,
    RelativeDateDirection? RelativeDirection,
    string? StringValue,
    int? IntValue,
    long? LongValue,
    decimal? DecimalValue,
    DateTime? DateTimeValue,
    bool? BoolValue,
    Guid? GuidValue)
{
    /// <summary>The state <paramref name="criterion"/> holds now.</summary>
    public static SyncRuleScopingCriterionState Of(SyncRuleScopingCriteria criterion)
    {
        ArgumentNullException.ThrowIfNull(criterion);

        return new SyncRuleScopingCriterionState(
            criterion.MetaverseAttribute?.Id ?? criterion.MetaverseAttributeId,
            criterion.ConnectedSystemAttribute?.Id ?? criterion.ConnectedSystemAttributeId,
            criterion.Expression, criterion.CompareAs, criterion.ComparisonType, criterion.CaseSensitive, criterion.ValueMode,
            criterion.RelativeCount, criterion.RelativeUnit, criterion.RelativeDirection, criterion.StringValue, criterion.IntValue,
            criterion.LongValue, criterion.DecimalValue, criterion.DateTimeValue, criterion.BoolValue, criterion.GuidValue);
    }

    /// <summary>True when <paramref name="criterion"/> compares exactly what is stored, against exactly the same value.</summary>
    public bool IsUnchangedIn(SyncRuleScopingCriteria criterion) => Equals(Of(criterion));
}
