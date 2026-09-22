// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using System.Net;
using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Reads a real 389 Directory Server's subschema and checks that the classes it keeps for itself are the ones JIM
/// marks internal, and that neither <c>inetOrgPerson</c> nor the classes 389 recommends for users is caught by the
/// same rule.
/// </summary>
/// <remarks>
/// The unit fixture pins the rule against OIDs written into the test. That proves the rule does what it says; it
/// cannot prove the OIDs are the ones a directory actually publishes, which is the assumption the whole feature
/// rests on and the only part that can silently stop being true after a directory upgrade. 389 raises the stakes
/// over OpenLDAP because its internals are matched by an exact list rather than an arc: a class a new 389 release
/// adds under the Netscape arc is neither hidden nor named, and the arc-wide case below exists to make that a test
/// failure that names the class, so a human judges it, rather than noise nobody notices.
/// <para>
/// Opt-in via the JIM_TEST_DIRSRV_HOST environment variable, mirroring the OpenLDAP fixture beside this one;
/// ignored otherwise, and never part of the default unit tier. Point it at a stock 389 Directory Server (the
/// repository's own lab image under <c>test/integration/docker/dirsrv</c> serves); the subschema entry on 389 is
/// <c>cn=schema</c>.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresDirectory")]
public class LdapObjectTypeVisibility389DirectoryTests
{
    private const string NetscapeArc = "2.16.840.1.113730.3.2.";

    private Dictionary<string, Rfc4512ObjectClassDescription> _objectClasses = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var host = Environment.GetEnvironmentVariable("JIM_TEST_DIRSRV_HOST");
        if (string.IsNullOrEmpty(host))
            Assert.Ignore("JIM_TEST_DIRSRV_HOST not set; skipping live 389 Directory Server object type visibility tests.");

        var port = int.Parse(Environment.GetEnvironmentVariable("JIM_TEST_DIRSRV_PORT") ?? "3389");
        var bindDn = Environment.GetEnvironmentVariable("JIM_TEST_DIRSRV_BINDDN") ?? "cn=Directory Manager";
        var password = Environment.GetEnvironmentVariable("JIM_TEST_DIRSRV_PASSWORD") ?? "Test@123!";

        using var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindDn, password)
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.Bind();

        var request = new SearchRequest("cn=schema", "(objectClass=*)", SearchScope.Base, "objectClasses");
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

    // 389's cn=config and plug-in configuration, replication, Class of Service and role definitions, operational
    // entries, in-tree password policy and the Netscape console classes. None describes anything an administrator
    // manages through JIM, and between them they are the bulk of what a stock instance publishes.
    [TestCase("nsslapdConfig")]
    [TestCase("nsslapdPlugin")]
    [TestCase("nsIndex")]
    [TestCase("nsBackendInstance")]
    [TestCase("nsDS5Replica")]
    [TestCase("cosPointerDefinition")]
    [TestCase("nsRoleDefinition")]
    [TestCase("passwordPolicy")]
    [TestCase("nsTombstone")]
    [TestCase("nsContainer")]
    [TestCase("nsAdminConfig")]
    public void ADirectorysOwnClass_IsClassifiedInternal(string className)
    {
        AssertClassifiedInternal(className, expectedInternal: true);
    }

    // The classes identity management is actually about, including the ones 389 itself recommends for users
    // (nsPerson, nsAccount, nsOrgPerson, nsMemberOf), which share the Netscape arc with the server's own classes.
    // Any of these being hidden would be a serious regression.
    [TestCase("inetOrgPerson")]
    [TestCase("person")]
    [TestCase("organizationalPerson")]
    [TestCase("groupOfNames")]
    [TestCase("groupOfUniqueNames")]
    [TestCase("organizationalUnit")]
    [TestCase("posixAccount")]
    [TestCase("posixGroup")]
    [TestCase("nsPerson")]
    [TestCase("nsAccount")]
    [TestCase("nsOrgPerson")]
    [TestCase("nsMemberOf")]
    [TestCase("groupOfURLs")]
    [TestCase("mailRecipient")]
    public void AClassAnAdministratorManages_IsNotClassifiedInternal(string className)
    {
        AssertClassifiedInternal(className, expectedInternal: false);
    }

    [Test]
    public void EveryStructuralClassUnderTheNetscapeArc_IsEitherListedInternalOrKnownManageable()
    {
        // The Netscape arc mixes 389's own classes with the ones an administrator manages, and JIM matches the
        // former by an exact list. A 389 upgrade that adds a structural class under the arc therefore lands in
        // neither set; this case turns that into a failure that names the class, so it is judged rather than left
        // as noise on the Schema tab (or, worse, "fixed" by widening the match to the arc).
        HashSet<string> knownManageable = new(StringComparer.OrdinalIgnoreCase)
        {
            "inetOrgPerson",
            "nsPerson",
            "groupOfURLs",
            "groupOfCertificates",
            "ntUser",
            "ntGroup",
            "nsLicenseUser"
        };

        var unjudged = _objectClasses.Values
            .Where(definition => definition.Kind == Rfc4512ObjectClassKind.Structural)
            .Where(definition => definition.Oid != null && definition.Oid.StartsWith(NetscapeArc, StringComparison.Ordinal))
            .Where(definition => !knownManageable.Contains(definition.Name!))
            .Where(definition => LdapObjectTypeClassification.FromRfc4512Definition(definition.Oid, definition.IsObsolete) == null)
            .Select(definition => $"{definition.Name} ({definition.Oid})")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        Assert.That(unjudged, Is.Empty,
            "These structural classes sit under the Netscape arc but are neither in the internal class list nor in this fixture's known-manageable set. Judge each one: add it to LdapObjectTypeClassification.InternalClassOids if its entries configure or record the server's own behaviour, or to the known-manageable set here if it describes an identity, a group or a resource.");
    }

    [Test]
    public void NoClassFromAStandardsArc_IsClassifiedInternal()
    {
        // The mirror of the cases above, and the one that matters more: the X.500 (2.5), COSINE (0.9.2342) and
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
            $"The directory does not publish '{className}', so this fixture is pointed at something other than the 389 Directory Server it expects.");

        var definition = _objectClasses[className];
        var tag = LdapObjectTypeClassification.FromRfc4512Definition(definition.Oid, definition.IsObsolete);

        if (expectedInternal)
        {
            Assert.That(tag, Is.Not.Null, $"'{className}' ({definition.Oid}) belongs to the directory itself and must be classified internal.");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tag!.Key, Is.EqualTo(ObjectTypeTags.Keys.Visibility));
                Assert.That(tag!.Value, Is.EqualTo(ObjectTypeTags.Values.VisibilityInternal));
            }
        }
        else
        {
            Assert.That(tag, Is.Null, $"'{className}' ({definition.Oid}) is a class an administrator manages and must never be hidden.");
        }
    }
}
