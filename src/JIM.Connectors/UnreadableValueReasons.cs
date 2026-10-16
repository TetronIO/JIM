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
    /// The reason each of two or more date values is not imported when they differ only below the 100 nanoseconds a
    /// Connector can read (#2153): read, they would be one value, and merging them silently would lose one.
    /// </summary>
    internal const string FinerThanJimReads =
        "Differs from another of this attribute's values by less than 100 nanoseconds, finer than JIM can read, so JIM cannot keep both.";

    private static bool IsWholeNumberOutside(string? value, BigInteger minimum, BigInteger maximum) =>
        BigInteger.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        (number < minimum || number > maximum);
}
