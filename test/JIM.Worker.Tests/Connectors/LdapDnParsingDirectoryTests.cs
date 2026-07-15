// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;
using System.DirectoryServices.Protocols;
using System.Net;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Integration tests that exercise the LDAP Connector's Distinguished Name handling against a live OpenLDAP
/// directory, using the same System.DirectoryServices.Protocols library the connector uses. They seed a nested
/// container tree (including entries whose names contain a comma, which the directory stores using RFC 4514
/// hex-pair escaping, for example "\2C"), read the DNs back exactly as the server returns them, and feed those
/// real DNs through the connector's DN-handling code paths. This proves the in-house DN parser that replaced the
/// former third-party dependency behaves correctly on directory-canonical DN formatting.
///
/// Opt-in via the JIM_TEST_LDAP_HOST environment variable (the fixture reads JIM_TEST_LDAP_* for connection
/// details); ignored otherwise, mirroring the RequiresPostgres fixtures. Never runs in the default unit tier.
/// </summary>
[TestFixture]
[Category("RequiresDirectory")]
public class LdapDnParsingDirectoryTests
{
    private LdapConnection _connection = null!;
    private string _baseDn = null!;
    private string _rootDn = null!;

    // Real DNs exactly as returned by the directory (populated in OneTimeSetUp).
    private string _corpDn = null!;
    private string _usersDn = null!;
    private string _salesDn = null!;
    private string _smithDn = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var host = Environment.GetEnvironmentVariable("JIM_TEST_LDAP_HOST");
        if (string.IsNullOrEmpty(host))
            Assert.Ignore("JIM_TEST_LDAP_HOST not set; skipping live-directory LDAP DN parsing tests.");

        var port = int.Parse(Environment.GetEnvironmentVariable("JIM_TEST_LDAP_PORT") ?? "389");
        var bindDn = Environment.GetEnvironmentVariable("JIM_TEST_LDAP_BINDDN") ?? "cn=admin,dc=jim,dc=test";
        var password = Environment.GetEnvironmentVariable("JIM_TEST_LDAP_PASSWORD") ?? "Test@123!";
        _baseDn = Environment.GetEnvironmentVariable("JIM_TEST_LDAP_BASEDN") ?? "dc=jim,dc=test";

