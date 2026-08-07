// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Utilities;
using System.Globalization;

namespace JIM.Connectors.Sql;

/// <summary>
/// Converts a keyset-pagination anchor between its database value and the string form carried in a
/// Connected System Pagination Token.
/// <para>
/// The token is the only thing that positions the next page, and it survives a round trip through the
/// database as text, so every conversion here is invariant-culture and lossless. Decimal anchors are
/// the case that matters most in practice: an Oracle primary key is typically a NUMBER, which JIM maps
/// to Decimal, so routing one through <c>double</c> or a culture-sensitive format would drop digits
/// or write "1,5", and the next page would resume from the wrong row without any error.
/// </para>
/// </summary>
internal static class SqlAnchorValue
{
    /// <summary>
    /// Round-trip format: sortable, unambiguous and parsed back to the same value with its UTC kind.
    /// </summary>
    private const string DateTimeTokenFormat = "O";

    /// <summary>
    /// Renders an anchor value for a Connected System Pagination Token.
    /// </summary>
    /// <exception cref="NotSupportedException">The attribute type cannot order a keyset page.</exception>
    internal static string ToTokenString(object value, AttributeDataType type)
    {
        ArgumentNullException.ThrowIfNull(value);

        return type switch
        {
            AttributeDataType.Text => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            AttributeDataType.Number => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            AttributeDataType.LongNumber => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),

            // The single source of truth for decimal string form; never a plain ToString.
            AttributeDataType.Decimal => DecimalAttributeValue.ToCanonicalString(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
            AttributeDataType.Guid => ToGuid(value).ToString("D", CultureInfo.InvariantCulture),
            AttributeDataType.DateTime => ToUtc(Convert.ToDateTime(value, CultureInfo.InvariantCulture)).ToString(DateTimeTokenFormat, CultureInfo.InvariantCulture),
            AttributeDataType.Binary => Convert.ToHexString(ToBytes(value)),
            _ => throw new NotSupportedException($"A {type} column cannot be a keyset pagination anchor; an anchor must impose a total order on the rows.")
        };
    }

    /// <summary>
    /// Renders an anchor value whose JIM attribute type is not in hand, from what the driver returned.
    /// </summary>
    /// <remarks>
    /// The one caller is an export create against a database-generated key: the value arrives from an
    /// identity or a sequence, and JIM holds no schema for a column it has never imported. Every branch
    /// renders exactly as the typed overload above would for the same value, so the external ID this
    /// produces is the one the confirming import composes for the same row.
    /// </remarks>
    internal static string ToTokenString(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            string text => text,
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToHexString(bytes),
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime.ToString(DateTimeTokenFormat, CultureInfo.InvariantCulture),
            DateTime dateTime => ToUtc(dateTime).ToString(DateTimeTokenFormat, CultureInfo.InvariantCulture),

            // Never a plain ToString for a number the database generated: 5.00 and 5.0 have to produce
            // the same string, or a confirming import reads them as two different objects.
            decimal number => DecimalAttributeValue.ToCanonicalString(number),
            float or double => DecimalAttributeValue.ToCanonicalString(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    /// <summary>
    /// Parses an anchor value back out of a Connected System Pagination Token. Returns false rather
    /// than throwing so the caller can turn a corrupt or stale token into a clear run error; it must
    /// never round, truncate or guess.
    /// </summary>
    internal static bool TryFromTokenString(string? token, AttributeDataType type, out object? value)
    {
        value = null;

        if (string.IsNullOrEmpty(token))
            return false;

        switch (type)
        {
            case AttributeDataType.Text:
                value = token;
                return true;

            case AttributeDataType.Number:
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                    return false;
                value = intValue;
                return true;

            case AttributeDataType.LongNumber:
                if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                    return false;
                value = longValue;
                return true;

            case AttributeDataType.Decimal:
                if (!DecimalAttributeValue.TryParse(token, out var decimalValue))
                    return false;
                value = decimalValue;
                return true;

            case AttributeDataType.Guid:
                if (!IdentifierParser.TryFromString(token, out var guidValue))
                    return false;
                value = guidValue;
                return true;

            case AttributeDataType.DateTime:
                if (!DateTime.TryParseExact(token, DateTimeTokenFormat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTimeValue))
                    return false;
                value = ToUtc(dateTimeValue);
                return true;

            case AttributeDataType.Binary:
                try
                {
                    value = Convert.FromHexString(token);
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// JIM holds every DateTime in UTC. An unspecified kind is taken to be UTC already rather than
    /// converted from the host's local time, which would silently move the watermark by the offset of
    /// whichever machine ran the import.
    /// </summary>
    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static Guid ToGuid(object value)
    {
        return value switch
        {
            Guid guid => guid,
            string text => IdentifierParser.FromString(text),
            _ => throw new ArgumentException($"A Guid anchor cannot be built from a {value.GetType().Name} value; byte order is dialect-specific, so bytes must be converted through the provider first.", nameof(value))
        };
    }

    private static byte[] ToBytes(object value)
    {
        return value as byte[] ?? throw new ArgumentException($"A Binary anchor cannot be built from a {value.GetType().Name} value.", nameof(value));
    }
}
