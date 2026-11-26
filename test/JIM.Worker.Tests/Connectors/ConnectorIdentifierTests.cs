// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Connectors;
using JIM.Models.Interfaces;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Every Connector carries a stable identifier beside its display name (#3103). The identifier names the Connector's
/// message catalogue (<c>connector:jim.ldap/Messages</c>) and, once Connectors can be installed, its package, so it must
/// be well formed, unique, and never change; the display name stays free to.
/// </summary>
[TestFixture]
public class ConnectorIdentifierTests
{
    private static readonly Regex IdentifierPattern = new(@"^[a-z0-9]+(\.[a-z0-9-]+)+$", RegexOptions.CultureInvariant);

    private static IEnumerable<Type> ConnectorTypes() => typeof(ConnectorConstants).Assembly.GetTypes()
        .Where(type => typeof(IConnector).IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false });

    private static IConnector Create(Type type) => (IConnector)Activator.CreateInstance(type)!;

    [Test]
    public void EveryConnector_HasAWellFormedIdentifier()
    {
        var connectors = ConnectorTypes().ToList();
        Assert.That(connectors, Is.Not.Empty);

        using (Assert.EnterMultipleScope())
        {
            foreach (var type in connectors)
                Assert.That(Create(type).Identifier, Does.Match(IdentifierPattern), type.Name);
        }
    }

    [Test]
    public void EveryConnector_HasItsOwnIdentifier()
    {
        var identifiers = ConnectorTypes().Select(type => Create(type).Identifier).ToList();

        Assert.That(identifiers, Is.Unique);
    }

    [TestCase("JIM LDAP Connector", "jim.ldap")]
    [TestCase("JIM File Connector", "jim.file")]
    [TestCase("JIM SCIM 2.0 Client Connector", "jim.scim.client")]
    [TestCase("JIM SCIM 2.0 Service Provider Connector", "jim.scim.serviceprovider")]
    [TestCase("JIM SQL Connector", "jim.sql")]
    [TestCase("JIM Microsoft Entra ID Connector", "jim.entraid")]
    public void BuiltInConnector_KeepsTheIdentifierItShippedWith(string name, string identifier)
    {
        // Pinned, because a stored message names its catalogue by this identifier: changing one orphans every record
        // the Connector wrote, which would then read in their stored English only.
        var connector = ConnectorTypes().Select(Create).Single(candidate => candidate.Name == name);

        Assert.That(connector.Identifier, Is.EqualTo(identifier));
    }
}
