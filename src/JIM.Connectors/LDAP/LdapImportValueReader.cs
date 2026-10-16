// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Utilities;
using Serilog;
using System.Diagnostics.CodeAnalysis;
using System.DirectoryServices.Protocols;
using System.Globalization;
namespace JIM.Connectors.LDAP;

/// <summary>
/// Reads a directory attribute's values as the data type JIM holds the attribute as, keeping each value it cannot read
/// so the import can report it (#2128). A value dropped instead leaves JIM reading the attribute as holding no value,
/// and a value read leniently can import a wrong one, which is worse.
/// </summary>
/// <remarks>
/// Every method takes the attribute in hand rather than the entry it came from, so an attribute the directory answered
/// in ranges (<see cref="LdapRangedAttribute"/>) reads exactly as one it returned whole. An empty value is no value,
/// as it is for a Text attribute, not an unreadable one.
/// </remarks>
internal static class LdapImportValueReader
{
    // UTCTime's two-digit years cover these, by the RFC 5280 convention: 50 to 99 are 19xx, 00 to 49 are 20xx.
    private const int UtcTimeFirstYear = 1950;
    private const int UtcTimeLastYear = 2049;

    // Fraction digits beyond these are below a DateTime's 100ns resolution for any unit, and would overflow the sum.
    private const int MaxFractionDigits = 18;

    // The ISO 8601 forms JIM reads besides the directory's own, each with a time of day so that nothing is guessed: a
    // date alone, a time alone or a culture's month and day order is not read.
    private static readonly string[] IsoDateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    /// <summary>
    /// Reads whole numbers that fit a Number (32 bits). A value with anything but digits and a sign, or one beyond
    /// that range, is unreadable.
    /// </summary>
    internal static LdapImportValues<int> ReadNumbers(DirectoryAttribute attribute, string attributeName, string? entryDn)
    {
        var read = new LdapImportValues<int>();
        foreach (var value in TextValues(attribute))
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                read.Values.Add(number);
            else
                read.AddUnreadable(value);
        }

