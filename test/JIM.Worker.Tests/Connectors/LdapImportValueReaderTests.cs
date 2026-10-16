// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors;
using JIM.Connectors.LDAP;
using JIM.Models.Core;
using System.DirectoryServices.Protocols;
using System.Globalization;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's typed reading of directory values (#2128): what it reads, and what it hands back as
/// unreadable rather than dropping or misreading.
/// </summary>
[TestFixture]
public class LdapImportValueReaderTests
{
    // GeneralizedTime (RFC 4517 3.3.13): hour required, minute and second optional, a fraction applying to the last
    // unit given, a leap second, and an optional time zone (none is read as UTC).
    [TestCase("20231215143000Z", "2023-12-15T14:30:00.0000000")]
    [TestCase("20231215143000.123456Z", "2023-12-15T14:30:00.1234560")]
    [TestCase("20231215143000,5Z", "2023-12-15T14:30:00.5000000")]
    [TestCase("20231215143000.12345678Z", "2023-12-15T14:30:00.1234567", Description = "beyond the 100ns a DateTime holds, truncated")]
    [TestCase("20231215143000+0530", "2023-12-15T09:00:00.0000000")]
    [TestCase("20231215143000-05", "2023-12-15T19:30:00.0000000")]
    [TestCase("20231215143000", "2023-12-15T14:30:00.0000000")]
    [TestCase("2023121514Z", "2023-12-15T14:00:00.0000000")]
    [TestCase("202312151430Z", "2023-12-15T14:30:00.0000000")]
    [TestCase("2023121514.5Z", "2023-12-15T14:30:00.0000000", Description = "a fraction of the hour")]
    [TestCase("202312151430,25Z", "2023-12-15T14:30:15.0000000", Description = "a fraction of the minute")]
    [TestCase("20231231235960Z", "2023-12-31T23:59:59.0000000", Description = "a leap second, held as the second before it")]
    // UTCTime (two-digit year, 1950 to 2049), which the schema maps to DateTime as well.
    [TestCase("231215143000Z", "2023-12-15T14:30:00.0000000")]
    [TestCase("2312151430Z", "2023-12-15T14:30:00.0000000")]
    [TestCase("231215143000+0100", "2023-12-15T13:30:00.0000000")]
    [TestCase("491215143000Z", "2049-12-15T14:30:00.0000000")]
    [TestCase("501215143000Z", "1950-12-15T14:30:00.0000000")]
    [TestCase("250101120000Z", "2025-01-01T12:00:00.0000000", Description = "also GeneralizedTime for 12 January 2501, which no directory means")]
    // ISO 8601 with a time, the one other form JIM reads.
    [TestCase("2023-12-15T14:30:00Z", "2023-12-15T14:30:00.0000000")]
    [TestCase("2023-12-15T14:30:00.5Z", "2023-12-15T14:30:00.5000000")]
    [TestCase("2023-12-15T14:30:00+05:30", "2023-12-15T09:00:00.0000000")]
    [TestCase("2023-12-15T14:30:00", "2023-12-15T14:30:00.0000000")]
    public void TryParseDirectoryTime_ValueInADirectoryTimeForm_ReadsItAsUtc(string value, string expectedUtc)
    {
        var parsed = LdapImportValueReader.TryParseDirectoryTime(value, out var utc);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(utc, Is.EqualTo(DateTime.ParseExact(expectedUtc, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)));
            Assert.That(utc.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [TestCase("")]
    [TestCase("not-a-date")]
    [TestCase("20230231000000Z", Description = "the 31st of February")]
    [TestCase("20231215243000Z", Description = "hour 24")]
    [TestCase("20231215146000Z", Description = "minute 60")]
    [TestCase("20231215143061Z", Description = "second 61")]
    [TestCase("20231215143000.Z", Description = "a fraction with no digits")]
    [TestCase("20231215143000+24", Description = "a time zone of 24 hours")]
    [TestCase("20231215143000+0560", Description = "a time zone of 60 minutes")]
    [TestCase("20231215143000+ab")]
    [TestCase("20231215143000Zjunk")]
    [TestCase("20231215143000Z ", Description = "a trailing space")]
    [TestCase("20231215143000z", Description = "the time zone letter is upper case")]
    [TestCase("00000101000000Z", Description = "year 0")]
    [TestCase("99991231235959-0100", Description = "past the last date a DateTime holds once made UTC")]
    [TestCase("202312151", Description = "no complete hour")]
    [TestCase("2312151430+01", Description = "UTCTime's time zone has four digits")]
    [TestCase("200805121430Z", Description = "12 May 2008 14:30 as GeneralizedTime, 5 August 2020 12:14:30 as UTCTime")]
    [TestCase("14:30")]
    [TestCase("Dec 2023")]
    [TestCase("12/15/2023")]
    [TestCase("2023-12-15", Description = "a date with no time")]
    public void TryParseDirectoryTime_ValueInNoDirectoryTimeForm_IsNotRead(string value)
    {
        Assert.That(LdapImportValueReader.TryParseDirectoryTime(value, out _), Is.False);
    }

    [Test]
    public void ReadNumbers_EmptyOrWhitespaceValue_IsNoValueRatherThanUnreadable()
    {
        var read = LdapImportValueReader.ReadNumbers(new DirectoryAttribute("n", "", "  ", "7"), "n", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[] { 7 }));
            Assert.That(read.Unreadable, Is.Empty);
        }
    }

