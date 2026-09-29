// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A partition's containers are read page by page on a directory that pages (#1853): a domain with more
/// organisational units than MaxPageSize is refused as an unpaged search, and Samba AD, which does not page, is sent
/// no control.
/// </summary>
[TestFixture]
public class LdapConnectorPartitionsContainerPagingTests
{
    private const string DomainDn = "DC=corp,DC=local";
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    [Test]
    public async Task GetPartitionsAsync_ActiveDirectory_ReadsEveryPageOfAPartitionsContainersAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) => Answer((SearchRequest)request, paged: true));

        var partitions = await new LdapConnectorPartitions(executor.Object, Logger, LdapDirectoryType.ActiveDirectory).GetPartitionsAsync();

        var domain = partitions.Single(partition => partition.Id == DomainDn);
        Assert.That(domain.Containers.Select(container => container.Name), Is.EquivalentTo(new[] { "First", "Second" }),
            "The second organisational unit is on the second page.");
    }

    [Test]
    public async Task GetPartitionsAsync_SambaAd_SendsNoPagingControlAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                var search = (SearchRequest)request;
                Assert.That(search.Controls.OfType<PageResultRequestControl>(), Is.Empty, "Samba AD returns duplicate entries across pages, so it must be sent no paging control.");
                return Answer(search, paged: false);
            });

        var partitions = await new LdapConnectorPartitions(executor.Object, Logger, LdapDirectoryType.SambaAD).GetPartitionsAsync();

        Assert.That(partitions.Single().Containers.Select(container => container.Name), Is.EquivalentTo(new[] { "First", "Second" }));
    }

    private static DirectoryResponse Answer(SearchRequest search, bool paged)
    {
        if (search.Scope == SearchScope.Base && search.Attributes.Contains("configurationNamingContext"))
            return LdapTestResponses.SearchResponseWith("", ("configurationNamingContext", $"CN=Configuration,{DomainDn}"));

        if (((string)search.Filter).Contains("crossRef"))
            return LdapTestResponses.SearchResponseWith($"CN=corp,CN=Partitions,CN=Configuration,{DomainDn}", ("nCName", DomainDn), ("systemFlags", "3"));

        if (((string)search.Filter).Contains("organizationalUnit"))
        {
            var first = LdapTestResponses.Entry($"OU=First,{DomainDn}", ("name", "First"));
            var second = LdapTestResponses.Entry($"OU=Second,{DomainDn}", ("name", "Second"));
            if (!paged)
                return LdapTestResponses.SearchResponseWithEntries(first, second);

            var continuing = search.Controls.OfType<PageResultRequestControl>().SingleOrDefault()?.Cookie is { Length: > 0 };
            return continuing
                ? LdapTestResponses.SearchResponseWithPagingCookie([], second)
                : LdapTestResponses.SearchResponseWithPagingCookie([1], first);
        }

        return LdapTestResponses.EmptySearchResponse();
    }
}
