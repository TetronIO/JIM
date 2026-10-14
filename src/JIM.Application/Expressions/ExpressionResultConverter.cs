// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Utilities;

namespace JIM.Application.Expressions;

/// <summary>
/// Converts one value of an expression's result to the value an attribute of a given type holds. Attribute Flow (in
/// both directions) and Object Matching all convert through here, so one expression produces the same typed value
/// whichever of them evaluates it. A result that cannot be represented exactly is refused rather than narrowed,
/// rounded or guessed at (the #871 lossy-cast class): the caller decides what a refusal means.
/// </summary>
/// <remarks>
/// Text is written in JIM's one text form (<see cref="ExpressionText"/>) and dates are read by JIM's one date rule
/// (<see cref="ExpressionDates"/>), so neither depends on the host's culture (#2153, N1 and N2). Several values are
/// not one value: <see cref="ExpressionResultValues"/> splits a collection before any element reaches here.
/// </remarks>
public static class ExpressionResultConverter
{
    /// <summary>
    /// Converts <paramref name="result"/> to the CLR value an attribute of <paramref name="targetType"/> stores:
    /// <see cref="string"/>, <see cref="int"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="DateTime"/>
    /// (canonical: UTC, to the microsecond), <see cref="bool"/>, <see cref="Guid"/> or a <see cref="byte"/> array.
    /// </summary>
    /// <returns>False when the result cannot be represented exactly, has no text form for a Text attribute (Binary,
    /// or several values), or the type is one no expression produces (Reference).</returns>
    public static bool TryConvert(object? result, AttributeDataType targetType, out object? value)
    {
        value = null;

        switch (targetType)
        {
            case AttributeDataType.Text:
                if (!ExpressionText.TryFormat(result, out var text))
                    return false;
                value = text;
                return true;

            case AttributeDataType.Number:
                // A long result only converts when it fits in an int; silently narrowing would corrupt the value.
                if (result is int intValue)
                    value = intValue;
                else if (result is long longValue && longValue is >= int.MinValue and <= int.MaxValue)
                    value = (int)longValue;
                else if (result is not long && int.TryParse(AsText(result), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                    value = parsedInt;
                else
                    return false;
                return true;

            case AttributeDataType.LongNumber:
                // int widens to long exactly. String results parse with invariant culture.
                if (result is long longNumber)
                    value = longNumber;
                else if (result is int intNumber)
                    value = (long)intNumber;
                else if (long.TryParse(AsText(result), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
                    value = parsedLong;
                else
                    return false;
                return true;

            case AttributeDataType.Decimal:
                // int and long widen to decimal exactly. double and float are refused: binary floating point cannot
                // represent most decimal fractions exactly. String results parse with invariant culture and accept
                // exponent notation ("1.5E3" becomes 1500).
                if (result is decimal decimalValue)
                    value = decimalValue;
                else if (result is int decimalInt)
                    value = (decimal)decimalInt;
                else if (result is long decimalLong)
                    value = (decimal)decimalLong;
                else if (result is not double && result is not float && DecimalAttributeValue.TryParse(AsText(result), out var parsedDecimal))
                    value = parsedDecimal;
                else
                    return false;
                return true;

            case AttributeDataType.DateTime:
                // A date is held canonically, and text is read only in ISO 8601, with no offset meaning UTC.
                if (!ExpressionDates.TryRead(result, out var date))
                    return false;
                value = date;
                return true;

            case AttributeDataType.Boolean:
                if (result is bool boolValue)
                    value = boolValue;
                else if (bool.TryParse(AsText(result), out var parsedBool))
                    value = parsedBool;
                else
                    return false;
                return true;

            case AttributeDataType.Guid:
                if (result is Guid guid)
                    value = guid;
                else if (Guid.TryParse(AsText(result), out var parsedGuid))
                    value = parsedGuid;
                else
                    return false;
                return true;

            case AttributeDataType.Binary:
                // Only a Binary value converts; the expression already holds its own copy (N6).
                if (result is not byte[] bytes)
                    return false;
                value = bytes;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The result's text form for parsing, or null where it has none (Binary, several values), which no parse accepts.
    /// </summary>
    private static string? AsText(object? result) => ExpressionText.TryFormat(result, out var text) ? text : null;
}
