// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Tests for the pure DN-parsing and nTDSDSA-to-server mapping logic behind Discover Domain Controllers (issue
/// #1167). These are the parts of discovery that do not need a live directory: given an nTDSDSA object's
/// Distinguished Name (as found under CN=Sites,CN=Configuration), can JIM derive the server object's DN and the
/// Active Directory Site name, and given a set of discovered (DN, dNSHostName) pairs, can it build the list an
/// administrator is shown.
/// </summary>
[TestFixture]
public class LdapConnectorDirectoryServerDiscoveryTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    #region GetServerDnFromNtdsDsaDn

    [Test]
    public void GetServerDnFromNtdsDsaDn_NormalDn_ReturnsParentServerDn()
    {
        const string ntdsDsaDn = "CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local";

        var serverDn = LdapConnectorUtilities.GetServerDnFromNtdsDsaDn(ntdsDsaDn);

        Assert.That(serverDn, Is.EqualTo("CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local"));
    }

    [Test]
    public void GetServerDnFromNtdsDsaDn_EscapedCommaInServerName_PreservesEscapedComponent()
    {
        // A server name containing a comma is escaped in the DN ("CN=DC01\, EU,CN=Servers,..."). The parent DN
        // must preserve the escaped comma rather than treating it as an RDN separator.
        const string ntdsDsaDn = @"CN=NTDS Settings,CN=DC01\, EU,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local";

        var serverDn = LdapConnectorUtilities.GetServerDnFromNtdsDsaDn(ntdsDsaDn);

        Assert.That(serverDn, Is.EqualTo(@"CN=DC01\, EU,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local"));
    }

    [Test]
    public void GetServerDnFromNtdsDsaDn_MalformedDn_ReturnsNull()
    {
        var serverDn = LdapConnectorUtilities.GetServerDnFromNtdsDsaDn("not a distinguished name===");

        Assert.That(serverDn, Is.Null);
    }

    [Test]
    public void GetServerDnFromNtdsDsaDn_SingleRdnDn_ReturnsNull()
    {
        // A DN with no parent (a single RDN) cannot be a genuine nTDSDSA object's DN, but the method must not
        // throw: it reports "no server DN" rather than crashing discovery for one malformed entry.
        var serverDn = LdapConnectorUtilities.GetServerDnFromNtdsDsaDn("CN=NTDS Settings");

        Assert.That(serverDn, Is.Null);
    }

    #endregion

    #region GetSiteNameFromNtdsDsaDn

    [Test]
    public void GetSiteNameFromNtdsDsaDn_NormalDn_ReturnsSiteName()
    {
        const string ntdsDsaDn = "CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local";

        var site = LdapConnectorUtilities.GetSiteNameFromNtdsDsaDn(ntdsDsaDn);

        Assert.That(site, Is.EqualTo("Default-First-Site-Name"));
    }

    [Test]
    public void GetSiteNameFromNtdsDsaDn_EscapedCommaInSiteName_ReturnsUnescapedSiteName()
    {
        // A Site name containing a comma is escaped in the DN; the returned value should be the unescaped,
        // human-readable form (what an administrator typed as the Site name), not the escaped DN component.
        const string ntdsDsaDn = @"CN=NTDS Settings,CN=DC01,CN=Servers,CN=London\, HQ,CN=Sites,CN=Configuration,DC=corp,DC=local";

        var site = LdapConnectorUtilities.GetSiteNameFromNtdsDsaDn(ntdsDsaDn);

        Assert.That(site, Is.EqualTo("London, HQ"));
    }

    [Test]
    public void GetSiteNameFromNtdsDsaDn_TooFewRdnComponents_ReturnsNull()
    {
        // Fewer than four RDN components above (and including) the nTDSDSA leaf means there is no Site
        // component to read; this must not throw.
        var site = LdapConnectorUtilities.GetSiteNameFromNtdsDsaDn("CN=NTDS Settings,CN=DC01,CN=Servers");

        Assert.That(site, Is.Null);
    }

    [Test]
    public void GetSiteNameFromNtdsDsaDn_MalformedDn_ReturnsNull()
    {
        var site = LdapConnectorUtilities.GetSiteNameFromNtdsDsaDn("not a distinguished name===");

        Assert.That(site, Is.Null);
    }

    #endregion

    #region MapNtdsDsaEntriesToDirectoryServers

    [Test]
    public void MapNtdsDsaEntriesToDirectoryServers_NormalEntries_ReturnsHostNameAndSiteOrderedByHostName()
    {
        var entries = new List<(string NtdsDsaDn, string? DnsHostName)>
        {
            ("CN=NTDS Settings,CN=DC02,CN=Servers,CN=London,CN=Sites,CN=Configuration,DC=corp,DC=local", "dc02.corp.local"),
            ("CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local", "dc01.corp.local")
        };

        var directoryServers = LdapConnectorUtilities.MapNtdsDsaEntriesToDirectoryServers(entries, Logger);

        Assert.That(directoryServers, Has.Count.EqualTo(2));
        Assert.That(directoryServers[0].HostName, Is.EqualTo("dc01.corp.local"));
        Assert.That(directoryServers[0].Site, Is.EqualTo("Default-First-Site-Name"));
        Assert.That(directoryServers[1].HostName, Is.EqualTo("dc02.corp.local"));
        Assert.That(directoryServers[1].Site, Is.EqualTo("London"));
    }

    [Test]
    public void MapNtdsDsaEntriesToDirectoryServers_MissingDnsHostName_SkipsEntry()
    {
        var entries = new List<(string NtdsDsaDn, string? DnsHostName)>
        {
            ("CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=local", "dc01.corp.local"),
            ("CN=NTDS Settings,CN=DC02,CN=Servers,CN=London,CN=Sites,CN=Configuration,DC=corp,DC=local", null)
        };

        var directoryServers = LdapConnectorUtilities.MapNtdsDsaEntriesToDirectoryServers(entries, Logger);

        Assert.That(directoryServers, Has.Count.EqualTo(1));
        Assert.That(directoryServers[0].HostName, Is.EqualTo("dc01.corp.local"));
    }

    [Test]
    public void MapNtdsDsaEntriesToDirectoryServers_NoEntries_ReturnsEmptyList()
    {
        var directoryServers = LdapConnectorUtilities.MapNtdsDsaEntriesToDirectoryServers([], Logger);

        Assert.That(directoryServers, Is.Empty);
    }

    [Test]
    public void MapNtdsDsaEntriesToDirectoryServers_MalformedDnStillHasDnsHostName_ReturnsEntryWithNullSite()
    {
        // The Site is derived from the DN and can fail to resolve independently of whether dNSHostName was read
        // successfully; a malformed DN must not prevent the server itself from being offered.
        var entries = new List<(string NtdsDsaDn, string? DnsHostName)>
        {
            ("CN=NTDS Settings,CN=DC01,CN=Servers", "dc01.corp.local")
        };

        var directoryServers = LdapConnectorUtilities.MapNtdsDsaEntriesToDirectoryServers(entries, Logger);

        Assert.That(directoryServers, Has.Count.EqualTo(1));
        Assert.That(directoryServers[0].HostName, Is.EqualTo("dc01.corp.local"));
        Assert.That(directoryServers[0].Site, Is.Null);
    }

    #endregion
}
