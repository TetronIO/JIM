// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector names, on every result it wrote, the domain controller its export connection is bound to
/// (#2021, decision D3, PRD requirement 8), read from the bound connection's RootDSE <c>dnsHostName</c>: the Connected
/// System's host may be a domain name any domain controller answers, so only the RootDSE says which one took the
/// write. Exchange is then pointed at the same one. Reading the RootDSE needs a directory; what is pinned here is that
/// the name reaches exactly the results that were written.
/// </summary>
[TestFixture]
public class LdapExportDirectoryServerTests
{
    [Test]
    public void StampDirectoryServer_SuccessfulResults_NameTheDomainController()
    {
        var results = new List<ConnectedSystemExportResult>
        {
            ConnectedSystemExportResult.Succeeded(),
            ConnectedSystemExportResult.Succeeded("guid", "CN=Ada,OU=People,DC=corp,DC=example")
        };

        LdapConnector.StampDirectoryServer(results, "dc01.corp.example");

        Assert.That(results.Select(r => r.DirectoryServer), Is.All.EqualTo("dc01.corp.example"));
    }

    /// <summary>
    /// A failed or never-attempted export wrote nothing, so it names no server: nothing follows it to Exchange.
    /// </summary>
    [Test]
    public void StampDirectoryServer_FailedAndNotAttemptedResults_NameNoServer()
    {
        var results = new List<ConnectedSystemExportResult>
        {
            ConnectedSystemExportResult.Failed("The object already exists."),
            ConnectedSystemExportResult.NotAttempted()
        };

        LdapConnector.StampDirectoryServer(results, "dc01.corp.example");

        Assert.That(results.Select(r => r.DirectoryServer), Is.All.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    public void StampDirectoryServer_NoServerKnown_LeavesItUnnamed(string? directoryServer)
    {
        var results = new List<ConnectedSystemExportResult> { ConnectedSystemExportResult.Succeeded() };

        LdapConnector.StampDirectoryServer(results, directoryServer);

        Assert.That(results[0].DirectoryServer, Is.Null);
    }
}
