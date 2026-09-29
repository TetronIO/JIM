// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The paged read behind the connector's metadata searches (#1853): every page is followed where the directory
/// pages, no control is sent where it does not, a cookie refused on a later page ends the read with what was
/// already answered, and a directory that stops at its own limit is not hidden.
/// </summary>
[TestFixture]
public class LdapPagedSearchTests
{
    private static readonly ILogger Logger = Serilog.Core.Logger.None;

    private static SearchRequest NewRequest() => new("DC=corp,DC=local", "(objectClass=*)", SearchScope.Subtree);

    private static byte[]? CookieOf(DirectoryRequest request) =>
        ((SearchRequest)request).Controls.OfType<PageResultRequestControl>().SingleOrDefault()?.Cookie;

    private static int PagingControlCount(DirectoryRequest request) =>
        ((SearchRequest)request).Controls.OfType<PageResultRequestControl>().Count();

    [Test]
    public void ReadAll_WhenTheDirectoryDoesNotPage_SendsOneRequestWithoutTheControl()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns(LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry("CN=a"), LdapTestResponses.Entry("CN=b")));

        var entries = LdapPagedSearch.ReadAll(executor.Object, NewRequest(), supportsPaging: false, pageSize: 1000, Logger, "test");

        Assert.That(entries.Select(entry => entry.DistinguishedName), Is.EqualTo(new[] { "CN=a", "CN=b" }));
        executor.Verify(e => e.SendRequest(It.Is<DirectoryRequest>(request => PagingControlCount(request) == 0)), Times.Once);
        executor.VerifyNoOtherCalls();
    }

    [Test]
    public void ReadAll_WhenTheDirectoryPages_FollowsTheCookieUntilTheLastPageWithOneControlPerRequest()
    {
        var cookiesSent = new List<byte[]?>();
        var controlsPerRequest = new List<int>();
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) =>
            {
                cookiesSent.Add(CookieOf(request));
                controlsPerRequest.Add(PagingControlCount(request));
                return CookieOf(request) switch
                {
                    null or { Length: 0 } => LdapTestResponses.SearchResponseWithPagingCookie([1], LdapTestResponses.Entry("CN=a")),
                    [1] => LdapTestResponses.SearchResponseWithPagingCookie([2], LdapTestResponses.Entry("CN=b")),
                    [2] => LdapTestResponses.SearchResponseWithPagingCookie([], LdapTestResponses.Entry("CN=c")),
                    _ => throw new InvalidOperationException("Unexpected cookie.")
                };
            });

        var entries = LdapPagedSearch.ReadAll(executor.Object, NewRequest(), supportsPaging: true, pageSize: 1, Logger, "test");

        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(entry => entry.DistinguishedName), Is.EqualTo(new[] { "CN=a", "CN=b", "CN=c" }));
            Assert.That(cookiesSent, Has.Count.EqualTo(3));
            Assert.That(cookiesSent[0], Is.Null.Or.Empty);
            Assert.That(cookiesSent[1], Is.EqualTo(new byte[] { 1 }));
            Assert.That(cookiesSent[2], Is.EqualTo(new byte[] { 2 }));
            Assert.That(controlsPerRequest, Is.All.EqualTo(1), "The paging control must be replaced between pages, not stacked.");
        });
    }

    [Test]
    public void ReadAll_WhenALaterPageRefusesTheCookie_ReturnsThePagesAlreadyRead()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Returns((DirectoryRequest request) => CookieOf(request) is { Length: > 0 }
                ? throw new DirectoryOperationException(
                    LdapTestResponses.Create<SearchResponse>(ResultCode.UnavailableCriticalExtension),
                    "The server does not support the control. The control is critical.")
                : LdapTestResponses.SearchResponseWithPagingCookie([1], LdapTestResponses.Entry("CN=a")));

        var entries = LdapPagedSearch.ReadAll(executor.Object, NewRequest(), supportsPaging: true, pageSize: 1, Logger, "test");

        Assert.That(entries.Select(entry => entry.DistinguishedName), Is.EqualTo(new[] { "CN=a" }));
    }

    [Test]
    public void ReadAll_WhenTheDirectoryStopsTheSearchAtItsLimit_LetsTheRefusalThrough()
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(e => e.SendRequest(It.IsAny<DirectoryRequest>()))
            .Throws(new DirectoryOperationException(
                LdapTestResponses.Create<SearchResponse>(ResultCode.SizeLimitExceeded), "The size limit was exceeded"));

        Assert.That(
            () => LdapPagedSearch.ReadAll(executor.Object, NewRequest(), supportsPaging: true, pageSize: 1000, Logger, "test"),
            Throws.TypeOf<DirectoryOperationException>().With.Message.Contains("size limit"));
    }
}
