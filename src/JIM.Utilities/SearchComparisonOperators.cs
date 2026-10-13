// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Utilities;

/// <summary>
/// The operator catalogue: the one place a criterion's comparison operator is described (#2053). It answers which
/// operators apply to each attribute <see cref="AttributeDataType"/>, in which <see cref="CriterionContext"/>, what each
/// is called, which negates which, whether it takes a value, and whether it takes a pattern.
///
/// Everything that offers or accepts an operator reads it: the scoping, Predefined Search and Example Data editors, the
/// REST API's validation, the Synchronisation Rule write path, the scoping evaluator and its explanations, and (through
/// a test that reads the cmdlet source) PowerShell's <c>ValidateSet</c>s. Pure and deterministic, with no data access,
/// so every layer can share it.
/// </summary>
public static class SearchComparisonOperators
{
    /// <summary>
    /// The longest pattern a "matches pattern" criterion may hold. A regular expression this long is either generated or
    /// a mistake, and either way is not something to evaluate against every value of every object.
    /// </summary>
    public const int MaximumPatternLength = 1024;

    // Each type's operators in display order: the type's own family first, then the presence operators every type has.
    private static readonly SearchComparisonType[] TextOperators =
    {
        SearchComparisonType.Equals,
        SearchComparisonType.NotEquals,
        SearchComparisonType.StartsWith,
        SearchComparisonType.NotStartsWith,
        SearchComparisonType.EndsWith,
        SearchComparisonType.NotEndsWith,
        SearchComparisonType.Contains,
        SearchComparisonType.NotContains,
        SearchComparisonType.MatchesPattern,
        SearchComparisonType.NotMatchesPattern,
        SearchComparisonType.HasValue,
        SearchComparisonType.HasNoValue
    };

    // DateTime is ordered; display leads with the date-oriented wording (before / on or before / after / ...).
    private static readonly SearchComparisonType[] DateTimeOperators =
    {
        SearchComparisonType.LessThan,
        SearchComparisonType.LessThanOrEquals,
        SearchComparisonType.GreaterThan,
        SearchComparisonType.GreaterThanOrEquals,
        SearchComparisonType.Equals,
        SearchComparisonType.NotEquals,
        SearchComparisonType.HasValue,
        SearchComparisonType.HasNoValue
    };

    // Numbers are ordered; display leads with equality.
    private static readonly SearchComparisonType[] NumberOperators =
    {
        SearchComparisonType.Equals,
        SearchComparisonType.NotEquals,
        SearchComparisonType.LessThan,
        SearchComparisonType.LessThanOrEquals,
        SearchComparisonType.GreaterThan,
        SearchComparisonType.GreaterThanOrEquals,
        SearchComparisonType.HasValue,
        SearchComparisonType.HasNoValue
    };

    // Boolean and Guid support equality and presence only.
    private static readonly SearchComparisonType[] EqualityOperators =
    {
        SearchComparisonType.Equals,
        SearchComparisonType.NotEquals,
        SearchComparisonType.HasValue,
        SearchComparisonType.HasNoValue
    };

    /// <summary>
    /// Every operator, in catalogue order. <see cref="SearchComparisonType.NotSet"/> is not an operator.
    /// </summary>
    public static IReadOnlyList<SearchComparisonType> All { get; } = Enum.GetValues<SearchComparisonType>()
        .Where(op => op != SearchComparisonType.NotSet)
        .ToArray();

    private static readonly Dictionary<(AttributeDataType, CriterionContext), SearchComparisonType[]> OperatorsByTypeAndContext = BuildOperatorsByTypeAndContext();

    private static Dictionary<(AttributeDataType, CriterionContext), SearchComparisonType[]> BuildOperatorsByTypeAndContext()
    {
        var table = new Dictionary<(AttributeDataType, CriterionContext), SearchComparisonType[]>();
        foreach (var type in Enum.GetValues<AttributeDataType>())
        {
            var operators = type switch
            {
                AttributeDataType.Text => TextOperators,
                AttributeDataType.DateTime => DateTimeOperators,
                AttributeDataType.Number or AttributeDataType.LongNumber or AttributeDataType.Decimal => NumberOperators,
                AttributeDataType.Boolean or AttributeDataType.Guid => EqualityOperators,
                _ => Array.Empty<SearchComparisonType>()
            };

            foreach (var context in Enum.GetValues<CriterionContext>())
                table[(type, context)] = operators.Where(op => IsOfferedIn(op, context)).ToArray();
        }

        return table;
    }

    /// <summary>
    /// Whether an operator may be offered in a context at all, whatever the data type. Pattern operators are .NET
    /// regular expressions and are never translated to SQL, so a context that runs as SQL never offers them.
    /// </summary>
    private static bool IsOfferedIn(SearchComparisonType op, CriterionContext context) =>
        !(IsPattern(op) && context == CriterionContext.PredefinedSearch);

    /// <summary>
    /// The operators valid for an attribute data type in a context, in display order. Unsupported types (Binary,
    /// Reference, NotSet) have none.
    /// </summary>
    public static IReadOnlyList<SearchComparisonType> ValidOperatorsFor(AttributeDataType type, CriterionContext context) =>
        OperatorsByTypeAndContext.TryGetValue((type, context), out var operators) ? operators : Array.Empty<SearchComparisonType>();

