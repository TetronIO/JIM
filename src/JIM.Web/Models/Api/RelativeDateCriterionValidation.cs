// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Web.Models.Api;

/// <summary>
/// Shared validation for the relative-date fields of a criterion request (used by both the scoping-criteria
/// and predefined-search-criteria endpoints). Parses and validates the value mode and relative offset,
/// enforcing that relative criteria are DateTime-only, fully specified, non-negative, and not mixed with an
/// absolute date value.
/// </summary>
public static class RelativeDateCriterionValidation
{
    /// <summary>
    /// Validates and parses the relative-date fields of a request.
    /// </summary>
    /// <param name="valueMode">The requested value mode ("Absolute" or "Relative"); null/empty means Absolute.</param>
    /// <param name="relativeCount">The relative offset count, when relative.</param>
    /// <param name="relativeUnit">The relative unit name (Hours, Days, Weeks, Months, Years), when relative.</param>
    /// <param name="relativeDirection">The relative direction name (Ago, FromNow), when relative.</param>
    /// <param name="attributeType">The data type of the attribute the criterion targets.</param>
    /// <param name="hasAbsoluteDate">Whether the request also supplied an absolute DateTime value.</param>
    /// <param name="mode">The parsed value mode.</param>
    /// <param name="count">The parsed relative count (null when Absolute).</param>
    /// <param name="unit">The parsed relative unit (null when Absolute).</param>
    /// <param name="direction">The parsed relative direction (null when Absolute).</param>
    /// <returns>Null when valid; otherwise a human-readable error message for a 400 response.</returns>
    public static string? Validate(
        string? valueMode,
        int? relativeCount,
        string? relativeUnit,
        string? relativeDirection,
        AttributeDataType attributeType,
        bool hasAbsoluteDate,
        out DateCriteriaValueMode mode,
        out int? count,
        out RelativeDateUnit? unit,
        out RelativeDateDirection? direction)
    {
        mode = DateCriteriaValueMode.Absolute;
        count = null;
        unit = null;
        direction = null;

        if (!string.IsNullOrEmpty(valueMode) && !Enum.TryParse(valueMode, true, out mode))
            return $"Invalid value mode '{valueMode}'. Use 'Absolute' or 'Relative'.";

        if (mode == DateCriteriaValueMode.Absolute)
        {
            if (relativeCount.HasValue || !string.IsNullOrEmpty(relativeUnit) || !string.IsNullOrEmpty(relativeDirection))
                return "Relative fields (relativeCount, relativeUnit, relativeDirection) must not be set for an absolute criterion.";
            return null;
        }

        // Relative.
        if (attributeType != AttributeDataType.DateTime)
            return "Relative value mode is only valid for DateTime attributes.";

        if (hasAbsoluteDate)
            return "Provide either an absolute date value or relative fields, not both.";

        if (!relativeCount.HasValue)
            return "relativeCount is required for a relative criterion.";

        if (relativeCount.Value < 0)
            return "relativeCount must be zero or positive; use the direction to offset into the past or future.";

        if (string.IsNullOrEmpty(relativeUnit) || !Enum.TryParse<RelativeDateUnit>(relativeUnit, true, out var parsedUnit))
            return "A valid relativeUnit is required (Hours, Days, Weeks, Months, Years).";

        if (string.IsNullOrEmpty(relativeDirection) || !Enum.TryParse<RelativeDateDirection>(relativeDirection, true, out var parsedDirection))
            return "A valid relativeDirection is required (Ago, FromNow).";

        count = relativeCount;
        unit = parsedUnit;
        direction = parsedDirection;
        return null;
    }
}
