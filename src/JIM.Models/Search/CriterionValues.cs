// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Search;

/// <summary>
/// A criterion's operator and value, carried on their own: for a criterion stored in another shape (an Example Data
/// dependency holds its value as text) that has to be validated and evaluated exactly as scoping and Predefined Search
/// criteria are. Never persisted.
/// </summary>
public class CriterionValues : ICriterionValues
{
    public SearchComparisonType ComparisonType { get; set; }
    public string? StringValue { get; set; }
    public int? IntValue { get; set; }
    public long? LongValue { get; set; }
    public decimal? DecimalValue { get; set; }
    public DateTime? DateTimeValue { get; set; }
    public bool? BoolValue { get; set; }
    public Guid? GuidValue { get; set; }
    public bool CaseSensitive { get; set; }
    public DateCriteriaValueMode ValueMode { get; set; }
    public int? RelativeCount { get; set; }
    public RelativeDateUnit? RelativeUnit { get; set; }
    public RelativeDateDirection? RelativeDirection { get; set; }
}
