// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Text;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How a deleted entry's identity is read back out of the LDIF-shaped lines a change log keeps of it
/// (<see cref="LdapDeletedEntryIdentity"/>): OpenLDAP's accesslog <c>reqOld</c> values and 389 Directory Server's
/// Retro Changelog <c>changes</c> attribute both describe the deleted entry this way, and the import object built
/// from either has to match the one a live entry would have produced, or the deletion finds nothing to delete.
/// </summary>
[TestFixture]
public class LdapDeletedEntryIdentityTests
{
    private const string DeletedDn = "uid=carol,ou=People,dc=example,dc=com";
    private const string Uuid = "1c0d8f2e-4d1a-4b2b-9c3e-1e2f3a4b5c6d";

    private CapturingSink _log = null!;

    /// <summary>A logger writing to <see cref="_log"/>, built per test so that each test reads only its own events.</summary>
    private ILogger Logger() => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_log).CreateLogger();

    [SetUp]
    public void SetUp() => _log = new CapturingSink();

    [Test]
    public void Identify_PlainLines_ResolvesTheObjectTypeExternalIdAndDn()
    {
        var deleted = LdapDeletedEntryIdentity.Identify(
            ["objectClass: top", "objectClass: person", "objectClass: inetOrgPerson", "uid: carol", $"entryUUID: {Uuid}"],
            DeletedDn, entryUuid: null, ObjectTypes(), Logger());

        Assert.That(deleted, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(deleted.ObjectType, Is.EqualTo("inetOrgPerson"), "resolved from the objectClass values, as a live entry's would be");
            Assert.That(deleted.Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { Uuid }));
            Assert.That(deleted.Attributes.Single(a => a.Name == "distinguishedName").StringValues, Is.EqualTo(new[] { DeletedDn }));
        }
    }

    [Test]
    public void Identify_CommentsAndBlankLines_AreIgnored()
    {
        var deleted = LdapDeletedEntryIdentity.Identify(
            ["# the deleted entry", "", "objectClass: inetOrgPerson", "", $"entryUUID: {Uuid}", "# end"],
            DeletedDn, entryUuid: null, ObjectTypes(), Logger());

        Assert.That(deleted?.ObjectType, Is.EqualTo("inetOrgPerson"));
    }

    [Test]
    public void Identify_FoldedLines_AreUnfoldedBeforeParsing()
    {
        // RFC 2849: a line beginning with a single space continues the previous one.
        var deleted = LdapDeletedEntryIdentity.Identify(
            ["objectClass: inetOrg", " Person", "entryUUID: " + Uuid[..12], " " + Uuid[12..]],
            DeletedDn, entryUuid: null, ObjectTypes(), Logger());

        Assert.That(deleted, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.ObjectType, Is.EqualTo("inetOrgPerson"));
            Assert.That(deleted.Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { Uuid }));
        }
    }

    [Test]
    public void Identify_Base64Value_IsDecoded()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Uuid));

        var deleted = LdapDeletedEntryIdentity.Identify(
            ["objectClass:: " + Convert.ToBase64String(Encoding.UTF8.GetBytes("inetOrgPerson")), "entryUUID:: " + encoded],
            DeletedDn, entryUuid: null, ObjectTypes(), Logger());

        Assert.That(deleted, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.ObjectType, Is.EqualTo("inetOrgPerson"));
            Assert.That(deleted.Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { Uuid }));
        }
    }

    [Test]
    public void Identify_UrlValue_IsIgnored()
    {
        var deleted = LdapDeletedEntryIdentity.Identify(
            ["objectClass: inetOrgPerson", "jpegPhoto:< file:///tmp/carol.jpg", $"entryUUID: {Uuid}"],
            DeletedDn, entryUuid: null, ObjectTypes(), Logger());

        Assert.That(deleted?.ObjectType, Is.EqualTo("inetOrgPerson"), "a URL-valued attribute is neither a class nor an identity, and must not derail the parse");
    }

    [Test]
    public void Identify_AttributeNamesInAnyCase_AreMatched()
    {
        var deleted = LdapDeletedEntryIdentity.Identify(
            ["OBJECTCLASS: inetorgperson", $"EntryUUID: {Uuid}"],
            DeletedDn, entryUuid: null, ObjectTypes(), Logger());

        Assert.That(deleted, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted.ObjectType, Is.EqualTo("inetOrgPerson"), "LDAP attribute and class names are case-insensitive");
            Assert.That(deleted.Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { Uuid }));
        }
    }

    [Test]
    public void Identify_EntryUuidSupplied_TakesPrecedenceOverTheParsedOne()
    {
        const string direct = "9f9f9f9f-0000-4b2b-9c3e-1e2f3a4b5c6d";

        var deleted = LdapDeletedEntryIdentity.Identify(
            ["objectClass: inetOrgPerson", $"entryUUID: {Uuid}"],
            DeletedDn, entryUuid: direct, ObjectTypes(), Logger());

        Assert.That(deleted?.Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { direct }),
            "the accesslog's reqEntryUUID is the directory's own statement and outranks what reqOld says");
    }

    [Test]
    public void Identify_EntryUuidSuppliedAndNoneParsed_UsesTheSuppliedOne()
    {
        var deleted = LdapDeletedEntryIdentity.Identify(["objectClass: inetOrgPerson"], DeletedDn, entryUuid: Uuid, ObjectTypes(), Logger());

        Assert.That(deleted?.Attributes.Single(a => a.Name == "entryUUID").StringValues, Is.EqualTo(new[] { Uuid }));
    }

    [Test]
    public void Identify_NoObjectClass_ReturnsNullWithAWarning()
    {
        var deleted = LdapDeletedEntryIdentity.Identify(["uid: carol", $"entryUUID: {Uuid}"], DeletedDn, entryUuid: null, ObjectTypes(), Logger());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted, Is.Null, "without an objectClass there is no Object Type to record the deletion against");
            Assert.That(_log.Events.Any(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("Could not determine object type for deleted object")), Is.True);
        }
    }

    [Test]
    public void Identify_ObjectClassNotASelectedObjectType_ReturnsNull()
    {
        var deleted = LdapDeletedEntryIdentity.Identify(["objectClass: organizationalUnit", $"entryUUID: {Uuid}"], DeletedDn, entryUuid: null, ObjectTypes(), Logger());

        Assert.That(deleted, Is.Null, "a deletion of something this Connected System does not import is nothing to stage");
    }

    [Test]
    public void Identify_NoEntryUuid_ReturnsNullWithAWarning()
    {
        var deleted = LdapDeletedEntryIdentity.Identify(["objectClass: inetOrgPerson", "uid: carol"], DeletedDn, entryUuid: null, ObjectTypes(), Logger());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted, Is.Null, "without the external id the deletion could not be matched to a Connected System Object");
            Assert.That(_log.Events.Any(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("Could not determine entryUUID for deleted object")), Is.True);
        }
    }

    [Test]
    public void Identify_ObjectTypeWithoutAnExternalIdAttribute_CarriesOnlyTheDn()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "inetOrgPerson", Selected = true };

        var deleted = LdapDeletedEntryIdentity.Identify(["objectClass: inetOrgPerson", $"entryUUID: {Uuid}"], DeletedDn, entryUuid: null, [objectType], Logger());

        Assert.That(deleted?.Attributes.Select(a => a.Name), Is.EqualTo(new[] { "distinguishedName" }));
    }

    [Test]
    public void Parse_NoObjectClassAndNoEntryUuid_IsEmpty()
    {
        var identity = LdapDeletedEntryIdentity.Parse(["# nothing about the entry", "changetype: delete"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(identity.IsEmpty, Is.True, "lines that describe no entry at all are how a change log says it did not record the deleted entry");
            Assert.That(identity.ObjectClasses, Is.Empty);
            Assert.That(identity.EntryUuid, Is.Null);
        }
    }

    [Test]
    public void Parse_ObjectClassOnly_IsNotEmpty() =>
        Assert.That(LdapDeletedEntryIdentity.Parse(["objectClass: inetOrgPerson"]).IsEmpty, Is.False);

    [Test]
    public void Parse_ValueWithSurroundingWhitespace_IsTrimmed() =>
        Assert.That(LdapDeletedEntryIdentity.Parse(["objectClass:   inetOrgPerson  "]).ObjectClasses, Is.EqualTo(new[] { "inetOrgPerson" }));

    [Test]
    public void Parse_LineWithoutASeparator_IsIgnored() =>
        Assert.That(LdapDeletedEntryIdentity.Parse(["not an attribute line", "objectClass: inetOrgPerson"]).ObjectClasses, Is.EqualTo(new[] { "inetOrgPerson" }));

    [Test]
    public void Parse_MalformedBase64Value_IsIgnored() =>
        Assert.That(LdapDeletedEntryIdentity.Parse(["objectClass:: not*base64", "objectClass: inetOrgPerson"]).ObjectClasses, Is.EqualTo(new[] { "inetOrgPerson" }));

    #region Helpers

    private static List<ConnectedSystemObjectType> ObjectTypes()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "inetOrgPerson", Selected = true };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "entryUUID", Type = AttributeDataType.Text, Selected = true, IsExternalId = true });
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "distinguishedName", Type = AttributeDataType.Text, Selected = true });
        return [objectType];
    }

    /// <summary>Collects what the helper logs, so a test can assert on the warning it raises.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        internal List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    #endregion
}
