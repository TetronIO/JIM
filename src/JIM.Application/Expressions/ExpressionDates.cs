// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using JIM.Utilities;

namespace JIM.Application.Expressions;

/// <summary>
/// The one rule for reading a value as a date (#2153, N2), used by <c>ToFileTime</c>, <c>FormatDate</c>, <c>Eq</c>
/// and <see cref="ExpressionResultConverter"/>, so every server reads the same text as the same instant whatever
/// its culture.
/// </summary>
/// <remarks>
/// Text is read only in ISO 8601: <c>yyyy-MM-dd</c>, optionally followed by <c>T</c> or a space and
/// <c>HH:mm[:ss[.fraction]]</c>, then <c>Z</c>, an offset (<c>+01:00</c>), or nothing, which means UTC. Every other
/// form is unreadable, including <c>03/15/2026</c> and <c>15/03/2026</c>: a numeric date means one day under one
/// culture and another under the next. JIM's own text form (<see cref="ExpressionText.DateFormat"/>) reads back as
/// the same instant. Text in any other form is read with <c>ParseDate</c>, which takes an explicit format.
/// </remarks>
public static class ExpressionDates
{
    /// <summary>
    /// Longer than any ISO 8601 date JIM reads (<c>2026-03-15T10:30:00.1234567+01:00</c> is 33 characters), so the
    /// pattern is never run over arbitrarily long text.
    /// </summary>
    private const int MaxIsoTextLength = 40;

    // Anchored with \z rather than $, which also matches before a trailing newline: the text is the date and nothing
    // else, so "2026-03-15\n" is refused exactly as "2026-03-15 " is.
    private static readonly Regex IsoDateText = new(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[T ][0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:\\.[0-9]{1,7})?)?(?:Z|[+-][0-9]{2}:[0-9]{2})?)?\\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Reads <paramref name="text"/> as a date under the ISO 8601 rule, as its canonical UTC instant.
    /// </summary>
    /// <returns>False for null, empty or unreadable text.</returns>
    public static bool TryReadText(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaxIsoTextLength || !IsoDateText.IsMatch(text))
            return false;

        // The pattern has already settled the shape, so the invariant parse only checks the ranges (no 13th month)
        // and applies the offset. AssumeUniversal makes text with no offset UTC rather than the host's zone.
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;

        value = DateTimeAttributeValue.ToCanonical(parsed.UtcDateTime);
        return true;
    }

    /// <summary>
    /// Reads <paramref name="value"/> as a date: a date as its canonical UTC instant (a Local date converted, an
    /// Unspecified one taken as UTC, floored to the microsecond), and text under the ISO 8601 rule.
    /// </summary>
    /// <returns>False for null, unreadable text, or a value of any other type.</returns>
    public static bool TryRead(object? value, out DateTime date)
    {
        switch (value)
        {
            case DateTime dateTime:
                date = DateTimeAttributeValue.ToCanonical(dateTime);
                return true;
            case DateTimeOffset dateWithOffset:
                date = DateTimeAttributeValue.ToCanonical(dateWithOffset.UtcDateTime);
                return true;
            case string text:
                return TryReadText(text, out date);
            default:
                date = default;
                return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a date (rather than text that might be read as one).
    /// </summary>
    public static bool IsDate(object? value) => value is DateTime or DateTimeOffset;
}
