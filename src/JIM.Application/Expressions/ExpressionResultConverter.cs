// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Utilities;

namespace JIM.Application.Expressions;

/// <summary>
/// Converts an expression's result to the value an attribute of a given type holds. Attribute Flow and Object
/// Matching both convert through here, so one expression produces the same typed value whichever of them evaluates
/// it. A result that cannot be represented exactly is refused rather than narrowed, rounded or guessed at (the #871
/// lossy-cast class): the caller decides what a refusal means.
/// </summary>
public static class ExpressionResultConverter
{
    /// <summary>
    /// Converts <paramref name="result"/> to the CLR value an attribute of <paramref name="targetType"/> stores:
    /// <see cref="string"/>, <see cref="int"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="DateTime"/>,
    /// <see cref="bool"/> or <see cref="Guid"/>.
    /// </summary>
    /// <returns>False when the result cannot be represented exactly, or the type is one no expression produces
    /// (Binary, Reference).</returns>
    public static bool TryConvert(object? result, AttributeDataType targetType, out object? value)
    {
        value = null;

        switch (targetType)
        {
            case AttributeDataType.Text:
                value = result?.ToString();
                return true;

            case AttributeDataType.Number:
                // A long result only converts when it fits in an int; silently narrowing would corrupt the value.
                if (result is int intValue)
                    value = intValue;
                else if (result is long longValue && longValue is >= int.MinValue and <= int.MaxValue)
                    value = (int)longValue;
                else if (result is not long && int.TryParse(result?.ToString(), out var parsedInt))
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
                else if (long.TryParse(result?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
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
                else if (result is not double && result is not float && DecimalAttributeValue.TryParse(result?.ToString(), out var parsedDecimal))
                    value = parsedDecimal;
                else
                    return false;
                return true;

            case AttributeDataType.DateTime:
                if (result is DateTime dateTime)
                    value = dateTime;
                else if (DateTime.TryParse(result?.ToString(), out var parsedDateTime))
                    value = parsedDateTime;
                else
                    return false;
                return true;

            case AttributeDataType.Boolean:
                if (result is bool boolValue)
                    value = boolValue;
                else if (bool.TryParse(result?.ToString(), out var parsedBool))
                    value = parsedBool;
                else
                    return false;
                return true;

            case AttributeDataType.Guid:
                if (result is Guid guid)
                    value = guid;
                else if (Guid.TryParse(result?.ToString(), out var parsedGuid))
                    value = parsedGuid;
                else
                    return false;
                return true;

            default:
                return false;
        }
    }
}