        RemoveDuplicates(read.Values, attributeName, entryDn);
        return read;
    }

    /// <summary>
    /// Reads whole numbers that fit a Long Number (64 bits), as <see cref="ReadNumbers"/> does for a Number.
    /// </summary>
    internal static LdapImportValues<long> ReadLongNumbers(DirectoryAttribute attribute, string attributeName, string? entryDn)
    {
        var read = new LdapImportValues<long>();
        foreach (var value in TextValues(attribute))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                read.Values.Add(number);
            else
                read.AddUnreadable(value);
        }

        RemoveDuplicates(read.Values, attributeName, entryDn);
        return read;
    }

    /// <summary>
    /// Reads a Boolean: <c>TRUE</c> or <c>FALSE</c> (RFC 4517 3.3.3), in any case. Anything else is unreadable rather
    /// than false. Every value is read: a true or false attribute holds one value, and JIM itself reports an attribute
    /// holding both (#2153), the same way for every Connector, rather than the reader choosing one.
    /// </summary>
    internal static LdapImportValues<bool> ReadBoolean(DirectoryAttribute attribute)
    {
        var read = new LdapImportValues<bool>();
        foreach (var value in TextValues(attribute))
        {
            if (value.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                read.Values.Add(true);
            else if (value.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                read.Values.Add(false);
            else
                read.AddUnreadable(value);
        }

        return read;
    }

    /// <summary>
    /// Reads every value of a date and time attribute as UTC, from any form <see cref="TryParseDirectoryTime(string, out DateTime)"/>
    /// reads (#2153). The same instant in two forms is one value. Two values that differ only below the 100 nanoseconds
    /// a DateTime holds would read as one, so each is reported with <see cref="UnreadableValueReasons.FinerThanJimReads"/>
    /// rather than merged silently. JIM itself puts each value into the form it holds (to the microsecond).
    /// </summary>
    internal static LdapImportValues<DateTime> ReadDateTime(DirectoryAttribute attribute)
    {
        var read = new LdapImportValues<DateTime>();

        // Each value read, with its text and whether digits finer than 100ns were dropped from it. Allocated only for an
        // attribute with more than one value; the single-valued case reads straight into the result.
        List<(string Text, DateTime Utc, bool Truncated)>? several = null;
        string? first = null;
        foreach (var value in TextValues(attribute))
        {
            if (first == null)
            {
                first = value;
                continue;
            }

            if (several == null)
            {
                several = [];
                AddParsed(first, several, read);
            }

            AddParsed(value, several, read);
        }

        if (several == null)
        {
            if (first == null)
                return read;

            if (TryParseDirectoryTime(first, out var utc, out _))
                read.Values.Add(utc);
            else
                read.AddUnreadable(first);
            return read;
        }

        var instants = new Dictionary<long, List<(string Text, DateTime Utc, bool Truncated)>>();
        var order = new List<long>();
        foreach (var parsed in several)
        {
            if (!instants.TryGetValue(parsed.Utc.Ticks, out var same))
            {
                same = [];
                instants[parsed.Utc.Ticks] = same;
                order.Add(parsed.Utc.Ticks);
            }

            same.Add(parsed);
        }

        foreach (var same in order.Select(ticks => instants[ticks]))
        {
            if (same.Count > 1 && same.Exists(p => p.Truncated))
            {
                foreach (var colliding in same)
                    read.AddUnreadable(colliding.Text, UnreadableValueReasons.FinerThanJimReads);
                continue;
            }

            read.Values.Add(same[0].Utc);
        }

        return read;
    }

    private static void AddParsed(string value, List<(string Text, DateTime Utc, bool Truncated)> parsed, LdapImportValues<DateTime> read)
    {
        if (TryParseDirectoryTime(value, out var utc, out var truncated))
            parsed.Add((value, utc, truncated));
        else
            read.AddUnreadable(value);
    }

    /// <summary>
    /// Reads GUIDs held as 16 bytes in Microsoft byte order, as Active Directory's and Samba AD's <c>objectGUID</c> is.
    /// A value of any other length is unreadable, given as hexadecimal.
    /// </summary>
    /// <remarks>
    /// Not for an RFC 4122 binary UUID, whose first three components are big-endian; OpenLDAP's and 389 Directory
    /// Server's <c>entryUUID</c> is text (RFC 4530) and is read as Text.
    /// </remarks>
    internal static LdapImportValues<Guid> ReadGuids(DirectoryAttribute attribute, string attributeName, string? entryDn)
    {
        var read = new LdapImportValues<Guid>();
        foreach (var bytes in attribute.GetValues(typeof(byte[])).Cast<byte[]>())
        {
            if (bytes.Length == 16)
                read.Values.Add(IdentifierParser.FromMicrosoftBytes(bytes));
            else
                read.AddUnreadable(Convert.ToHexString(bytes));
        }

        RemoveDuplicates(read.Values, attributeName, entryDn);
        return read;
    }

    /// <summary>
    /// Reads a directory's date and time as UTC. Three forms are read, and nothing else is:
    /// <list type="bullet">
    ///   <item>GeneralizedTime (RFC 4517 3.3.13): <c>yyyyMMddHH</c>, then optionally minutes and seconds (a leap
    ///   second, 60, is held as 59), an optional fraction after <c>.</c> or <c>,</c> applying to the last unit given,
    ///   and an optional time zone, <c>Z</c> or <c>+</c>/<c>-</c> with hours and optional minutes. No time zone is
    ///   read as UTC, as directories write it.</item>
    ///   <item>UTCTime: <c>yyMMddHHmm</c>, optional seconds, then <c>Z</c> or <c>+</c>/<c>-</c><c>hhmm</c>; years 50
    ///   to 99 are 19xx and 00 to 49 are 20xx. An RFC 4512 UTC Time attribute, and an Active Directory one (omSyntax
    ///   23), is a DateTime attribute too.</item>
    ///   <item>ISO 8601 with a time: <c>yyyy-MM-ddTHH:mm:ss</c>, an optional fraction, and an optional <c>Z</c> or
    ///   offset.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Ten or twelve digits and a time zone are both a GeneralizedTime given to the hour or minute and a UTCTime, and
    /// the value alone does not say which the directory holds. UTCTime cannot mean a year outside 1950 to 2049, so
    /// where the GeneralizedTime reading falls outside them (12 January 2501 for <c>250101120000Z</c>) the value is the
    /// UTCTime. Where both readings fall inside them either could be meant, and the value is not read rather than
    /// risking a wrong date.
    /// </remarks>
    internal static bool TryParseDirectoryTime(string? value, out DateTime utc) => TryParseDirectoryTime(value, out utc, out _);

    /// <summary>
    /// <see cref="TryParseDirectoryTime(string, out DateTime)"/>, also saying whether the value carried digits finer
    /// than the 100 nanoseconds a DateTime holds, which reading it dropped (#2153).
    /// </summary>
    internal static bool TryParseDirectoryTime(string? value, out DateTime utc, out bool truncated)
    {
        utc = default;
        truncated = false;
        if (string.IsNullOrEmpty(value))
            return false;

        var isGeneralizedTime = TryParseGeneralizedTime(value, out var generalizedTime, out var generalizedTruncated);
        var isUtcTime = TryParseUtcTime(value, out var utcTime);
        if (isGeneralizedTime && isUtcTime && generalizedTime.Year is >= UtcTimeFirstYear and <= UtcTimeLastYear)
            return false;

        if (isUtcTime)
        {
            utc = utcTime;
            return true;
        }

        if (isGeneralizedTime)
        {
            utc = generalizedTime;
            truncated = generalizedTruncated;
            return true;
        }

        return DateTime.TryParseExact(value, IsoDateTimeFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    private static bool TryParseGeneralizedTime(string value, out DateTime utc, out bool truncated)
    {
        utc = default;
        truncated = false;
        var position = 0;
        if (!TryReadDigits(value, ref position, 4, out var year) ||
            !TryReadDigits(value, ref position, 2, out var month) ||
            !TryReadDigits(value, ref position, 2, out var day) ||
            !TryReadDigits(value, ref position, 2, out var hour))
            return false;

        // Minutes and seconds are each optional, and a fraction is of whichever unit came last.
        var minute = 0;
        var second = 0;
        var lastUnitTicks = TimeSpan.TicksPerHour;
        if (TryReadDigits(value, ref position, 2, out var readMinute))
        {
            minute = readMinute;
            lastUnitTicks = TimeSpan.TicksPerMinute;
            if (TryReadDigits(value, ref position, 2, out var readSecond))
            {
                second = readSecond;
                lastUnitTicks = TimeSpan.TicksPerSecond;
            }
        }

        if (second > 60)
            return false;

        long fractionTicks = 0;
        if (position < value.Length && value[position] is '.' or ',')
        {
            position++;
            if (!TryReadFraction(value, ref position, lastUnitTicks, out fractionTicks, out truncated))
                return false;
        }

        var offset = TimeSpan.Zero;
        if (position < value.Length && value[position] == 'Z')
            position++;
        else if (position < value.Length && !TryReadOffset(value, ref position, minutesRequired: false, out offset))
            return false;

        // A leap second is held as the second before it: a DateTime has no 60th second.
        return position == value.Length &&
            TryCompose(year, month, day, hour, minute, Math.Min(second, 59), fractionTicks, offset, out utc);
    }

    private static bool TryParseUtcTime(string value, out DateTime utc)
    {
        utc = default;
        var position = 0;
        if (!TryReadDigits(value, ref position, 2, out var twoDigitYear) ||
            !TryReadDigits(value, ref position, 2, out var month) ||
            !TryReadDigits(value, ref position, 2, out var day) ||
            !TryReadDigits(value, ref position, 2, out var hour) ||
            !TryReadDigits(value, ref position, 2, out var minute))
            return false;

        var second = 0;
        if (TryReadDigits(value, ref position, 2, out var readSecond))
            second = readSecond;

        // UTCTime always carries its time zone.
        var offset = TimeSpan.Zero;
        if (position < value.Length && value[position] == 'Z')
            position++;
        else if (!TryReadOffset(value, ref position, minutesRequired: true, out offset))
            return false;

        var year = twoDigitYear >= UtcTimeFirstYear % 100 ? 1900 + twoDigitYear : 2000 + twoDigitYear;
        return position == value.Length && TryCompose(year, month, day, hour, minute, second, 0, offset, out utc);
    }

    /// <summary>
    /// Reads <c>+</c> or <c>-</c>, two digits of hours (00 to 23) and two of minutes (00 to 59), which may be absent
    /// unless <paramref name="minutesRequired"/>.
    /// </summary>
    private static bool TryReadOffset(string value, ref int position, bool minutesRequired, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (position >= value.Length || value[position] is not ('+' or '-'))
            return false;

        var sign = value[position] == '-' ? -1 : 1;
        position++;
        if (!TryReadDigits(value, ref position, 2, out var hours) || hours > 23)
            return false;

        var minutes = 0;
        if (TryReadDigits(value, ref position, 2, out var readMinutes))
            minutes = readMinutes;
        else if (minutesRequired)
            return false;

        if (minutes > 59)
            return false;

        offset = new TimeSpan(sign * hours, sign * minutes, 0);
        return true;
    }

    /// <summary>
    /// Reads one or more digits as a fraction of <paramref name="unitTicks"/>, truncated to the 100ns a DateTime holds;
    /// <paramref name="truncated"/> says whether anything non-zero was dropped.
    /// </summary>
    private static bool TryReadFraction(string value, ref int position, long unitTicks, out long ticks, out bool truncated)
    {
        truncated = false;
        var start = position;
        long numerator = 0;
        long denominator = 1;
        while (position < value.Length && char.IsAsciiDigit(value[position]))
        {
            if (position - start < MaxFractionDigits)
            {
                numerator = (numerator * 10) + (value[position] - '0');
                denominator *= 10;
            }
            else if (value[position] != '0')
            {
                truncated = true;
            }

            position++;
        }

        var scaled = (Int128)unitTicks * numerator;
        ticks = (long)(scaled / denominator);
        truncated |= scaled % denominator != 0;
        return position > start;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> ASCII digits, consuming nothing when they are not all there.
    /// </summary>
    private static bool TryReadDigits(string value, ref int position, int count, out int number)
    {
        number = 0;
        if (position + count > value.Length)
            return false;

        var read = 0;
        for (var i = position; i < position + count; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
                return false;

            read = (read * 10) + (value[i] - '0');
        }

        number = read;
        position += count;
        return true;
    }

    /// <summary>
    /// Builds the UTC instant a directory's date and time denotes, refusing a date that does not exist (the 31st of
    /// February) or an instant beyond what a DateTime can hold.
    /// </summary>
    private static bool TryCompose(int year, int month, int day, int hour, int minute, int second, long fractionTicks, TimeSpan offset, out DateTime utc)
    {
        utc = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 59)
            return false;

        var utcTicks = new DateTime(year, month, day, hour, minute, second).Ticks + fractionTicks - offset.Ticks;
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
            return false;

        utc = new DateTime(utcTicks, DateTimeKind.Utc);
        return true;
    }

    private static IEnumerable<string> TextValues(DirectoryAttribute attribute) =>
        attribute.GetValues(typeof(string)).Cast<string>().Where(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>
    /// Removes duplicate values defensively: a directory should not return any, but corrupt data or a fault in the
    /// directory can.
    /// </summary>
    private static void RemoveDuplicates<T>(List<T> values, string attributeName, string? entryDn)
    {
        if (values.Count < 2)
            return;

        var uniqueValues = values.Distinct().ToList();
        if (uniqueValues.Count == values.Count)
            return;

        Log.Warning("LdapImportValueReader: Detected and removed {DuplicateCount} duplicate value(s) from attribute '{AttributeName}' on entry '{EntryDn}'. " +
            "Original count: {OriginalCount}, Unique count: {UniqueCount}",
            values.Count - uniqueValues.Count, LogSanitiser.Sanitise(attributeName), LogSanitiser.Sanitise(entryDn), values.Count, uniqueValues.Count);
        values.Clear();
        values.AddRange(uniqueValues);
    }
}