    [Test]
    public void ReadNumbers_ValuesThatAreNotWholeNumbers_AreUnreadableAsReceived()
    {
        var read = LdapImportValueReader.ReadNumbers(new DirectoryAttribute("n", "1", "1.5", "2147483648", "-2147483648"), "n", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[] { 1, int.MinValue }));
            Assert.That(read.Unreadable.Select(u => u.Value), Is.EqualTo(new[] { "1.5", "2147483648" }));
        }
    }

    [Test]
    public void ReadNumbers_DuplicateValues_AreReadOnce()
    {
        var read = LdapImportValueReader.ReadNumbers(new DirectoryAttribute("n", "5", "5", "6"), "n", null);

        Assert.That(read.Values, Is.EqualTo(new[] { 5, 6 }));
    }

    [Test]
    public void ReadLongNumbers_ValueBeyondALongNumber_IsUnreadable()
    {
        var read = LdapImportValueReader.ReadLongNumbers(new DirectoryAttribute("n", "9223372036854775807", "9223372036854775808"), "n", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[] { long.MaxValue }));
            Assert.That(read.Unreadable.Select(u => u.Value), Is.EqualTo(new[] { "9223372036854775808" }));
        }
    }

    [Test]
    public void ReadBoolean_TrueAndFalse_ReadsBothForJimToReport()
    {
        // The reader hands JIM what the directory holds (#2153 S5); JIM's import normaliser reports a true or false
        // attribute holding both, once, for every Connector, rather than each Connector choosing.
        var read = LdapImportValueReader.ReadBoolean(new DirectoryAttribute("b", "TRUE", "FALSE"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[] { true, false }));
            Assert.That(read.Unreadable, Is.Empty);
        }
    }

    [Test]
    public void ReadDateTime_SeveralValues_ReadsEachAndReportsOnlyTheUnreadableOne()
    {
        var read = LdapImportValueReader.ReadDateTime(new DirectoryAttribute("d", "20231215143000Z", "nonsense", "20240101000000.123456Z"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[]
            {
                new DateTime(2023, 12, 15, 14, 30, 0, DateTimeKind.Utc),
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560)
            }));
            Assert.That(read.Unreadable.Select(u => (u.Value, u.Reason)), Is.EqualTo(new[] { ("nonsense", (string?)null) }));
        }
    }

    [Test]
    public void ReadDateTime_TwoValuesTheSameInstantInDifferentForms_CollapseWithoutReport()
    {
        var read = LdapImportValueReader.ReadDateTime(new DirectoryAttribute("d", "20231215143000Z", "20231215143000.0Z"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[] { new DateTime(2023, 12, 15, 14, 30, 0, DateTimeKind.Utc) }));
            Assert.That(read.Unreadable, Is.Empty);
        }
    }

    [Test]
    public void ReadDateTime_TwoValuesThatDifferBelow100Nanoseconds_ReportsBothAsColliding()
    {
        // A DateTime holds 100ns. Two directory values that differ only below that would read as one; merging them
        // silently would lose a value, so each is reported instead.
        var read = LdapImportValueReader.ReadDateTime(new DirectoryAttribute("d", "20231215143000.12345671Z", "20231215143000.12345678Z", "20240101000000Z"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[] { new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) }));
            Assert.That(read.Unreadable.Select(u => (u.Value, u.Reason)), Is.EqualTo(new[]
            {
                ("20231215143000.12345671Z", (string?)UnreadableValueReasons.FinerThanJimReads),
                ("20231215143000.12345678Z", (string?)UnreadableValueReasons.FinerThanJimReads)
            }));
        }
    }

    [Test]
    public void ReadDateTime_OneValueBesideAnEmptyOne_ReadsTheOne()
    {
        // An empty value is no value, so it does not make the attribute one of several values.
        var read = LdapImportValueReader.ReadDateTime(new DirectoryAttribute("d", "", "20231215143000Z"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[] { new DateTime(2023, 12, 15, 14, 30, 0, DateTimeKind.Utc) }));
            Assert.That(read.Unreadable, Is.Empty);
        }
    }

    [Test]
    public void ReadBoolean_ValueThatIsNotTrueOrFalse_IsUnreadableWithTheReasonItsTypeGives()
    {
        // No reason of the reader's own: the caller words it from the attribute's type, as for every other type.
        var read = LdapImportValueReader.ReadBoolean(new DirectoryAttribute("b", "yes"));

        Assert.That(read.Unreadable.Select(u => (u.Value, u.Reason)), Is.EqualTo(new[] { ("yes", (string?)null) }));
    }

    [Test]
    public void ReadNumbers_ValuesAsASearchResultHoldsThem_AreRead()
    {
        var read = LdapImportValueReader.ReadNumbers(LdapTestResponses.SearchResultAttribute("n", "42"u8.ToArray(), "x"u8.ToArray()), "n", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[] { 42 }));
            Assert.That(read.Unreadable.Select(u => u.Value), Is.EqualTo(new[] { "x" }));
        }
    }

    [Test]
    public void ReadBoolean_ValueThatIsNotUtf8Text_IsUnreadableRatherThanNoValue()
    {
        // The directory client hands back the raw bytes when a value does not decode as UTF-8; read as text, it
        // must still arrive as a value to report rather than throw out of the import or vanish.
        var read = LdapImportValueReader.ReadBoolean(LdapTestResponses.SearchResultAttribute("b", new byte[] { 0xFF, 0xFE }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.Empty);
            Assert.That(read.Unreadable, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void ReadGuids_ValueAsASearchResultHoldsIt_IsRead()
    {
        var guid = Guid.NewGuid();

        var read = LdapImportValueReader.ReadGuids(LdapTestResponses.SearchResultAttribute("g", guid.ToByteArray()), "g", null);

        Assert.That(read.Values, Is.EqualTo(new[] { guid }));
    }

    [Test]
    public void ReadGuids_ValuesOfSixteenBytesAndOfAnotherLength_ReadsTheOneAndGivesTheOtherAsHex()
    {
        var guid = Guid.NewGuid();
        var read = LdapImportValueReader.ReadGuids(new DirectoryAttribute("g", guid.ToByteArray(), new byte[] { 0xAB, 0x01 }), "g", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Values, Is.EqualTo(new[] { guid }));
            Assert.That(read.Unreadable.Select(u => u.Value), Is.EqualTo(new[] { "AB01" }));
        }
    }
}
