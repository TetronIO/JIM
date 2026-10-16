// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Utilities;

namespace JIM.Application.Servers.ConfigurationTests;

/// <summary>
/// The one text form a configuration test case compares values in (#2055 Phase 4): the same value always gives the
/// same text, whatever the server's culture or time zone, so a case passes or fails the same everywhere. A date is
/// UTC ISO 8601 to the microsecond (<see cref="DateTimeAttributeValue.ToRoundTripString"/>), a decimal its canonical
/// digits, a number and a Boolean their invariant text, a Guid its hyphenated lower-case form, Binary base64.
/// </summary>
public static class ConfigurationTestValues
{
    /// <summary>
    /// <paramref name="value"/>, a value the configuration gave, in the canonical text form.
    /// </summary>
    public static string Canonical(object value) => value switch
    {
        null => string.Empty,
        string text => text,
        DateTime date => DateTimeAttributeValue.ToRoundTripString(date),
        DateTimeOffset dateWithOffset => DateTimeAttributeValue.ToRoundTripString(dateWithOffset.UtcDateTime),
        decimal number => DecimalAttributeValue.ToCanonicalString(number),
        bool flag => flag ? "true" : "false",
        Guid guid => guid.ToString("D"),
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>
    /// <paramref name="expected"/>, a value as a test case states it, read as <paramref name="type"/> and given in the
    /// canonical text form, so <c>2026-03-02</c> expected of a date matches the date 2 March 2026 at midnight UTC.
    /// Null when the text is not a value of that type. With no type (a Scope case), the text as it is.
    /// </summary>
    public static string? CanonicalExpected(string expected, AttributeDataType? type)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (type is null or AttributeDataType.Text)
            return expected;

        return TestSampleValues.TryConvertSample(expected, type.Value, out var typed) ? Canonical(typed) : null;
    }
}
