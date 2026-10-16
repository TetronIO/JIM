// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;
using System.Globalization;
using JIM.Utilities;

namespace JIM.Application.Expressions;

/// <summary>
/// The one text form of an expression value (#2153, N1). Every non-text value an Expression produces or reads
/// becomes text here, the same way on every server whatever its culture: a date as <c>2026-03-15 10:30:00Z</c>
/// (UTC, with a fraction only when it is non-zero, to the microsecond), a number with <c>.</c> as the decimal point.
/// A Binary value has no text form, and nor do several values, so neither is ever written as its type name.
/// </summary>
public static class ExpressionText
{
    /// <summary>
    /// How a date is written as text. It reads back as the same instant under the date-reading rule
    /// (<see cref="ExpressionDates"/>), and ParseDate reads it with this pattern too.
    /// </summary>
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFK";

    /// <summary>
    /// The culture every evaluation runs under: invariant, with dates in <see cref="DateFormat"/>. It governs what
    /// the language itself writes as text, such as <c>"Changed " + mv["Last Changed"]</c>, so the result does not
    /// depend on the host's <c>LANG</c>.
    /// </summary>
    public static CultureInfo Culture { get; } = CreateCulture();

    private static CultureInfo CreateCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.ShortDatePattern = "yyyy-MM-dd";
        culture.DateTimeFormat.LongTimePattern = "HH:mm:ss.FFFFFFK";
        return CultureInfo.ReadOnly(culture);
    }

    /// <summary>
    /// Writes <paramref name="value"/> as text. Null is written as null. Returns false for a value with no text
    /// form: Binary, or a collection of several values.
    /// </summary>
    public static bool TryFormat(object? value, out string? text)
    {
        switch (value)
        {
            case null:
                text = null;
                return true;
            case string s:
                text = s;
                return true;
            case DateTime date:
                text = FormatDate(date);
                return true;
            case DateTimeOffset dateWithOffset:
                text = FormatDate(dateWithOffset.UtcDateTime);
                return true;
            case byte[]:
            case IEnumerable:
                text = null;
                return false;
            case IFormattable formattable:
                text = formattable.ToString(null, Culture);
                return true;
            default:
                text = value.ToString();
                return true;
        }
    }

    /// <summary>
    /// A date in JIM's text form: its canonical UTC instant, to the microsecond, with <c>Z</c>.
    /// </summary>
    public static string FormatDate(DateTime value) =>
        DateTimeAttributeValue.ToCanonical(value).ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// A value as an administrator should see it in a message or a preview: its text form where it has one, and a
    /// description of it where it has none.
    /// </summary>
    public static string Describe(object value) =>
        TryFormat(value, out var text) ? text ?? string.Empty
        : value is byte[] bytes ? $"(Binary value, {bytes.Length} bytes)"
        : $"({value.GetType().Name})";

    /// <summary>
    /// An Expression's result as the lines a tester shows: one line per value, each in its text form, and none for
    /// no value (null, or an empty collection).
    /// </summary>
    public static IReadOnlyList<string> ToDisplayLines(object? result) =>
        ExpressionResultValues.Elements(result)
            .Where(element => element != null)
            .Select(element => Describe(element!))
            .ToList();
}
