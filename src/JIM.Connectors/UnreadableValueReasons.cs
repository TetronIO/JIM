// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.Globalization;
using System.Numerics;
namespace JIM.Connectors;

/// <summary>
/// Why a value could not be read, for the administrator (#2066), shared by the File and LDAP Connectors so the two
/// word the same fault the same way. Worded from the attribute's type rather than the parser's exception, whose text
/// is written for developers and can run to several lines.
/// </summary>
internal static class UnreadableValueReasons
{
    /// <summary>
    /// The reason a value could not be read as <paramref name="type"/>.
    /// </summary>
    /// <param name="type">The attribute's data type.</param>
    /// <param name="value">The value as received. A well-formed whole number outside the range of its type gets its
    /// own reason (#2128): the value is fine and the type is too small to hold it, which is not what "not a valid
    /// whole number" tells the administrator.</param>
    internal static string For(AttributeDataType type, string? value) => type switch
    {
        AttributeDataType.Number when IsWholeNumberOutside(value, int.MinValue, int.MaxValue) => "Not a whole number JIM can hold.",
        AttributeDataType.LongNumber when IsWholeNumberOutside(value, long.MinValue, long.MaxValue) => "Not a whole number JIM can hold.",
        AttributeDataType.Number or AttributeDataType.LongNumber => "Not a valid whole number.",
        AttributeDataType.Decimal => "Not a valid decimal number.",
        AttributeDataType.DateTime => "Not a valid date and time.",
        AttributeDataType.Boolean => "Not a valid true or false value.",
        AttributeDataType.Guid => "Not a valid GUID.",
        _ => $"Not a valid {type} value."
    };

    /// <summary>
    /// The reason each of an attribute's values is not imported when the directory returned several and JIM holds one
    /// value for an attribute of <paramref name="type"/>: a Boolean or a DateTime.
    /// </summary>
    /// <param name="type">The attribute's data type.</param>
    /// <param name="count">How many values the directory returned.</param>
    /// <remarks>
    /// Worded for a directory: the LDAP Connector is the one Connector that reads several values for these types. A
    /// stop-gap until #2153 slice S5, which imports several dates as they are and has JIM report a second Boolean
    /// value itself.
    /// </remarks>
    internal static string MoreValuesThanJimHolds(AttributeDataType type, int count) => type switch
    {
        AttributeDataType.DateTime => $"JIM holds one value for a date and time attribute, and the directory returned {count}.",
        AttributeDataType.Boolean => $"JIM holds one value for a true or false attribute, and the directory returned {count}.",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "JIM holds any number of values for an attribute of this type.")
    };

    private static bool IsWholeNumberOutside(string? value, BigInteger minimum, BigInteger maximum) =>
        BigInteger.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        (number < minimum || number > maximum);
}
