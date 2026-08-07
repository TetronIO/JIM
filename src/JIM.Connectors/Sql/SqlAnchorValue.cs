// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Utilities;
using System.Globalization;

namespace JIM.Connectors.Sql;

/// <summary>
/// The single crossing point between a value as a database driver hands it over and the string form JIM
/// carries it as: an anchor part of an external ID, a Connected System Pagination Token, or a Delta
/// Import watermark.
/// <para>
/// The token is the only thing that identifies an object between an export and its confirming import,
/// and the only thing that positions the next page, so every conversion here is invariant-culture and
/// lossless, and there is exactly one routine per direction. Two routines that happen to agree today is
/// how an Oracle table whose key is <c>RAW(16) DEFAULT SYS_GUID()</c> came to compose its key as a
/// hyphenated GUID on the way in and as hex on the way out, leaving an exported object that no import
/// could ever find again.
/// </para>
/// <para>
/// Decimal anchors are the case that matters most in practice: an Oracle primary key is typically a
/// NUMBER, which JIM maps to Decimal, so routing one through <c>double</c> or a culture-sensitive format
/// would drop digits or write "1,5", and the next page would resume from the wrong row without any error.
/// </para>
/// <para>
/// <b>The dialect seam is crossed here, not at the call site.</b> A GUID's byte order is the database
/// server's own (Oracle stores RAW(16) big-endian, Microsoft SQL Server stores <c>uniqueidentifier</c>
/// with its first three components little-endian), so the provider converts it. Taking the provider as a
/// parameter is what makes that impossible to forget: there is no way to compose a token without one.
/// </para>
/// </summary>
internal static class SqlAnchorValue
{
    /// <summary>
    /// Round-trip format: sortable, unambiguous and parsed back to the same value with its UTC kind.
    /// </summary>
    private const string DateTimeTokenFormat = "O";

    /// <summary>
    /// Renders a value a driver handed back, or one about to be bound, as the string JIM carries it as.
    /// </summary>
    /// <param name="provider">The dialect the value came from, which decides how a GUID's bytes are read.</param>
    /// <param name="value">The value, exactly as the driver returned it or as it will be bound.</param>
    /// <param name="type">The JIM attribute type of the column it belongs to.</param>
    /// <exception cref="NotSupportedException">The attribute type cannot identify an object or order a keyset page.</exception>
    internal static string ToTokenString(ISqlProvider provider, object value, AttributeDataType type)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(value);

        return type switch
        {
            AttributeDataType.Text => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            AttributeDataType.Number => Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            AttributeDataType.LongNumber => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),

            // The single source of truth for decimal string form; never a plain ToString.
            AttributeDataType.Decimal => DecimalAttributeValue.ToCanonicalString(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),

            // Byte order is the dialect's, and reading it with the wrong one transposes the first three
            // components silently, so the value crosses the seam before it is ever rendered.
            AttributeDataType.Guid => provider.ConvertToGuid(value).ToString("D", CultureInfo.InvariantCulture),
            AttributeDataType.DateTime => ToUtc(value).ToString(DateTimeTokenFormat, CultureInfo.InvariantCulture),
            AttributeDataType.Binary => Convert.ToHexString(ToBytes(value)),
            _ => throw new NotSupportedException($"A {type} column cannot identify an object or order a keyset page; an anchor must impose a total order on the rows.")
        };
    }

    /// <summary>
    /// Turns a token back into the value this dialect's driver binds, which is the exact inverse of
    /// <see cref="ToTokenString"/>. Returns false rather than throwing so the caller can turn a corrupt
    /// or stale token into a clear run error; it must never round, truncate or guess.
    /// </summary>
    /// <remarks>
    /// The value handed back is in the shape the driver expects, not necessarily the shape JIM holds it
    /// in: a GUID goes back through the provider, so against Oracle it emerges as the RAW(16) bytes the
    /// column takes. Binding what came out of the token unconverted is how a page resumes from a
    /// transposed identifier and silently re-reads or skips rows.
    /// </remarks>
    internal static bool TryFromTokenString(ISqlProvider provider, string? token, AttributeDataType type, out object? value)
    {
        ArgumentNullException.ThrowIfNull(provider);
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
                value = provider.ConvertFromGuid(guidValue);
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
    /// JIM holds every DateTime in UTC. A value carrying its own offset (which is what a driver returns
    /// for a <c>datetimeoffset</c> or a <c>TIMESTAMP WITH TIME ZONE</c> column) is taken at the instant
    /// it names; a value carrying no offset at all is taken to be UTC already rather than converted from
    /// the host's local time, which would silently move the watermark by the offset of whichever machine
    /// ran the import.
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeOffset"/> is handled here rather than left to <see cref="Convert.ToDateTime(object?, IFormatProvider?)"/>,
    /// which throws for it: the type does not implement <see cref="IConvertible"/>. A date and time is a
    /// pathological primary key and a perfectly ordinary Delta Import watermark, and both reach this
    /// method, so refusing the offset-bearing case would take away the second to punish the first.
    /// </remarks>
    private static DateTime ToUtc(object value)
    {
        if (value is DateTimeOffset dateTimeOffset)
            return dateTimeOffset.UtcDateTime;

        var dateTime = Convert.ToDateTime(value, CultureInfo.InvariantCulture);

        return dateTime.Kind switch
        {
            DateTimeKind.Utc => dateTime,
            DateTimeKind.Local => dateTime.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
        };
    }

    private static byte[] ToBytes(object value)
    {
        return value as byte[] ?? throw new ArgumentException($"A Binary anchor cannot be built from a {value.GetType().Name} value.", nameof(value));
    }
}
