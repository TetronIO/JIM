// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using System.Net;
using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Reads a real directory's own subschema and checks that the classes it keeps for itself are the ones JIM marks
/// internal, and that nothing an administrator manages is caught by the same rule.
/// </summary>
/// <remarks>
/// The unit fixture beside this one pins the rule against OIDs written into the test. That proves the rule does what
/// it says; it cannot prove the OIDs are the ones a directory actually publishes, which is the assumption the whole
/// feature rests on and the only part that can silently stop being true after a directory upgrade.
/// <para>
/// Opt-in via the JIM_TEST_LDAP_HOST environment variable, mirroring the other live-directory fixtures; ignored
/// otherwise, and never part of the default unit tier. Point it at the repository's own OpenLDAP fixture
/// (<c>test/integration/docker/openldap</c>), which enables the accesslog overlay and so publishes the audit
/// classes this asserts on.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresDirectory")]
public class LdapObjectTypeVisibilityDirectoryTests
{
    private Dictionary<string, Rfc4512ObjectClassDescription> _objectClasses = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var host = Environment.GetEnvironmentVariable("JIM_TEST_LDAP_HOST");
        if (string.IsNullOrEmpty(host))
            Assert.Ignore("JIM_TEST_LDAP_HOST not set; skipping live-directory object type visibility tests.");

        var port = int.Parse(Environment.GetEnvironmentVariable("JIM_TEST_LDAP_PORT") ?? "389");
        var bindDn = Environment.GetEnvironmentVariable("JIM_TEST_LDAP_BINDDN") ?? "cn=admin,dc=jim,dc=test";
        var password = Environment.GetEnvironmentVariable("JIM_TEST_LDAP_PASSWORD") ?? "Test@123!";

        using var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindDn, password)
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.Bind();

        var request = new SearchRequest("cn=Subschema", "(objectClass=*)", SearchScope.Base, "objectClasses");
        var response = (SearchResponse)connection.SendRequest(request);

        _objectClasses = response.Entries[0].Attributes["objectClasses"]
            .GetValues(typeof(string))
            .Cast<string>()
            .Select(Rfc4512SchemaParser.ParseObjectClassDescription)
            .Where(definition => definition?.Name != null)
            .GroupBy(definition => definition!.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()!, StringComparer.OrdinalIgnoreCase);

        Assert.That(_objectClasses, Is.Not.Empty, "The directory published no object classes, so nothing here would be meaningful.");
    }

    // OpenLDAP's cn=config backend and its accesslog overlay. Neither has anything to do with the directory an
    // administrator manages, and between them they are the bulk of what a stock instance publishes.
    [TestCase("olcGlobal")]
    [TestCase("olcDatabaseConfig")]
    [TestCase("olcSchemaConfig")]
    [TestCase("olcMdbConfig")]
    [TestCase("auditAdd")]
    [TestCase("auditModify")]
    [TestCase("auditSearch")]
    public void ADirectorysOwnClass_IsClassifiedInternal(string className)
    {
        AssertClassifiedInternal(className, expectedInternal: true);
    }

    // The classes identity management is actually about. Any of these being hidden would be a serious regression.
    [TestCase("inetOrgPerson")]
    [TestCase("person")]
    [TestCase("organizationalPerson")]
    [TestCase("groupOfNames")]
    [TestCase("groupOfUniqueNames")]
    [TestCase("organizationalUnit")]
    [TestCase("posixAccount")]
    [TestCase("posixGroup")]
    public void AClassAnAdministratorManages_IsNotClassifiedInternal(string className)
    {
        AssertClassifiedInternal(className, expectedInternal: false);
    }

    [Test]
    public void EveryClassTheDirectoryPublishesUnderItsOwnArc_IsClassifiedInternal()
    {
        // Catches an OpenLDAP upgrade adding classes under its arc that the named cases above would not notice.
        var missed = _objectClasses.Values
            .Where(definition => definition.Oid != null && definition.Oid.StartsWith("1.3.6.1.4.1.4203.", StringComparison.Ordinal))
            .Where(definition => LdapObjectTypeClassification.FromRfc4512Definition(definition.Oid, definition.IsObsolete) == null)
            .Select(definition => $"{definition.Name} ({definition.Oid})")
            .ToList();

        Assert.That(missed, Is.Empty, "These classes sit under the directory vendor's own arc but were not classified internal.");
    }

    [Test]
    public void NoClassFromAStandardsArc_IsClassifiedInternal()
    {
        // The mirror of the case above, and the one that matters more: the X.500 (2.5), COSINE (0.9.2342) and
        // Internet (1.3.6.1.1) arcs carry the classes identity management is built on. Hiding one would be a defect
        // an administrator experiences as a class the directory has and JIM does not.
        string[] standardsArcs = ["2.5.", "0.9.2342.", "1.3.6.1.1."];

        var wronglyHidden = _objectClasses.Values
            .Where(definition => definition.Oid != null && standardsArcs.Any(arc => definition.Oid.StartsWith(arc, StringComparison.Ordinal)))
            .Where(definition => LdapObjectTypeClassification.FromRfc4512Definition(definition.Oid, definition.IsObsolete) != null)
            .Select(definition => $"{definition.Name} ({definition.Oid})")
            .ToList();

        // A class a standards body has itself marked OBSOLETE is a legitimate exception, so report the OIDs rather
        // than the count; a failure here should say which class and let a human judge it.
        Assert.That(wronglyHidden.Where(entry => !IsObsolete(entry)), Is.Empty,
            "These classes come from a standards arc but were classified internal.");
    }

    private bool IsObsolete(string entry)
    {
        var name = entry[..entry.IndexOf(" (", StringComparison.Ordinal)];
        return _objectClasses.TryGetValue(name, out var definition) && definition.IsObsolete;
    }

    private void AssertClassifiedInternal(string className, bool expectedInternal)
    {
        Assert.That(_objectClasses.ContainsKey(className), Is.True,
            $"The directory does not publish '{className}', so this fixture is pointed at something other than the OpenLDAP fixture it expects.");

        var definition = _objectClasses[className];
        var tag = LdapObjectTypeClassification.FromRfc4512Definition(definition.Oid, definition.IsObsolete);

        if (expectedInternal)
        {
            Assert.That(tag, Is.Not.Null, $"'{className}' ({definition.Oid}) belongs to the directory itself and must be classified internal.");
            Assert.Multiple(() =>
            {
                Assert.That(tag!.Key, Is.EqualTo(ObjectTypeTags.Keys.Visibility));
                Assert.That(tag!.Value, Is.EqualTo(ObjectTypeTags.Values.VisibilityInternal));
            });
        }
        else
        {
            Assert.That(tag, Is.Null, $"'{className}' ({definition.Oid}) is a class an administrator manages and must never be hidden.");
        }
    }
}
