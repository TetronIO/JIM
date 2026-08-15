// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The search a Container object count runs (#1276). The filter is the part that decides whether the figure an
/// administrator reads matches what a Full Import would actually bring back.
/// </summary>
[TestFixture]
public class LdapConnectorContainerCountsTests
{
    [Test]
    public void BuildObjectClassFilter_OneObjectType_IsNotWrappedInARedundantOr()
    {
        Assert.That(LdapConnectorContainerCounts.BuildObjectClassFilter(["user"]), Is.EqualTo("(objectClass=user)"));
    }

    [Test]
    public void BuildObjectClassFilter_SeveralObjectTypes_MatchesAnyOfThem()
    {
        // One search over the union costs one pass over the directory; one search per Object Type costs a pass
        // each, and the counts have to be merged afterwards anyway.
        Assert.That(LdapConnectorContainerCounts.BuildObjectClassFilter(["user", "group"]),
            Is.EqualTo("(|(objectClass=user)(objectClass=group))"));
    }

    [Test]
    public void BuildObjectClassFilter_TheSameTypesAsAFullImport_ProducesTheUnionOfItsFilters()
    {
        // A Full Import searches (objectClass={type}) per selected Object Type. The count has to match that set
        // exactly, or it reports a number the next import contradicts.
        var filter = LdapConnectorContainerCounts.BuildObjectClassFilter(["user", "group", "contact"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter, Does.Contain("(objectClass=user)"));
            Assert.That(filter, Does.Contain("(objectClass=group)"));
            Assert.That(filter, Does.Contain("(objectClass=contact)"));
            Assert.That(filter, Does.StartWith("(|"));
        }
    }

    [TestCase("weird(name", "(objectClass=weird\\28name)")]
    [TestCase("weird)name", "(objectClass=weird\\29name)")]
    [TestCase("weird*name", "(objectClass=weird\\2aname)")]
    [TestCase("weird\\name", "(objectClass=weird\\5cname)")]
    public void BuildObjectClassFilter_AnObjectTypeCarryingAReservedCharacter_IsEscaped(string objectTypeName, string expected)
    {
        // Object Type names come from the directory's own schema rather than from a person, so this is not a
        // user-input injection route. It is still a filter built by concatenation, and one schema carrying a
        // parenthesis would otherwise produce a malformed filter or a search that matches the wrong thing.
        Assert.That(LdapConnectorContainerCounts.BuildObjectClassFilter([objectTypeName]), Is.EqualTo(expected));
    }

