// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using System.Text.Json;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Tests for <see cref="LdapConnector.GetDetectedCapabilities"/> (issue #231): mapping the persisted rootDSE
/// facts JIM already captures during connection open (<see cref="LdapConnectorRootDse"/>) to the
/// human-readable capability facts shown on the Connected System details page.
/// </summary>
[TestFixture]
public class LdapConnectorDetectedCapabilitiesTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;
    private readonly LdapConnector _connector = new();

    [Test]
    public void GetDetectedCapabilities_FullData_ReturnsAllExpectedFacts()
    {
        var rootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.ActiveDirectory,
            VendorName = "Microsoft",
            DnsHostName = "dc1.contoso.local",
            PinnedDirectoryServer = "dc1.contoso.local",
            InvocationId = Guid.Parse("11111111-2222-3333-4444-555555555555")
        };
        var persistedData = JsonSerializer.Serialize(rootDse);

        var capabilities = _connector.GetDetectedCapabilities(persistedData, Logger);

        Assert.That(capabilities.Select(c => c.Name), Is.EqualTo(new[]
        {
            "Directory Type", "Vendor", "DNS Host Name", "Paging", "Pinned Directory Server", "Invocation Id"
        }));
        Assert.That(GetValue(capabilities, "Directory Type"), Is.EqualTo("Active Directory"));
        Assert.That(GetValue(capabilities, "Vendor"), Is.EqualTo("Microsoft"));
        Assert.That(GetValue(capabilities, "DNS Host Name"), Is.EqualTo("dc1.contoso.local"));
        Assert.That(GetValue(capabilities, "Paging"), Is.EqualTo("Supported"));
        Assert.That(GetValue(capabilities, "Pinned Directory Server"), Is.EqualTo("dc1.contoso.local"));
        Assert.That(GetValue(capabilities, "Invocation Id"), Is.EqualTo("11111111-2222-3333-4444-555555555555"));
    }

    [Test]
    public void GetDetectedCapabilities_SambaAd_PagingNotSupported_ReturnsNotSupported()
    {
        var rootDse = new LdapConnectorRootDse
        {
            DirectoryType = LdapDirectoryType.SambaAD,
            VendorName = "Samba Team"
        };
        var persistedData = JsonSerializer.Serialize(rootDse);

        var capabilities = _connector.GetDetectedCapabilities(persistedData, Logger);

        Assert.That(GetValue(capabilities, "Directory Type"), Is.EqualTo("Samba AD"));
        Assert.That(GetValue(capabilities, "Paging"), Is.EqualTo("Not Supported"));
    }

    [Test]
    public void GetDetectedCapabilities_OpenLdap_PagingSupported_ReturnsSupported()
    {
        var rootDse = new LdapConnectorRootDse { DirectoryType = LdapDirectoryType.OpenLDAP };
        var persistedData = JsonSerializer.Serialize(rootDse);

        var capabilities = _connector.GetDetectedCapabilities(persistedData, Logger);

        Assert.That(GetValue(capabilities, "Directory Type"), Is.EqualTo("OpenLDAP"));
        Assert.That(GetValue(capabilities, "Paging"), Is.EqualTo("Supported"));
    }

    [Test]
    public void GetDetectedCapabilities_MinimalLegacyJsonMissingNewFields_OmitsAbsentFactsButStillReturnsKnownOnes()
    {
        // Simulates persisted data from before Pinned Directory Server / Invocation Id existed (issue #230):
        // a JSON object carrying only the original fields.
        const string legacyJson = """{"DnsHostName":"dc1.contoso.local","DirectoryType":0,"VendorName":"Microsoft"}""";

        var capabilities = _connector.GetDetectedCapabilities(legacyJson, Logger);

        Assert.That(capabilities.Select(c => c.Name), Is.EqualTo(new[]
        {
            "Directory Type", "Vendor", "DNS Host Name", "Paging"
        }));
        Assert.That(capabilities.Any(c => c.Name == "Pinned Directory Server"), Is.False);
        Assert.That(capabilities.Any(c => c.Name == "Invocation Id"), Is.False);
    }

    [Test]
    public void GetDetectedCapabilities_NullPersistedConnectorData_ReturnsEmptyList()
    {
        var capabilities = _connector.GetDetectedCapabilities(null, Logger);

        Assert.That(capabilities, Is.Empty);
    }

    [Test]
    public void GetDetectedCapabilities_EmptyPersistedConnectorData_ReturnsEmptyList()
    {
        var capabilities = _connector.GetDetectedCapabilities(string.Empty, Logger);

        Assert.That(capabilities, Is.Empty);
    }

    [Test]
    public void GetDetectedCapabilities_CorruptJson_ReturnsEmptyListAndDoesNotThrow()
    {
        List<ConnectorCapability>? capabilities = null;

        Assert.That(() => capabilities = _connector.GetDetectedCapabilities("{not valid json", Logger), Throws.Nothing);

        Assert.That(capabilities, Is.Empty);
    }

    [Test]
    public void GetDetectedCapabilities_JsonNullLiteral_ReturnsEmptyList()
    {
        var capabilities = _connector.GetDetectedCapabilities("null", Logger);

        Assert.That(capabilities, Is.Empty);
    }

    private static string GetValue(List<ConnectorCapability> capabilities, string name) =>
        capabilities.Single(c => c.Name == name).Value;
}
