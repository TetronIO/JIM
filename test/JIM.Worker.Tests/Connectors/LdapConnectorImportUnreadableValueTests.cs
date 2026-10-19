// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A value the directory returns that cannot be read as its attribute's type is reported on the object (#2128), as
/// the File, SQL and SCIM Connectors already report theirs (#2066), rather than dropped (which JIM reads as the
/// attribute holding no value), misread, or thrown out of the import. Full Import and every Delta Import source
/// convert entries through the same seam these tests drive.
/// </summary>
[TestFixture]
public class LdapConnectorImportUnreadableValueTests
{
    private const string UserDn = "CN=Alex Smith,OU=Users,DC=corp,DC=local";
    private const string NotAWholeNumber = "Not a valid whole number.";
    private const string NotAWholeNumberJimCanHold = "Not a whole number JIM can hold.";
    private const string NotADateAndTime = "Not a valid date and time.";
    private const string NotTrueOrFalse = "Not a valid true or false value.";
    private const string NotAGuid = "Not a valid GUID.";

    [Test]
    public void ConvertEntries_NumberValueThatIsNotAWholeNumber_ReportsItAndLeavesTheAttributeOff()
    {
        var imported = ConvertUser(("uidNumber", ["12a"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value, e.Reason)),
                Is.EqualTo(new[] { ("uidNumber", (string?)"12a", NotAWholeNumber) }));
            Assert.That(AttributeNames(imported), Has.None.EqualTo("uidNumber").IgnoreCase,
                "Left off, so JIM keeps the value it already holds rather than reading the attribute as empty.");
            Assert.That(AttributeNames(imported), Has.Some.EqualTo("cn").IgnoreCase, "Every other attribute still imports.");
        }
    }

    [Test]
    public void ConvertEntries_NumberValueOutsideTheRangeANumberHolds_ReportsItAsAWholeNumberJimCannotHold()
    {
        // A Number attribute holds 32 bits: a larger value is well formed, so
        // "not a whole number" would send the administrator looking for a fault in the directory that is not there.
        var imported = ConvertUser(("uidNumber", ["2147483648"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value, e.Reason)),
                Is.EqualTo(new[] { ("uidNumber", (string?)"2147483648", NotAWholeNumberJimCanHold) }));
            Assert.That(AttributeNames(imported), Has.None.EqualTo("uidNumber").IgnoreCase);
        }
    }

    [Test]
    public void ConvertEntries_MultiValuedNumberWithOneUnreadableValue_ReportsItAndLeavesTheWholeAttributeOff()
    {
        var imported = ConvertUser(("badgeNumbers", ["1", "two", "3"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value)),
                Is.EqualTo(new[] { ("badgeNumbers", (string?)"two") }));
            Assert.That(AttributeNames(imported), Has.None.EqualTo("badgeNumbers").IgnoreCase,
                "Two of the three values is not the attribute; reading it as the whole set would remove the third.");
        }
    }

    [TestCase("never", NotAWholeNumber)]
    [TestCase("9223372036854775808", NotAWholeNumberJimCanHold)]
    public void ConvertEntries_LongNumberValueItCannotRead_ReportsItAndLeavesTheAttributeOff(string value, string expectedReason)
    {
        var imported = ConvertUser(("pwdLastSet", [value]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value, e.Reason)),
                Is.EqualTo(new[] { ("pwdLastSet", (string?)value, expectedReason) }));
            Assert.That(AttributeNames(imported), Has.None.EqualTo("pwdLastSet").IgnoreCase);
        }
    }

    [Test]
    public void ConvertEntries_AccountExpiresUnreadable_ReportsItRatherThanReadingItAsNeverExpires()
    {
        // An accountExpires arriving with no value means "never expires" to JIM, so an unreadable one must not.
        var imported = ConvertUser(("accountExpires", ["soon"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value)),
                Is.EqualTo(new[] { ("accountExpires", (string?)"soon") }));
            Assert.That(AttributeNames(imported), Has.None.EqualTo("accountExpires").IgnoreCase);
        }
    }

    [Test]
    public void ConvertEntries_AccountExpiresAtItsNeverExpiresDefault_ImportsTheAttributeWithNoValue()
    {
        // Active Directory cannot clear accountExpires and stores this sentinel instead; JIM reads it as no value,
        // the counterpart of export writing the sentinel for one.
        var imported = ConvertUser(("accountExpires", ["9223372036854775807"]));

        var accountExpires = imported.Attributes.SingleOrDefault(a => a.Name.Equals("accountExpires", StringComparison.OrdinalIgnoreCase));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors, Is.Null);
            Assert.That(accountExpires, Is.Not.Null);
            Assert.That(accountExpires!.LongValues, Is.Empty);
        }
    }

    [TestCase("yes")]
    [TestCase("1")]
    [TestCase("maybe")]
    public void ConvertEntries_BooleanValueOtherThanTrueOrFalse_ReportsItAndLeavesTheAttributeOff(string value)
    {
        var imported = ConvertUser(("isCriticalSystemObject", [value]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value, e.Reason)),
                Is.EqualTo(new[] { ("isCriticalSystemObject", (string?)value, NotTrueOrFalse) }));
            Assert.That(AttributeNames(imported), Has.None.EqualTo("isCriticalSystemObject").IgnoreCase,
                "Anything but TRUE was read as false.");
        }
    }

    [TestCase("TRUE", true)]
    [TestCase("true", true)]
    [TestCase("FALSE", false)]
    [TestCase("False", false)]
    public void ConvertEntries_BooleanTrueOrFalseInAnyCase_IsRead(string value, bool expected)
    {
        var imported = ConvertUser(("isCriticalSystemObject", [value]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors, Is.Null);
            Assert.That(Attribute(imported, "isCriticalSystemObject").BoolValues.Single(), Is.EqualTo(expected));
        }
    }

    [TestCase("")]
    [TestCase("   ")]
    public void ConvertEntries_BooleanValueThatIsEmpty_IsNoValueRatherThanFalse(string value)
    {
        var imported = ConvertUser(("isCriticalSystemObject", [value]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors, Is.Null);
            Assert.That(Attribute(imported, "isCriticalSystemObject").BoolValues, Is.Empty);
        }
    }

    [TestCase("20230231000000Z", Description = "the 31st of February")]
    [TestCase("not-a-date")]
    [TestCase("14:30", Description = "a time alone, which a lenient parse dates today")]
    [TestCase("Dec 2023")]
    [TestCase("12/15/2023", Description = "month and day order is a guess")]
    public void ConvertEntries_DateTimeValueThatIsNotADate_ReportsItAndLeavesTheAttributeOff(string value)
    {
        var imported = ConvertUser(("whenChanged", [value]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value, e.Reason)),
                Is.EqualTo(new[] { ("whenChanged", (string?)value, NotADateAndTime) }));
            Assert.That(AttributeNames(imported), Has.None.EqualTo("whenChanged").IgnoreCase);
        }
    }

    [TestCase("20231215143000Zjunk")]
    [TestCase("20231215143000+ab")]
    public void ConvertEntries_GeneralizedTimeWithCharactersAfterItsTimeZone_ReportsIt(string value)
    {
        var imported = ConvertUser(("whenChanged", [value]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value)),
                Is.EqualTo(new[] { ("whenChanged", (string?)value) }),
                "The leftover characters were ignored and the value read as 14:30 UTC.");
            Assert.That(AttributeNames(imported), Has.None.EqualTo("whenChanged").IgnoreCase);
        }
    }

    [Test]
    public void ConvertEntries_GeneralizedTimeWithACommaBeforeItsFraction_ReadsTheFraction()
    {
        var imported = ConvertUser(("whenChanged", ["20231215143000,5Z"]));

        var whenChanged = Attribute(imported, "whenChanged").DateTimeValues.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors, Is.Null);
            Assert.That(whenChanged, Is.EqualTo(new DateTime(2023, 12, 15, 14, 30, 0, 500, DateTimeKind.Utc)));
            Assert.That(whenChanged.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [TestCase("20231215143000Z", "2023-12-15T14:30:00")]
    [TestCase("20231215143000.0Z", "2023-12-15T14:30:00")]
    [TestCase("20231215143000+0530", "2023-12-15T09:00:00")]
    [TestCase("20231215143000-05", "2023-12-15T19:30:00")]
    [TestCase("20231215143000", "2023-12-15T14:30:00", Description = "no time zone is read as UTC")]
    [TestCase("2023-12-15T14:30:00Z", "2023-12-15T14:30:00")]
    [TestCase("2023121514Z", "2023-12-15T14:00:00", Description = "minutes and seconds are optional in GeneralizedTime")]
    [TestCase("231215143000Z", "2023-12-15T14:30:00", Description = "UTCTime, which the schema maps to DateTime too")]
    public void ConvertEntries_DateTimeForms_AreReadAsUtc(string value, string expectedUtc)
    {
        var imported = ConvertUser(("whenChanged", [value]));

        var whenChanged = Attribute(imported, "whenChanged").DateTimeValues.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors, Is.Null);
            Assert.That(whenChanged, Is.EqualTo(DateTime.SpecifyKind(DateTime.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)));
            Assert.That(whenChanged.Kind, Is.EqualTo(DateTimeKind.Utc));
        }
    }

    [Test]
    public void ConvertEntries_DateTimeWithMoreThanOneValue_ImportsEveryValue()
    {
        // #2153 S5: a multi-valued date imports every value, on Full and Delta Import alike (both convert entries here).
        // Before, the attribute was added with no value, which the import took as the directory holding none.
        var imported = ConvertUser(("whenChanged", ["20231215143000Z", "20240101000000.5Z"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors, Is.Null);
            Assert.That(Attribute(imported, "whenChanged").DateTimeValues, Is.EqualTo(new[]
            {
                new DateTime(2023, 12, 15, 14, 30, 0, DateTimeKind.Utc),
                new DateTime(2024, 1, 1, 0, 0, 0, 500, DateTimeKind.Utc)
            }));
        }
    }

    [Test]
    public void ConvertEntries_DateTimeWithAnUnreadableSecondValue_ReportsItAndLeavesTheAttributeOff()
    {
        var imported = ConvertUser(("whenChanged", ["20231215143000Z", "nonsense"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value)), Is.EqualTo(new[] { ("whenChanged", (string?)"nonsense") }));
            Assert.That(AttributeNames(imported), Has.None.EqualTo("whenChanged").IgnoreCase,
                "Left off, so JIM keeps the dates it already holds rather than reading part of the set as the whole.");
            Assert.That(imported.SourceLocation, Is.EqualTo(UserDn));
        }
    }

    [Test]
    public void ConvertEntries_BooleanWithMoreThanOneValue_ImportsBothForJimToReport()
    {
        var imported = ConvertUser(("isCriticalSystemObject", ["TRUE", "FALSE"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.AttributeValueErrors, Is.Null, "JIM, not the Connector, reports a true or false attribute holding both.");
            Assert.That(Attribute(imported, "isCriticalSystemObject").BoolValues, Is.EqualTo(new[] { true, false }));
        }
    }

    [Test]
    public void ConvertEntries_GuidValueThatIsNotSixteenBytes_ReportsItAsHexAndLeavesTheAttributeOff()
    {
        // objectGUID is Active Directory's External ID. A value of the wrong length threw out of the conversion,
        // which failed the whole Full or Delta Import.
        var fifteenBytes = Enumerable.Range(1, 15).Select(b => (byte)b).ToArray();
        var entry = LdapTestResponses.EntryWithRawValues(UserDn,
            ("objectClass", ["top", "user"]), ("cn", ["Alex Smith"]), ("objectGUID", [fifteenBytes]));

        Assert.That(() => Convert(entry), Throws.Nothing);

        var user = Convert(entry).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(user.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value, e.Reason)),
                Is.EqualTo(new[] { ("objectGUID", (string?)"0102030405060708090A0B0C0D0E0F", NotAGuid) }));
            Assert.That(AttributeNames(user), Has.None.EqualTo("objectGUID").IgnoreCase);
            Assert.That(AttributeNames(user), Has.Some.EqualTo("cn").IgnoreCase);
        }
    }

    [Test]
    public void ConvertEntries_GuidValueOfSixteenBytes_IsRead()
    {
        var guid = Guid.NewGuid();
        var entry = LdapTestResponses.EntryWithRawValues(UserDn,
            ("objectClass", ["top", "user"]), ("cn", ["Alex Smith"]), ("objectGUID", [guid.ToByteArray()]));

        var user = Convert(entry).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(user.AttributeValueErrors, Is.Null);
            Assert.That(Attribute(user, "objectGUID").GuidValues, Is.EqualTo(new[] { guid }));
        }
    }

    [Test]
    public void ConvertEntries_EntryWithTwoUnreadableValues_ReportsBothAndImportsEveryOtherAttribute()
    {
        var imported = ConvertUser(("uidNumber", ["x"]), ("isCriticalSystemObject", ["maybe"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
            Assert.That(imported.AttributeValueErrors?.Select(e => (e.AttributeName, e.Value)),
                Is.EquivalentTo(new[] { ("uidNumber", (string?)"x"), ("isCriticalSystemObject", (string?)"maybe") }));
            Assert.That(AttributeNames(imported).Select(n => n.ToLowerInvariant()), Is.EquivalentTo(new[] { "objectclass", "cn" }));
        }
    }

    [Test]
    public void ConvertEntries_EntryWithAnUnreadableValue_GivesItsDistinguishedNameAsItsSourceLocation()
    {
        // The External ID (an objectGUID or entryUUID) means nothing to an administrator; the DN says where to look.
        const string cleanDn = "CN=Sam Jones,OU=Users,DC=corp,DC=local";
        var imported = Convert(
            UserEntry(UserDn, ("uidNumber", ["x"])),
            UserEntry(cleanDn, ("uidNumber", ["7"])));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported[0].SourceLocation, Is.EqualTo(UserDn));
            Assert.That(imported[1].SourceLocation, Is.Null, "An entry that read cleanly needs no location.");
            Assert.That(imported[1].AttributeValueErrors, Is.Null);
        }
    }

    [Test]
    public void ConvertEntries_UnreadableValueThenAttributeMissingFromSchema_ReportsTheConfigurationError()
    {
        // The entry's attributes are visited in no fixed order, so each of twenty entries carries an unreadable value
        // and an attribute the schema lacks under names of its own: in almost every run some entry has its unreadable
        // value visited first, which is the case at issue. Left in place, the unreadable values would stand in for the
        // configuration error when JIM words the object's message.
        var userType = UserObjectType();
        var entries = new List<SearchResultEntry>();
        for (var i = 0; i < 20; i++)
        {
            userType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 100 + i, Name = $"counter{i}", Type = AttributeDataType.Number, Selected = true, ConnectedSystemObjectType = userType });
            entries.Add(UserEntry($"CN=User {i},OU=Users,DC=corp,DC=local", ($"counter{i}", ["x"]), ($"notInSchema{i}", ["1"])));
        }

        var imported = Convert(userType, entries.ToArray());

        Assert.That(imported, Has.Count.EqualTo(20));
        using (Assert.EnterMultipleScope())
        {
            foreach (var importObject in imported)
            {
                Assert.That(importObject.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.ConfigurationError));
                Assert.That(importObject.AttributeValueErrors, Is.Null);
            }
        }
    }

    [Test]
    public void ConvertEntries_EntriesWithUnreadableValues_LogsOneWarningCountingThemPerAttributeWithoutTheValues()
    {
        // One warning per call rather than per object: a schema mapping gap would otherwise log once for every object
        // in a large directory on every import. The values themselves can be personal data; the execution items hold them.
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        Convert(UserObjectType(), logger,
            UserEntry(UserDn, ("uidNumber", ["12a"]), ("isCriticalSystemObject", ["maybe"])),
            UserEntry("CN=Sam Jones,OU=Users,DC=corp,DC=local", ("uidNumber", ["34b"])),
            UserEntry("CN=Jo Brown,OU=Users,DC=corp,DC=local", ("uidNumber", ["56"])));

        var warnings = sink.Events.Where(e => e.Level == LogEventLevel.Warning).Select(e => e.RenderMessage()).ToList();
        Assert.That(warnings, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warnings[0], Does.Contain("uidNumber: 2"));
            Assert.That(warnings[0], Does.Contain("isCriticalSystemObject: 1"));
            Assert.That(sink.Events.Select(e => e.RenderMessage()).Where(m => m.Contains("12a") || m.Contains("34b") || m.Contains("maybe")), Is.Empty,
                "No log line carries a value.");
        }
    }

    [Test]
    public void ConvertEntries_EntriesThatReadCleanly_LogNoWarning()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        Convert(UserObjectType(), logger, UserEntry(UserDn, ("uidNumber", ["56"])));

        Assert.That(sink.Events.Where(e => e.Level >= LogEventLevel.Warning), Is.Empty);
    }

    private static ConnectedSystemImportObject ConvertUser(params (string Name, string[] Values)[] attributes) =>
        Convert(UserEntry(UserDn, attributes)).Single();

    private static SearchResultEntry UserEntry(string distinguishedName, params (string Name, string[] Values)[] attributes)
    {
        (string Name, string[] Values)[] all = [("objectClass", ["top", "user"]), ("cn", ["Alex Smith"]), .. attributes];
        return LdapTestResponses.EntryWithValues(distinguishedName, all);
    }

    private static List<ConnectedSystemImportObject> Convert(params SearchResultEntry[] entries) =>
        Convert(UserObjectType(), entries);

    private static List<ConnectedSystemImportObject> Convert(ConnectedSystemObjectType userType, params SearchResultEntry[] entries) =>
        Convert(userType, Logger.None, entries);

    private static List<ConnectedSystemImportObject> Convert(ConnectedSystemObjectType userType, ILogger logger, params SearchResultEntry[] entries)
    {
        var connectedSystem = new ConnectedSystem
        {
            Name = "Active Directory",
            ObjectTypes = [userType],
            Partitions = [new ConnectedSystemPartition { ExternalId = "DC=corp,DC=local", Name = "corp", Selected = true, Containers = [] }]
        };
        var import = new LdapConnectorImport(connectedSystem, new ConnectedSystemRunProfile { Name = "Full Import", RunType = ConnectedSystemRunType.FullImport },
            new LdapConnection("localhost"), null, 1, [], null, null, "dc1.corp.local", _ => true, logger, CancellationToken.None, new RecordingConnectorProgress());

        var response = LdapTestResponses.SearchResponseWithEntries(entries);
        return ((ILdapDeltaImportHost)import).ConvertEntries(response.Entries, ObjectChangeType.NotSet, userType).ToList();
    }

    private static ConnectedSystemObjectType UserObjectType()
    {
        var userType = new ConnectedSystemObjectType { Id = 1, Name = "user", Selected = true };
        var id = 1;
        void Add(string name, AttributeDataType type, AttributePlurality plurality = AttributePlurality.SingleValued, bool isExternalId = false) =>
            userType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Id = id++, Name = name, Type = type, AttributePlurality = plurality, Selected = true, IsExternalId = isExternalId, ConnectedSystemObjectType = userType
            });

        Add("objectClass", AttributeDataType.Text, AttributePlurality.MultiValued);
        Add("cn", AttributeDataType.Text);
        Add("objectGUID", AttributeDataType.Guid, isExternalId: true);
        Add("uidNumber", AttributeDataType.Number);
        Add("badgeNumbers", AttributeDataType.Number, AttributePlurality.MultiValued);
        Add("pwdLastSet", AttributeDataType.LongNumber);
        Add("accountExpires", AttributeDataType.LongNumber);
        Add("isCriticalSystemObject", AttributeDataType.Boolean);
        Add("whenChanged", AttributeDataType.DateTime);
        return userType;
    }

    private static IEnumerable<string> AttributeNames(ConnectedSystemImportObject importObject) =>
        importObject.Attributes.Select(a => a.Name);

    private static ConnectedSystemImportObjectAttribute Attribute(ConnectedSystemImportObject importObject, string name) =>
        importObject.Attributes.Single(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private sealed class CapturingSink : ILogEventSink
    {
        internal List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
