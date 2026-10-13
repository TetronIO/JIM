// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors.ActiveDirectory;

/// <summary>
/// Schema discovery against a stock Windows Server forest (#1853).
/// </summary>
/// <remarks>
/// A stock forest publishes well over 1,000 non-defunct <c>attributeSchema</c> entries, and Active Directory caps an
/// unpaged search at its MaxPageSize (1,000 by default) with sizeLimitExceeded. Samba AD applies no such cap, which
/// is why the integration lab never saw the connector's unpaged schema fetch fail.
/// </remarks>
[TestFixture]
[Category(ActiveDirectoryLab.Category)]
public class ActiveDirectorySchemaProbeTests
{
    private Serilog.Core.Logger _logger = null!;

    [SetUp]
    public void SetUp() => _logger = new LoggerConfiguration().CreateLogger();

    [TearDown]
    public void TearDown() => _logger.Dispose();

    [Test]
    public async Task GetSchemaAsync_StockForest_DiscoversTheUserClassWithItsAttributesAsync()
    {
        var lab = ActiveDirectoryLab.Require();
        using var connector = ActiveDirectoryLab.NewConnector(lab);

        ConnectorSchema? schema = null;
        Assert.That(
            async () => schema = await connector.GetSchemaAsync(ActiveDirectoryLab.ConnectorSettings(lab), _logger),
            Throws.Nothing,
            "Schema discovery must read every attributeSchema entry under the directory's MaxPageSize, which needs the paged-results control.");

        var user = schema!.ObjectTypes.SingleOrDefault(objectType => objectType.Name.Equals("user", StringComparison.OrdinalIgnoreCase));
        Assert.That(user, Is.Not.Null, "The user class was not discovered.");

        var attributeNames = user!.Attributes.Select(attribute => attribute.Name).ToList();
        TestContext.Out.WriteLine($"user: {attributeNames.Count} attributes across {schema.ObjectTypes.Count} object types");
        Assert.Multiple(() =>
        {
            // A real user class carries several hundred attributes once its parents and auxiliary classes are
            // walked; a count this low means the walk stopped short.
            Assert.That(attributeNames, Has.Count.GreaterThan(300));
            Assert.That(attributeNames, Does.Contain("sAMAccountName").IgnoreCase);
            Assert.That(attributeNames, Does.Contain("userAccountControl").IgnoreCase);
            Assert.That(attributeNames, Does.Contain("unicodePwd").IgnoreCase);
        });
    }

    [Test]
    public async Task GetSchemaAsync_StockForest_DeclaresCaseExactExactlyTheCaseSensitiveStringSyntaxesAsync()
    {
        // #2051: discovery reads case-sensitivity from oMSyntax (27, 22 and 19). The documented meaning is the
        // attributeSyntax beside it: 2.5.5.3 String(Case Sensitive), and 2.5.5.5 String(IA5) and String(Printable).
        // This checks the mapping against the directory's own schema rather than against a table written from the
        // documentation, so an attribute the mapping misreads names itself.
        var lab = ActiveDirectoryLab.Require();
        using var connector = ActiveDirectoryLab.NewConnector(lab);
        var schema = await connector.GetSchemaAsync(ActiveDirectoryLab.ConnectorSettings(lab), _logger);
        var user = schema.ObjectTypes.Single(objectType => objectType.Name.Equals("user", StringComparison.OrdinalIgnoreCase));

        using var connection = ActiveDirectoryLab.OpenAdminConnection(lab, _logger);
        var syntaxByName = ReadAttributeSyntaxes(connection);

        var textAttributes = user.Attributes.Where(attribute => attribute.Type == AttributeDataType.Text && syntaxByName.ContainsKey(attribute.Name)).ToList();
        var misread = textAttributes
            .Where(attribute => attribute.CaseExact != syntaxByName[attribute.Name] is "2.5.5.3" or "2.5.5.5")
            .Select(attribute => $"{attribute.Name} ({syntaxByName[attribute.Name]}): declared {(attribute.CaseExact ? "case-exact" : "ignoring case")}")
            .ToList();

        TestContext.Out.WriteLine($"user: {textAttributes.Count} Text attributes, {textAttributes.Count(attribute => attribute.CaseExact)} case-exact: " +
            string.Join(", ", textAttributes.Where(attribute => attribute.CaseExact).Select(attribute => attribute.Name)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(misread, Is.Empty);
            Assert.That(textAttributes.Single(attribute => attribute.Name.Equals("sAMAccountName", StringComparison.OrdinalIgnoreCase)).CaseExact, Is.False,
                "An account name ignores case in Active Directory.");
        }
    }

    /// <summary>
    /// Every attribute's attributeSyntax, read straight from the schema partition, independently of the connector.
    /// </summary>
    private static Dictionary<string, string> ReadAttributeSyntaxes(LdapConnection connection)
    {
        var rootDse = (SearchResponse)connection.SendRequest(new SearchRequest(string.Empty, "(objectClass=*)", SearchScope.Base, "schemaNamingContext"));
        var schemaNamingContext = (string)rootDse.Entries[0].Attributes["schemaNamingContext"][0];

        var syntaxes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[]? cookie = null;
        do
        {
            var request = new SearchRequest(schemaNamingContext, "(objectClass=attributeSchema)", SearchScope.OneLevel, "lDAPDisplayName", "attributeSyntax");
            var paging = new PageResultRequestControl(1000);
            if (cookie is { Length: > 0 })
                paging.Cookie = cookie;
            request.Controls.Add(paging);

            var response = (SearchResponse)connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
                syntaxes[(string)entry.Attributes["lDAPDisplayName"][0]] = (string)entry.Attributes["attributeSyntax"][0];

            cookie = response.Controls.OfType<PageResultResponseControl>().SingleOrDefault()?.Cookie;
        } while (cookie is { Length: > 0 });

        return syntaxes;
    }
}