    /// <summary>
    /// Every operator a context offers for at least one data type, in catalogue order. This is what an interface that
    /// cannot know the attribute's type up front (a PowerShell <c>ValidateSet</c>) accepts.
    /// </summary>
    public static IReadOnlyList<SearchComparisonType> OperatorsFor(CriterionContext context) =>
        All.Where(op => IsOfferedIn(op, context)).ToArray();

    /// <summary>
    /// Whether the operator applies to the data type in the context. <see cref="SearchComparisonType.NotSet"/>, and any
    /// value that is not a defined operator, is never valid.
    /// </summary>
    public static bool IsValid(SearchComparisonType op, AttributeDataType type, CriterionContext context) =>
        ValidOperatorsFor(type, context).Contains(op);

    /// <summary>
    /// The operator a negated operator negates (Equals for NotEquals), or null for an operator that negates nothing.
    /// Over a multi-valued attribute a positive operator is met when any value matches, and a negated operator when the
    /// attribute holds at least one value and no value matches the operator it negates (#1923).
    /// </summary>
    public static SearchComparisonType? Negates(SearchComparisonType op) => op switch
    {
        SearchComparisonType.NotEquals => SearchComparisonType.Equals,
        SearchComparisonType.NotStartsWith => SearchComparisonType.StartsWith,
        SearchComparisonType.NotEndsWith => SearchComparisonType.EndsWith,
        SearchComparisonType.NotContains => SearchComparisonType.Contains,
        SearchComparisonType.NotMatchesPattern => SearchComparisonType.MatchesPattern,
        _ => null
    };

    /// <summary>
    /// Whether the operator negates another (does not equal, does not start with, does not end with, does not contain,
    /// does not match pattern). The presence operators are not negations of a per-value test: "has no value" is about
    /// whether any value is held at all.
    /// </summary>
    public static bool IsNegated(SearchComparisonType op) => Negates(op).HasValue;

    /// <summary>
    /// Whether the operator compares the attribute against a value the criterion holds. "has a value" and "has no
    /// value" take none; a criterion using them carries no value.
    /// </summary>
    public static bool TakesValue(SearchComparisonType op) =>
        op is not (SearchComparisonType.NotSet or SearchComparisonType.HasValue or SearchComparisonType.HasNoValue);

    /// <summary>
    /// Whether the operator's value is a .NET regular expression ("matches pattern", "does not match pattern").
    /// </summary>
    public static bool IsPattern(SearchComparisonType op) =>
        op is SearchComparisonType.MatchesPattern or SearchComparisonType.NotMatchesPattern;

    /// <summary>
    /// Why a pattern cannot be saved, or null when it can. Every surface that saves a pattern criterion calls this, so
    /// a pattern that would fail during synchronisation is refused when it is written instead.
    /// </summary>
    public static string? ValidatePattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return "A pattern is required.";

        if (pattern.Length > MaximumPatternLength)
            return $"The pattern is longer than {MaximumPatternLength} characters.";

        try
        {
            CriterionPatterns.Get(pattern, caseSensitive: true);
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"The pattern is not a valid regular expression: {ex.Message}";
        }
    }

    /// <summary>
    /// The friendly label for an operator in the context of an attribute type. DateTime uses date-oriented wording;
    /// numeric types spell out the magnitude comparison; everything else uses plain wording. Shared by the criteria
    /// editors and by scoping explanations (#348), so the two never word a comparison differently.
    /// </summary>
    public static string LabelFor(SearchComparisonType op, AttributeDataType type)
    {
        if (type == AttributeDataType.DateTime)
        {
            switch (op)
            {
                case SearchComparisonType.LessThan: return "before";
                case SearchComparisonType.LessThanOrEquals: return "on or before";
                case SearchComparisonType.GreaterThan: return "after";
                case SearchComparisonType.GreaterThanOrEquals: return "on or after";
            }
        }

        return op switch
        {
            SearchComparisonType.Equals => "equals",
            SearchComparisonType.NotEquals => "does not equal",
            SearchComparisonType.StartsWith => "starts with",
            SearchComparisonType.NotStartsWith => "does not start with",
            SearchComparisonType.EndsWith => "ends with",
            SearchComparisonType.NotEndsWith => "does not end with",
            SearchComparisonType.Contains => "contains",
            SearchComparisonType.NotContains => "does not contain",
            SearchComparisonType.LessThan => "less than",
            SearchComparisonType.LessThanOrEquals => "less than or equal to",
            SearchComparisonType.GreaterThan => "greater than",
            SearchComparisonType.GreaterThanOrEquals => "greater than or equal to",
            SearchComparisonType.HasValue => "has a value",
            SearchComparisonType.HasNoValue => "has no value",
            SearchComparisonType.MatchesPattern => "matches pattern",
            SearchComparisonType.NotMatchesPattern => "does not match pattern",
            _ => op.ToString().SplitOnCapitalLetters()
        };
    }
}
