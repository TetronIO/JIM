// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Search;

/// <summary>
/// The value-carrying surface shared by scoping and predefined-search criteria. Lets a single UI value
/// editor (and any other shared logic) read and write a criterion's comparison operator, typed value
/// carriers and relative-date fields without knowing which concrete criterion type it is editing.
/// </summary>
public interface ICriterionValues
{
    SearchComparisonType ComparisonType { get; set; }
    string? StringValue { get; set; }
    int? IntValue { get; set; }
    long? LongValue { get; set; }
    decimal? DecimalValue { get; set; }
    DateTime? DateTimeValue { get; set; }
    bool? BoolValue { get; set; }
    Guid? GuidValue { get; set; }
    bool CaseSensitive { get; set; }
    DateCriteriaValueMode ValueMode { get; set; }
    int? RelativeCount { get; set; }
    RelativeDateUnit? RelativeUnit { get; set; }
    RelativeDateDirection? RelativeDirection { get; set; }
}
