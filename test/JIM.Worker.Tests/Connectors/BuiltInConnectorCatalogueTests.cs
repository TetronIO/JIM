// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors;
using JIM.Models.Localisation;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Each built-in Connector has a message catalogue of its own, named for its identifier
/// (<c>connector:jim.ldap/Messages</c>), so what it writes reads in an administrator's language the way an installed
/// Connector's will (#3103). The catalogues ship in JIM.Connectors and are served wherever JIM runs.
/// </summary>
[TestFixture]
public class BuiltInConnectorCatalogueTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    private static IEnumerable<string> BuiltInCatalogues() =>
        SeedingServer.BuiltInConnectors().Select(connector => ConnectorCatalogueNames.For(connector.Identifier));

    [Test]
    public void EveryBuiltInConnector_HasACatalogue()
    {
        var source = new BuiltInConnectorCatalogueSource();

        using (Assert.EnterMultipleScope())
        {
            foreach (var catalogue in BuiltInCatalogues())
            {
                Assert.That(source.Serves(catalogue), Is.True, catalogue);
                // Proves the catalogue is embedded: a resource file that never made it into the assembly throws here.
                Assert.That(() => source.Template(catalogue, "NoSuchKey", French), Throws.Nothing, catalogue);
                Assert.That(source.Template(catalogue, "NoSuchKey", French), Is.Null, catalogue);
            }
        }
    }

    [Test]
    public void Source_ServesNoOtherCatalogue()
    {
        var source = new BuiltInConnectorCatalogueSource();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Serves(ConnectorCatalogueNames.For("acme.payroll")), Is.False, "an installed Connector's is not built in");
            Assert.That(source.Serves(ConnectorMessageCatalogues.Connectors), Is.False, "JIM's own catalogues have a source of their own");
            Assert.That(source.Template(ConnectorCatalogueNames.For("acme.payroll"), "Anything", French), Is.Null);
        }
    }

    [Test]
    public void JimApplication_ServesTheBuiltInCataloguesWhereverItRuns()
    {
        // Every host (portal, Worker, Scheduler) and every test that builds a JimApplication gets them, without each
        // having to remember to register them.
        RuntimeHelpers.RunClassConstructor(typeof(JimApplication).TypeHandle);

        Assert.That(BuiltInCatalogues().Where(catalogue => !MessageCatalogueSources.Serves(catalogue)), Is.Empty);
    }
}
