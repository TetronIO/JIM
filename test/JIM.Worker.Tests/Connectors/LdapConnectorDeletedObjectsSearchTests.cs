// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The paged tombstone search a USN Delta Import runs against a partition's Deleted Objects container (#1724).
/// Two things are at stake: that the search pages, so a bulk clean-up larger than the directory's page size is
/// imported rather than refused, and that a refusal it cannot recover from is named for what it is rather than
/// read as the directory not supporting the Show Deleted Objects control.
/// </summary>
[TestFixture]
public class LdapConnectorDeletedObjectsSearchTests
{
    private const string ContainerDn = "CN=Deleted Objects,DC=corp,DC=local";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] Cookie = [1, 2, 3, 4];

    #region Request shape

    [Test]
    public void Search_Always_AsksTheContainerForTombstonesChangedAfterTheWatermark()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);

        Search(executor, supportsPaging: false, cookie: null, previousUsn: 41);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent.Value!.DistinguishedName, Is.EqualTo(ContainerDn));
            Assert.That(sent.Value.Scope, Is.EqualTo(SearchScope.Subtree));
            Assert.That(sent.Value.Filter, Is.EqualTo("(&(isDeleted=TRUE)(uSNChanged>=42))"));
            Assert.That(sent.Value.Attributes.Cast<string>(),
                Is.EquivalentTo(new[] { "objectGUID", "objectClass", "isDeleted", "lastKnownParent", "distinguishedName" }));
        }
    }

    [Test]
    public void Search_Always_CarriesTheShowDeletedObjectsControlAsCriticalAndServerSide()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);

        Search(executor, supportsPaging: false, cookie: null);

        var control = sent.Value!.Controls.Cast<DirectoryControl>().SingleOrDefault(c => c.Type == LdapConnectorConstants.LDAP_SERVER_SHOW_DELETED_OID);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(control, Is.Not.Null, "without it the container is invisible and the search answers nothing");
            Assert.That(control!.IsCritical, Is.True);
            Assert.That(control.ServerSide, Is.True);
        }
    }

    [Test]
    public void Search_DirectoryDoesNotSupportPaging_SendsNoPagingControl()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);

        Search(executor, supportsPaging: false, cookie: null);

        Assert.That(sent.Value!.Controls.OfType<PageResultRequestControl>(), Is.Empty);
    }

    [Test]
    public void Search_DirectorySupportsPagingAndNoCookie_SendsAPagingControlForTheFirstPage()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);

        Search(executor, supportsPaging: true, cookie: null, pageSize: 250);

        var control = sent.Value!.Controls.OfType<PageResultRequestControl>().SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(control, Is.Not.Null, "an unpaged search is capped at the directory's page size, and more tombstones than that fail the whole search");
            Assert.That(control!.PageSize, Is.EqualTo(250));
            Assert.That(control.Cookie, Is.Empty);
            Assert.That(control.IsCritical, Is.False, "a directory that cannot page must answer the search rather than refuse it");
        }
    }

    [Test]
    public void Search_GivenACookie_SendsItToResumeTheSearch()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse(), out var sent);

        Search(executor, supportsPaging: true, cookie: Cookie);

        Assert.That(sent.Value!.Controls.OfType<PageResultRequestControl>().Single().Cookie, Is.EqualTo(Cookie));
    }

    #endregion

    #region Response handling

    [Test]
    public void Search_DirectoryAnswersAPageWithACookie_ReturnsTheEntriesAndTheCookie()
    {
        var entries = new[]
        {
            LdapTestResponses.Entry("CN=a\\0ADEL:1," + ContainerDn, ("isDeleted", "TRUE")),
            LdapTestResponses.Entry("CN=b\\0ADEL:2," + ContainerDn, ("isDeleted", "TRUE"))
        };
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithPagingCookie(Cookie, entries), out _);

        var page = Search(executor, supportsPaging: true, cookie: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Outcome, Is.EqualTo(DeletedObjectsSearchOutcome.Read));
            Assert.That(page.Entries.Select(e => e.DistinguishedName), Is.EqualTo(entries.Select(e => e.DistinguishedName)));
            Assert.That(page.NextCookie, Is.EqualTo(Cookie), "the next page of the import resumes from here");
        }
    }

    [Test]
    public void Search_DirectoryAnswersTheLastPage_ReturnsNoCookie()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithPagingCookie([], LdapTestResponses.Entry("CN=a," + ContainerDn)), out _);

        var page = Search(executor, supportsPaging: true, cookie: Cookie);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Outcome, Is.EqualTo(DeletedObjectsSearchOutcome.Read));
            Assert.That(page.Entries, Has.Count.EqualTo(1));
            Assert.That(page.NextCookie, Is.Null, "an empty cookie means there is nothing more to read");
        }
    }

    [Test]
    public void Search_DirectoryDoesNotSupportPagingAndAnswersWithoutAControl_ReturnsTheEntriesAndNoCookie()
    {
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry("CN=a," + ContainerDn)), out _);

        var page = Search(executor, supportsPaging: false, cookie: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Outcome, Is.EqualTo(DeletedObjectsSearchOutcome.Read));
            Assert.That(page.Entries, Has.Count.EqualTo(1));
            Assert.That(page.NextCookie, Is.Null);
        }
    }

    #endregion

    #region Failures

    [Test]
    public void Search_DirectoryExceedsItsSizeLimit_ReportsLimitExceededAndImportsNothing()
    {
        // The exception carries a partial set of entries, but with no cookie the rest can never be fetched, and
        // importing the half that arrived would move the watermark past the half that did not.
        var partial = LdapTestResponses.Create<SearchResponse>(ResultCode.SizeLimitExceeded);
        var executor = ExecutorThrowing(new DirectoryOperationException(partial, "The size limit was exceeded"));

        var page = Search(executor, supportsPaging: false, cookie: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Outcome, Is.EqualTo(DeletedObjectsSearchOutcome.LimitExceeded));
            Assert.That(page.Entries, Is.Empty);
            Assert.That(page.NextCookie, Is.Null);
            Assert.That(page.Detail, Does.Contain("size limit"));
        }
    }

    [Test]
    public void Search_DirectoryRejectsTheCookieAsUnsupported_ReportsAnEmptyReadRatherThanARefusal()
    {
        // Samba AD hands back a cookie on the first page and then refuses it; everything was on that first page.
        var executor = ExecutorThrowing(new DirectoryOperationException("The server does not support the control. The control is critical."));

        var page = Search(executor, supportsPaging: true, cookie: Cookie);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Outcome, Is.EqualTo(DeletedObjectsSearchOutcome.Read));
            Assert.That(page.Entries, Is.Empty);
            Assert.That(page.NextCookie, Is.Null);
        }
    }

    [Test]
    public void Search_DirectoryRejectsTheControlOnTheFirstPage_ReportsARefusal()
    {
        // Without a cookie there was no earlier page for the results to have been on, so this is a real refusal.
        var executor = ExecutorThrowing(new DirectoryOperationException("The server does not support the control. The control is critical."));

        var page = Search(executor, supportsPaging: true, cookie: null);

        Assert.That(page.Outcome, Is.EqualTo(DeletedObjectsSearchOutcome.Refused));
    }

    [Test]
    public void Search_DirectoryRefusesTheSearch_ReportsRefusedWithTheDirectorysReason()
    {
        var executor = ExecutorThrowing(new DirectoryOperationException(
            LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights"));

        var page = Search(executor, supportsPaging: true, cookie: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Outcome, Is.EqualTo(DeletedObjectsSearchOutcome.Refused));
            Assert.That(page.Entries, Is.Empty);
            Assert.That(page.Detail, Does.Contain("insufficient access rights"));
        }
    }

    [Test]
    public void Search_ContainerAnswersNoSuchObjectAsADirectoryOperationException_ReportsContainerMissing()
    {
        // The shape System.DirectoryServices.Protocols actually gives a missing base: the server's noSuchObject
        // arrives as a DirectoryOperationException carrying the response, not as an LdapException with code 32.
        // Caught after the generic refusal, a missing container was reported as a refusal and this arm never ran.
        var executor = ExecutorThrowing(new DirectoryOperationException(
            LdapTestResponses.Create<SearchResponse>(ResultCode.NoSuchObject), "no such object"));

        var page = Search(executor, supportsPaging: true, cookie: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Outcome, Is.EqualTo(DeletedObjectsSearchOutcome.ContainerMissing));
            Assert.That(page.Entries, Is.Empty);
        }
    }

    [Test]
    public void Search_ContainerDoesNotExistAsLdapException32_ReportsContainerMissing()
    {
        // The legacy shape, kept for client libraries that surface noSuchObject this way.
        var executor = ExecutorThrowing(new LdapException(32, "no such object"));

        var page = Search(executor, supportsPaging: true, cookie: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Outcome, Is.EqualTo(DeletedObjectsSearchOutcome.ContainerMissing));
            Assert.That(page.Entries, Is.Empty);
        }
    }

    #endregion

    private static DeletedObjectsPage Search(Mock<ILdapOperationExecutor> executor, bool supportsPaging, byte[]? cookie, long previousUsn = 100, int pageSize = 500) =>
        new LdapConnectorDeletedObjectsSearch(executor.Object, Log.Logger)
            .Search(ContainerDn, previousUsn, pageSize, supportsPaging, cookie, Timeout);

    private static Mock<ILdapOperationExecutor> ExecutorReturning(SearchResponse response, out StrongBox<SearchRequest?> sent)
    {
        var captured = new StrongBox<SearchRequest?>(null);
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), Timeout))
            .Callback<DirectoryRequest, TimeSpan>((request, _) => captured.Value = (SearchRequest)request)
            .Returns(response);
        sent = captured;
        return executor;
    }

    private static Mock<ILdapOperationExecutor> ExecutorThrowing(Exception exception)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>())).Throws(exception);
        return executor;
    }

    /// <summary>A mutable cell, so a request captured inside a Moq callback can be read back through an out parameter.</summary>
    private sealed class StrongBox<T>(T value)
    {
        internal T Value { get; set; } = value;
    }
}