        _connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindDn, password)
        };
        _connection.SessionOptions.ProtocolVersion = 3;
        _connection.Bind();

        // Isolate all test data under a dedicated container and rebuild it from scratch for reproducibility.
        _rootDn = $"ou=DnTest,{_baseDn}";
        TreeDelete(_rootDn);

        AddOrganisationalUnit(_rootDn, "DnTest");
        _corpDn = $"ou=Corp,{_rootDn}";
        AddOrganisationalUnit(_corpDn, "Corp");
        _usersDn = $"ou=Users,{_corpDn}";
        AddOrganisationalUnit(_usersDn, "Users");

        // An OU whose name contains a comma: the directory returns it hex-escaped ("ou=Sales\2C Inc,...").
        var salesInputDn = $@"ou=Sales\, Inc,{_corpDn}";
        AddOrganisationalUnit(salesInputDn, "Sales, Inc");

        // A person whose RDN value contains a comma, under the two-level OU path.
        var smithInputDn = $@"cn=Smith\, John,{_usersDn}";
        AddPerson(smithInputDn, "Smith, John", "Smith");

        // Read every entry back and capture the DN strings exactly as the directory returns them.
        var returned = SearchSubtreeDns(_rootDn, "(|(objectClass=organizationalUnit)(objectClass=inetOrgPerson))");
        _corpDn = MatchByLeaf(returned, "ou=Corp,");
        _usersDn = MatchByLeaf(returned, "ou=Users,");
        _salesDn = MatchByLeaf(returned, "ou=Sales");
        _smithDn = MatchByLeaf(returned, "cn=Smith");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_connection == null)
            return;

        TreeDelete(_rootDn);
        _connection.Dispose();
    }

    [Test]
    public void GetContainerDisplayName_CommaInName_ReturnsUnescapedValue()
    {
        // The directory returns "ou=Sales\2C Inc,..."; the display name must be the human-readable, unescaped value.
        var connector = new LdapConnector();

        Assert.That(connector.GetContainerDisplayName(_salesDn), Is.EqualTo("Sales, Inc"));
    }

    [Test]
    public void GetParentContainerExternalId_NestedOu_ReturnsCorrectParent()
    {
        var connector = new LdapConnector();

        Assert.That(connector.GetParentContainerExternalId(_usersDn), Is.EqualTo(_corpDn).IgnoreCase);
        Assert.That(connector.GetParentContainerExternalId(_salesDn), Is.EqualTo(_corpDn).IgnoreCase);
    }

    [Test]
    public void GetParentContainerExternalId_TopLevelUnderRoot_ReturnsRoot()
    {
        var connector = new LdapConnector();

        Assert.That(connector.GetParentContainerExternalId(_corpDn), Is.EqualTo(_rootDn).IgnoreCase);
    }

    [Test]
    public void ParseDistinguishedName_PersonWithCommaInRdn_SplitsCorrectly()
    {
        // The leaf RDN's comma is hex-escaped by the directory, so it must not be treated as a separator.
        var (rdn, parentDn) = LdapConnectorUtilities_ParseDistinguishedName(_smithDn);

        Assert.That(rdn, Does.StartWith("cn=Smith").IgnoreCase);
        Assert.That(rdn, Does.Not.Contain(",")); // the only comma in the RDN is escaped, so none remains literal
        Assert.That(parentDn, Is.EqualTo(_usersDn).IgnoreCase);
    }

    [Test]
    public void HasValidRdnValues_RealDirectoryDns_AllValid()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LdapConnectorUtilities_HasValidRdnValues(_corpDn), Is.True);
            Assert.That(LdapConnectorUtilities_HasValidRdnValues(_usersDn), Is.True);
            Assert.That(LdapConnectorUtilities_HasValidRdnValues(_salesDn), Is.True);
            Assert.That(LdapConnectorUtilities_HasValidRdnValues(_smithDn), Is.True);
        });
    }

    [Test]
    public void BuildContainerHierarchy_FromRealOuDns_LinksParentAndChildren()
    {
        var entries = new List<LdapConnectorPartitions.ContainerEntry>
        {
            new(_corpDn, "Corp"),
            new(_usersDn, "Users"),
            new(_salesDn, "Sales, Inc")
        };

        var hierarchy = LdapConnectorPartitions.BuildContainerHierarchy(entries, _rootDn);

        Assert.That(hierarchy, Has.Count.EqualTo(1), "Corp should be the only top-level container under the root.");
        var corp = hierarchy[0];
        Assert.That(corp.Name, Is.EqualTo("Corp"));
        Assert.That(corp.ChildContainers.Select(c => c.Name), Is.EquivalentTo(new[] { "Users", "Sales, Inc" }));
    }

    // Thin wrappers so the internal utility methods read clearly in the assertions above.
    private static (string? Rdn, string? ParentDn) LdapConnectorUtilities_ParseDistinguishedName(string dn) =>
        LdapConnectorUtilities.ParseDistinguishedName(dn);

    private static bool LdapConnectorUtilities_HasValidRdnValues(string dn) =>
        LdapConnectorUtilities.HasValidRdnValues(dn);

    private void AddOrganisationalUnit(string dn, string ou)
    {
        var request = new AddRequest(dn,
            new DirectoryAttribute("objectClass", "organizationalUnit"),
            new DirectoryAttribute("ou", ou));
        _connection.SendRequest(request);
    }

    private void AddPerson(string dn, string cn, string sn)
    {
        var request = new AddRequest(dn,
            new DirectoryAttribute("objectClass", "inetOrgPerson"),
            new DirectoryAttribute("cn", cn),
            new DirectoryAttribute("sn", sn));
        _connection.SendRequest(request);
    }

    private List<string> SearchSubtreeDns(string baseDn, string filter)
    {
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, "dn");
        var response = (SearchResponse)_connection.SendRequest(request);
        return response.Entries.Cast<SearchResultEntry>().Select(e => e.DistinguishedName).ToList();
    }

    private static string MatchByLeaf(List<string> dns, string leafPrefix)
    {
        var match = dns.FirstOrDefault(d => d.StartsWith(leafPrefix, StringComparison.OrdinalIgnoreCase));
        Assert.That(match, Is.Not.Null, $"No returned DN started with '{leafPrefix}'.");
        return match!;
    }

    private void TreeDelete(string dn)
    {
        var request = new DeleteRequest(dn);
        request.Controls.Add(new TreeDeleteControl());
        try
        {
            _connection.SendRequest(request);
        }
        catch (DirectoryOperationException)
        {
            // The subtree does not exist yet (first run or already cleaned up); nothing to delete.
        }
    }
}