    [Test]
    public void ShouldStopForBudget_WellInsideTheBudget_KeepsGoing()
    {
        Assert.That(LdapConnectorContainerCounts.ShouldStopForBudget(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)), Is.False);
    }

    [Test]
    public void ShouldStopForBudget_PastTheBudget_Stops()
    {
        // Counting is folded into Retrieve Hierarchy, so it is spending an administrator's wait on something they
        // did not ask for by name. The hierarchy is the thing they wanted; the count is not allowed to hold it
        // hostage indefinitely on a large directory.
        Assert.That(LdapConnectorContainerCounts.ShouldStopForBudget(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(30)), Is.True);
    }

    [Test]
    public void ShouldStopForBudget_ExactlyOnTheBudget_Stops()
    {
        Assert.That(LdapConnectorContainerCounts.ShouldStopForBudget(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)), Is.True);
    }

    [Test]
    public void ShouldStopForBudget_NoBudgetSet_NeverStops()
    {
        // Zero or less means "no budget", which is what an unattended caller wants; the cancellation token is then
        // the only thing that stops it.
        Assert.That(LdapConnectorContainerCounts.ShouldStopForBudget(TimeSpan.FromHours(2), TimeSpan.Zero), Is.False);
    }

    [Test]
    public void BuildObjectClassFilter_ABackslashInAName_IsEscapedOnceNotTwice()
    {
        // The backslash must be escaped before the characters whose escapes introduce backslashes of their own,
        // or "\\" becomes "\\5c5c" and the filter no longer means what it says.
        Assert.That(LdapConnectorContainerCounts.BuildObjectClassFilter(["a\\*b"]), Is.EqualTo("(objectClass=a\\5c\\2ab)"));
    }

    [Test]
    public async Task CountAsync_EntriesReturned_BucketsEachOneUnderTheContainerItSitsDirectlyInAsync()
    {
        var executor = ExecutorReturning(
            LdapTestResponses.SearchResponseWithEntries(
                LdapTestResponses.Entry("cn=Ann,ou=People,dc=corp"),
                LdapTestResponses.Entry("cn=Bob,ou=People,dc=corp"),
                LdapTestResponses.Entry("cn=Cat,ou=Sales,ou=Corp,dc=corp")));

        var result = await CountAsync(executor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Complete, Is.True);
            Assert.That(result.DirectCountsByContainerIdentifier["ou=People,dc=corp"], Is.EqualTo(2));
            Assert.That(result.DirectCountsByContainerIdentifier["ou=Sales,ou=Corp,dc=corp"], Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CountAsync_EntryAtTheTopOfTheNamespace_BelongsToNoContainerAsync()
    {
        // An entry with no parent sits above every Container, so there is nothing to attribute it to. Counting it
        // against something would put an object in a Container an import from that Container would never return.
        var executor = ExecutorReturning(LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.Entry("dc=corp")));

        var result = await CountAsync(executor);

        Assert.That(result.DirectCountsByContainerIdentifier, Is.Empty);
    }

    [Test]
    public async Task CountAsync_TheSearchItRuns_AsksForNoAttributesOverTheWholeSubtreeAsync()
    {
        // The whole design rests on this: counting is one attribute-free subtree search per partition, not an
        // import. A search that starts returning attributes turns a cheap count into a full read of the directory.
        SearchRequest? sent = null;
        var executor = new Mock<ILdapOperationExecutor>();
        executor
            .Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Callback<DirectoryRequest, TimeSpan>((request, _) => sent = (SearchRequest)request)
            .Returns(LdapTestResponses.EmptySearchResponse());

        await CountAsync(executor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent!.Attributes.Cast<string>(), Is.EqualTo(new[] { "1.1" }),
                "RFC 4511's 'no attributes at all' selector");
            Assert.That(sent!.Scope, Is.EqualTo(SearchScope.Subtree));
            Assert.That(sent!.DistinguishedName, Is.EqualTo("dc=corp"));
        }
    }

    [TestCase(ResultCode.SizeLimitExceeded)]
    [TestCase(ResultCode.TimeLimitExceeded)]
    [TestCase(ResultCode.AdminLimitExceeded)]
    public async Task CountAsync_DirectoryStopsTheSearchAtItsOwnLimit_ReportsAnIncompleteCountAsync(ResultCode resultCode)
    {
        // The counts gathered so far are real but short of the truth. JIM discards them rather than displaying
        // them, and this is what tells it to.
        var executor = ExecutorThrowing(new DirectoryOperationException(
            LdapTestResponses.Create<SearchResponse>(resultCode), "limit exceeded"));

        var result = await CountAsync(executor);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Complete, Is.False);
            Assert.That(result.IncompleteReason, Does.Contain("limit"),
                "the administrator can raise a directory limit, but only if told that is what stopped it");
        }
    }

    [Test]
    public async Task CountAsync_BudgetAlreadySpent_ReportsAnIncompleteCountRatherThanHoldingUpTheHierarchyAsync()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse());

        var result = await CountAsync(executor, budget: TimeSpan.FromTicks(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Complete, Is.False);
            Assert.That(result.IncompleteReason, Does.Contain("Counting stopped"));
            Assert.That(result.DirectCountsByContainerIdentifier, Is.Empty);
        }
    }

    [Test]
    public async Task CountAsync_Cancelled_ReportsAnIncompleteCountAsync()
    {
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await new LdapConnectorContainerCounts(executor.Object, Log.Logger, supportsPaging: false)
            .CountAsync(Partition(), ["user"], cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Complete, Is.False);
            Assert.That(result.IncompleteReason, Does.Contain("cancelled"));
        }
    }

    [Test]
    public async Task CountAsync_NoObjectTypesSelected_CountsNothingAndDoesNotSearchAsync()
    {
        // A count across Object Types JIM will never import is a number nobody can act on, and running the search
        // to produce it spends the administrator's wait for nothing.
        var executor = ExecutorReturning(LdapTestResponses.EmptySearchResponse());

        var result = await new LdapConnectorContainerCounts(executor.Object, Log.Logger, supportsPaging: false)
            .CountAsync(Partition(), [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Complete, Is.True, "nothing was asked for, so nothing is missing");
            Assert.That(result.DirectCountsByContainerIdentifier, Is.Empty);
            executor.Verify(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()), Times.Never);
        }
    }

    [Test]
    public void CountAsync_DirectoryFailsForAnyOtherReason_PropagatesRatherThanReportingAnEmptyCountAsync()
    {
        // A count that returns "complete, and everything is empty" after the directory refused the search would be
        // a lie of exactly the shape #1276 exists to avoid. Let it throw; the caller warns and shows no counts.
        var executor = ExecutorThrowing(new DirectoryOperationException(
            LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "no rights"));

        Assert.That(async () => await CountAsync(executor), Throws.TypeOf<DirectoryOperationException>());
    }

    private static Task<ConnectorContainerObjectCountResult> CountAsync(Mock<ILdapOperationExecutor> executor, TimeSpan? budget = null) =>
        new LdapConnectorContainerCounts(executor.Object, Log.Logger, supportsPaging: false, budget)
            .CountAsync(Partition(), ["user"], CancellationToken.None);

    private static ConnectorPartition Partition() => new() { Id = "dc=corp", Name = "dc=corp" };

    private static Mock<ILdapOperationExecutor> ExecutorReturning(SearchResponse response)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>())).Returns(response);
        return executor;
    }

    private static Mock<ILdapOperationExecutor> ExecutorThrowing(Exception exception)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>())).Throws(exception);
        return executor;
    }
}
