// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Domain controller discovery reads the forest's nTDSDSA objects page by page where the directory pages (#1853),
/// so that a forest with more domain controllers than MaxPageSize is listed in full and every metadata search
/// follows the one paging rule.
/// </summary>
[TestFixture]
public class LdapConnectorDirectoryServersPagingTests
{
    private const string ConfigurationDn = "CN=Configuration,DC=corp,DC=local";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    [Test]
    public async Task GetDirectoryServersAsync_WhenTheDirectoryPages_ListsTheDomainControllersOnEveryPageAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                var search = (SearchRequest)request;
                if (search.Scope == SearchScope.Base && search.Attributes.Contains("configurationNamingContext"))
                    return LdapTestResponses.SearchResponseWith("", ("configurationNamingContext", ConfigurationDn));

                if (((string)search.Filter).Contains("nTDSDSA"))
                {
                    var continuing = search.Controls.OfType<PageResultRequestControl>().SingleOrDefault()?.Cookie is { Length: > 0 };
                    return continuing
                        ? LdapTestResponses.SearchResponseWithPagingCookie([], LdapTestResponses.Entry(NtdsDsaDn("DC2")))
                        : LdapTestResponses.SearchResponseWithPagingCookie([1], LdapTestResponses.Entry(NtdsDsaDn("DC1")));
                }

                if (((string)search.Filter).Contains("objectClass=server"))
                {
                    var server = search.DistinguishedName.Split(',')[0]["CN=".Length..];
                    return LdapTestResponses.SearchResponseWith(search.DistinguishedName, ("dNSHostName", $"{server.ToLowerInvariant()}.corp.local"));
                }

                return LdapTestResponses.EmptySearchResponse();
            });

        var servers = await new LdapConnectorDirectoryServers(executor.Object, Logger, supportsPaging: true).GetDirectoryServersAsync();

        Assert.That(servers.Select(server => server.HostName), Is.EquivalentTo(new[] { "dc1.corp.local", "dc2.corp.local" }),
            "The second domain controller is on the second page.");
    }

    private static string NtdsDsaDn(string server) => $"CN=NTDS Settings,CN={server},CN=Servers,CN=Default-First-Site-Name,CN=Sites,{ConfigurationDn}";
}
