// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Search;

/// <summary>
/// Determines what type of logic a CriteriaGroup should apply to it's members. 
/// </summary>
public enum SearchGroupType
{
    /// <summary>
    /// All criteria in a group must be true.
    /// </summary>
    All = 0,
    /// <summary>
    /// Any criteria in the group can be true, i.e. at least one must be true.
    /// </summary>
    Any = 1
}

/// <summary>
/// A criterion's comparison operator: the one vocabulary shared by Synchronisation Rule scoping, Predefined Searches
/// and Example Data dependencies.
/// </summary>
/// <remarks>
/// Which operators apply to which data type, and where each may be offered, is decided by
/// <c>SearchComparisonOperators</c> in JIM.Utilities, never here. The ordinals are stored, so members are only ever
/// appended. The names are the REST API's and PowerShell's.
/// </remarks>
public enum SearchComparisonType
{
    NotSet = 0,
    Equals = 1,
    NotEquals = 2,
    StartsWith = 3,
    NotStartsWith = 4,
    EndsWith = 5,
    NotEndsWith = 6,
    Contains = 7,
    NotContains = 8,
    LessThan = 9,
    LessThanOrEquals = 10,
    GreaterThan = 11,
    GreaterThanOrEquals = 12,
    /// <summary>Met when the attribute holds at least one value. Takes no value.</summary>
    HasValue = 13,
    /// <summary>Met when the attribute holds no value. Takes no value.</summary>
    HasNoValue = 14,
    /// <summary>Met when any value matches a .NET regular expression. Text only, and never offered where criteria run as SQL.</summary>
    MatchesPattern = 15,
    /// <summary>Met when the attribute holds at least one value and none matches a .NET regular expression.</summary>
    NotMatchesPattern = 16
}

/// <summary>
/// Where a criterion is evaluated, which decides the operators it may be offered. Pattern operators run only in .NET,
/// so a context that runs as SQL never offers them.
/// </summary>
public enum CriterionContext
{
    /// <summary>A Synchronisation Rule's scoping criteria, evaluated in .NET during synchronisation.</summary>
    Scoping = 0,
    /// <summary>A Predefined Search's criteria, translated to SQL.</summary>
    PredefinedSearch = 1,
    /// <summary>An Example Data template attribute's dependency, evaluated in .NET while generating.</summary>
    ExampleData = 2
}

/// <summary>
/// Determines whether a date/time criterion compares against a fixed (absolute) date or a date
/// resolved relative to "now" each time the criterion is evaluated.
/// </summary>
public enum DateCriteriaValueMode
{
    /// <summary>
    /// The criterion compares against a fixed DateTime value (the default).
    /// </summary>
    Absolute = 0,
    /// <summary>
    /// The criterion compares against a date resolved relative to the current UTC time at evaluation,
    /// for example "30 days ago" or "7 days from now".
    /// </summary>
    Relative = 1
}

/// <summary>
/// The unit of a relative date/time offset. All units except <see cref="Hours"/> resolve to a whole day
/// (midnight UTC); Hours resolves to an exact instant.
/// </summary>
public enum RelativeDateUnit
{
    Hours = 0,
    Days = 1,
    Weeks = 2,
    Months = 3,
    Years = 4
}

/// <summary>
/// The direction of a relative date/time offset from "now".
/// </summary>
public enum RelativeDateDirection
{
    /// <summary>
    /// The resolved date is in the past (now minus the offset).
    /// </summary>
    Ago = 0,
    /// <summary>
    /// The resolved date is in the future (now plus the offset).
    /// </summary>
    FromNow = 1
}