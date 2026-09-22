// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using Moq;
using NUnit.Framework;
using Serilog;
using System.DirectoryServices.Protocols;
using static JIM.Worker.Tests.Connectors.Security.SecurityDescriptorTestData;

namespace JIM.Worker.Tests.Connectors.Security;

/// <summary>
/// Covers reading the security context of the account JIM is bound as, from the rootDSE.
/// <para>
/// Shared by every rights check the LDAP Connector performs, so its one rule matters everywhere: anything short of
/// a full set of memberships is returned as null, never as a smaller set, because a partial context evaluates
/// to a denial the account may not deserve.
/// </para>
/// </summary>
[TestFixture]
public class LdapCallerSecurityContextTests
{
    private const string ServiceAccount = "S-1-5-21-1111111111-2222222222-3333333333-1104";
    private const string HelpDeskGroup = "S-1-5-21-1111111111-2222222222-3333333333-1105";

    private Mock<ILdapOperationExecutor> _executor = null!;

    [SetUp]
    public void SetUp() => _executor = new Mock<ILdapOperationExecutor>();

    private void GivenTheRootDseReturns(SearchResponse response) =>
        _executor.Setup(x => x.SendRequestAsync(It.IsAny<SearchRequest>())).ReturnsAsync(response);

    private Task<HashSet<string>?> ReadAsync() =>
        LdapCallerSecurityContext.ReadAsync(_executor.Object, Log.Logger, "LdapCallerSecurityContextTests");

    [Test]
    public async Task ReadAsync_WithTokenGroups_ReturnsEveryIdentifierReportedAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary("",
            (LdapCallerSecurityContext.AttributeTokenGroups, [Sid(ServiceAccount), Sid(HelpDeskGroup)])));

        var sids = await ReadAsync();

        Assert.That(sids, Is.Not.Null);
        Assert.That(sids, Is.SupersetOf([ServiceAccount, HelpDeskGroup]));
    }

    /// <summary>
    /// Group expansion does not include the identities every authenticated network connection has by virtue of
    /// being one, and a directory can legitimately grant a right to those.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithTokenGroups_AddsTheWellKnownConnectionIdentifiersAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary("",
            (LdapCallerSecurityContext.AttributeTokenGroups, [Sid(ServiceAccount)])));

        var sids = await ReadAsync();

        Assert.That(sids, Is.SupersetOf(["S-1-1-0", "S-1-5-11", "S-1-5-2"]));
    }

    /// <summary>
    /// SELF stands for the object being examined, not the caller. Adding it would match entries meant for a user
    /// acting on their own account.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithTokenGroups_DoesNotAddSelfAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary("",
            (LdapCallerSecurityContext.AttributeTokenGroups, [Sid(ServiceAccount)])));

        var sids = await ReadAsync();

        Assert.That(sids, Does.Not.Contain("S-1-5-10"));
    }

    /// <summary>
    /// One unreadable identifier among many is dropped rather than failing the whole read.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithOneUnparseableIdentifier_DropsItAndKeepsTheRestAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary("",
            (LdapCallerSecurityContext.AttributeTokenGroups, [Sid(ServiceAccount), [1, 2, 3]])));

        var sids = await ReadAsync();

        Assert.That(sids, Is.Not.Null);
        Assert.That(sids, Does.Contain(ServiceAccount));
    }

    [Test]
    public async Task ReadAsync_AsksTheRootDseForTokenGroupsAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary("",
            (LdapCallerSecurityContext.AttributeTokenGroups, [Sid(ServiceAccount)])));

        await ReadAsync();

        _executor.Verify(x => x.SendRequestAsync(It.Is<SearchRequest>(r =>
            string.IsNullOrEmpty(r.DistinguishedName) &&
            r.Scope == SearchScope.Base &&
            r.Attributes.Contains(LdapCallerSecurityContext.AttributeTokenGroups))), Times.Once);
    }

    #region silences that must not become a partial context

    [Test]
    public async Task ReadAsync_WhenTheRootDseReturnsNoEntries_ReturnsNullAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.EmptySearchResponse());

        Assert.That(await ReadAsync(), Is.Null);
    }

    /// <summary>
    /// Active Directory omits tokenGroups entirely, with no error, when it cannot reach a Global Catalog.
    /// </summary>
    [Test]
    public async Task ReadAsync_WhenTokenGroupsIsAbsent_ReturnsNullAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary(""));

        Assert.That(await ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ReadAsync_WhenNoIdentifierParses_ReturnsNullAsync()
    {
        GivenTheRootDseReturns(LdapTestResponses.SearchResponseWithBinary("",
            (LdapCallerSecurityContext.AttributeTokenGroups, [[1, 2, 3]])));

        Assert.That(await ReadAsync(), Is.Null);
    }

    [Test]
    public async Task ReadAsync_WhenTheDirectoryRefuses_ReturnsNullRatherThanThrowingAsync()
    {
        _executor.Setup(x => x.SendRequestAsync(It.IsAny<SearchRequest>()))
            .ThrowsAsync(new DirectoryOperationException("Insufficient access rights."));

        HashSet<string>? sids = [];
        Assert.That(async () => sids = await ReadAsync(), Throws.Nothing);
        Assert.That(sids, Is.Null);
    }

    /// <summary>
    /// A connection failure is a sibling of a refusal, not a subclass of it, so it needs its own proof that it
    /// stays inside the reader rather than escaping a check that has no catch of its own.
    /// </summary>
    [Test]
    public async Task ReadAsync_WhenTheConnectionFails_ReturnsNullRatherThanThrowingAsync()
    {
        _executor.Setup(x => x.SendRequestAsync(It.IsAny<SearchRequest>()))
            .ThrowsAsync(new LdapException("The server is unavailable."));

        HashSet<string>? sids = [];
        Assert.That(async () => sids = await ReadAsync(), Throws.Nothing);
        Assert.That(sids, Is.Null);
    }

    #endregion
}
