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
/// The comparison to be made on the search term in the search query.
/// The values need to map to operations that the database can natively perform. This is why Functions/Expressions are not used.
/// </summary>
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
    GreaterThanOrEquals = 12
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