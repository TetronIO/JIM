// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's live uniqueness probe (Unique Value Generation, #242, release 3, plan Phase 7 item 3): the
/// OR filter and its RFC 4515 escaping, which attributes a partition-wide search can answer for, partition roots as
/// search bases, and how directory faults become results rather than exceptions.
/// </summary>
[TestFixture]
public class LdapConnectorUniquenessProbeTests
{
    private const string Root = "DC=corp,DC=local";
    private const string SecondRoot = "DC=emea,DC=corp,DC=local";

    // ---- Filter construction and escaping ----

    [Test]
    public void BuildFilter_SeveralValues_BuildsOneOrFilterWithAnEqualityAssertionEach()
    {
        var filter = LdapConnectorUniquenessProbe.BuildFilter("sAMAccountName", ["jbloggs", "jbloggs2", "asmith"]);

        Assert.That(filter, Is.EqualTo("(|(sAMAccountName=jbloggs)(sAMAccountName=jbloggs2)(sAMAccountName=asmith))"));
    }

    /// <summary>
    /// A generated value comes from a base expression over source data, so it is untrusted input: a candidate shaped
    /// as filter syntax must only ever be matched, never widen the search into "any entry with a uid".
    /// </summary>
    [Test]
    public void BuildFilter_InjectionAttempt_EscapesItIntoALiteral()
    {
        var filter = LdapConnectorUniquenessProbe.BuildFilter("uid", ["*)(uid=*"]);

        Assert.That(filter, Is.EqualTo(@"(|(uid=\2a\29\28uid=\2a))"));
    }

    [Test]
    public void BuildFilter_BackslashAndNul_AreEscapedPerRfc4515()
    {
        var filter = LdapConnectorUniquenessProbe.BuildFilter("uid", ["a\\b", "c\0d"]);

        Assert.That(filter, Is.EqualTo(@"(|(uid=a\5cb)(uid=c\00d))"));
    }

    [TestCase("sAMAccountName", true)]
    [TestCase("userPrincipalName", true)]
    [TestCase("x-custom-attr", true)]
    [TestCase("1.2.840.113556.1.4.221", true)]
    [TestCase("uid)(objectClass=*", false)]
    [TestCase("uid=", false)]
    [TestCase("1uid", false)]
    [TestCase("", false)]
    [TestCase("u id", false)]
    public void IsValidAttributeDescription_ReturnsWhetherTheNameIsSafeInAFilter(string attributeName, bool expected)
    {
        Assert.That(LdapConnectorUniquenessProbe.IsValidAttributeDescription(attributeName), Is.EqualTo(expected));
    }

    [TestCase("sAMAccountName", true)]
    [TestCase("uid", true)]
    [TestCase("mail", true)]
    [TestCase("cn", false)]
    [TestCase("CN", false)]
    [TestCase("distinguishedName", false)]
    [TestCase("ou", false)]
    [TestCase("", false)]
    public void CanProbeAttribute_ContainerScopedNamesAreNotProbed(string attributeName, bool expected)
    {
        Assert.That(LdapConnectorUniquenessProbe.CanProbeAttribute(attributeName), Is.EqualTo(expected));
    }

    [Test]
    public void LdapConnector_CanProbeAttribute_DelegatesToTheStaticRule()
    {
        IConnectorUniquenessProbe connector = new LdapConnector();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.CanProbeAttribute("sAMAccountName"), Is.True);
            Assert.That(connector.CanProbeAttribute("cn"), Is.False);
        }
    }

    // ---- Searching ----

    [Test]
    public async Task ProbeAsync_ControlReturned_ReportsEachCandidateAsync()
    {
        var requests = new List<SearchRequest>();
        var executor = ExecutorReturning(requests, LdapTestResponses.SearchResponseWithEntries(
            LdapTestResponses.Entry("CN=A Smith,OU=Users," + Root, ("sAMAccountName", "asmith")),
            LdapTestResponses.Entry("CN=J Bloggs,OU=Elsewhere," + Root, ("sAMAccountName", "JBloggs"))));

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs", "jbloggs2"], "asmith"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
            Assert.That(result.IsFailure, Is.False);
            Assert.That(requests, Has.Count.EqualTo(1));
            Assert.That(requests[0].DistinguishedName, Is.EqualTo(Root));
            Assert.That(requests[0].Scope, Is.EqualTo(SearchScope.Subtree));
            Assert.That(requests[0].Filter, Is.EqualTo("(|(sAMAccountName=jbloggs)(sAMAccountName=jbloggs2)(sAMAccountName=asmith))"));
            Assert.That(requests[0].Attributes, Is.EqualTo(new[] { "sAMAccountName" }), "only the probed attribute is asked for");
            Assert.That(requests[0].SizeLimit, Is.EqualTo(LdapConnectorUniquenessProbe.SizeLimit));
        }
    }

    [Test]
    public async Task ProbeAsync_ControlNotReturned_ReportsEveryCandidateUndeterminedAsync()
    {
        var executor = ExecutorReturning([], LdapTestResponses.EmptySearchResponse());

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs"], "asmith"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.Reason, Is.Not.Null);
            Assert.That(result.IsFailure, Is.False);
        }
    }

    /// <summary>
    /// Plan decision 14, revised 2026-10-06: with no control, the filter carries the candidates only, and a value the
    /// search returns is Found whatever the bind can see.
    /// </summary>
    [Test]
    public async Task ProbeAsync_NoControl_SearchesForTheCandidatesOnlyAndAHitIsFoundAsync()
    {
        var requests = new List<SearchRequest>();
        var executor = ExecutorReturning(requests, LdapTestResponses.SearchResponseWith("CN=J Bloggs,OU=Elsewhere," + Root, ("uid", "jbloggs")));

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs", "jbloggs2"], null, "uid"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests[0].Filter, Is.EqualTo("(|(uid=jbloggs)(uid=jbloggs2))"));
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
            Assert.That(result.Reason, Is.Null);
        }
    }

    [Test]
    public async Task ProbeAsync_SeveralPartitionRoots_SearchesEachAndCombinesTheAnswersAsync()
    {
        var requests = new List<SearchRequest>();
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest r, TimeSpan _) =>
            {
                var search = (SearchRequest)r;
                requests.Add(search);
                return search.DistinguishedName == Root
                    ? LdapTestResponses.SearchResponseWith("CN=A Smith," + Root, ("sAMAccountName", "asmith"))
                    : LdapTestResponses.SearchResponseWith("CN=J Bloggs," + SecondRoot, ("sAMAccountName", "jbloggs"));
            });

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs"], "asmith"), [Root, SecondRoot], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests.Select(r => r.DistinguishedName), Is.EqualTo(new[] { Root, SecondRoot }));
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
        }
    }

    [Test]
    public async Task ProbeAsync_NoPartitionRoots_FailsWithoutSearchingAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>(MockBehavior.Strict);

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs"], "asmith"), [], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
        }
    }

    [Test]
    public async Task ProbeAsync_UnsafeAttributeName_FailsWithoutSearchingAsync()
    {
        var executor = new Mock<ILdapOperationExecutor>(MockBehavior.Strict);

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs"], "asmith", "uid)(objectClass=*"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Reason, Does.Contain("not an LDAP attribute name"));
        }
    }

    [Test]
    public async Task ProbeAsync_SizeLimitExceeded_IsUndeterminedNotAFailureAsync()
    {
        var executor = ExecutorThrowing(new DirectoryOperationException(
            LdapTestResponses.Create<SearchResponse>(ResultCode.SizeLimitExceeded), "size limit exceeded"));

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs"], "asmith"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsFailure, Is.False, "an attribute that is not unique says nothing about the rest of the system");
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.Reason, Does.Contain("not unique"));
        }
    }

    [Test]
    public async Task ProbeAsync_Refused_IsAFailureNamingTheResultCodeAsync()
    {
        var executor = ExecutorThrowing(new DirectoryOperationException(
            LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights"));

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs"], "asmith"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Reason, Does.Contain("InsufficientAccessRights"));
        }
    }

    [Test]
    public async Task ProbeAsync_ClientTimeout_IsAFailureSayingSoAsync()
    {
        var executor = ExecutorThrowing(new LdapException(85, "timeout"));

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs"], "asmith"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Reason, Does.Contain("did not answer within"));
        }
    }

    [Test]
    public async Task ProbeAsync_ServerDown_IsAFailureAsync()
    {
        var executor = ExecutorThrowing(new LdapException(81, "The LDAP server is unavailable."));

        var result = await Probe(executor).ProbeAsync(Request(["jbloggs"], "asmith"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Reason, Does.Contain("The LDAP server is unavailable"));
        }
    }

    [Test]
    public void ProbeAsync_MalformedBatch_Throws()
    {
        var executor = new Mock<ILdapOperationExecutor>(MockBehavior.Strict);

        Assert.That(() => Probe(executor).ProbeAsync(Request([], null), [Root], CancellationToken.None), Throws.ArgumentException);
    }

    // ---- Capability declarations ----

    /// <summary>
    /// The three Connectors that export to identity stores probe (LDAP in release 3, SCIM and SQL by #1941); a
    /// declaration without the implementation would have every run report the system as unprobed.
    /// </summary>
    [Test]
    public void SupportsUniquenessProbe_IsDeclaredByTheConnectorsThatExportToIdentityStores()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new LdapConnector().SupportsUniquenessProbe, Is.True);
            Assert.That(new LdapConnector(), Is.InstanceOf<IConnectorUniquenessProbe>());
            Assert.That(new JIM.Connectors.SCIM.ScimConnector().SupportsUniquenessProbe, Is.True);
            Assert.That(new JIM.Connectors.SCIM.ScimConnector(), Is.InstanceOf<IConnectorUniquenessProbe>());
            Assert.That(new JIM.Connectors.Sql.SqlConnector().SupportsUniquenessProbe, Is.True);
            Assert.That(new JIM.Connectors.Sql.SqlConnector(), Is.InstanceOf<IConnectorUniquenessProbe>());
            Assert.That(new JIM.Connectors.File.FileConnector().SupportsUniquenessProbe, Is.False);
            Assert.That(new JIM.Connectors.Mock.MockCallConnector().SupportsUniquenessProbe, Is.False);
            Assert.That(new JIM.Connectors.Mock.MockFileConnector().SupportsUniquenessProbe, Is.False);
        }
    }

    private static UniquenessProbeRequest Request(IReadOnlyList<string> candidates, string? controlValue, string attributeName = "sAMAccountName") => new()
    {
        ObjectTypeName = "user",
        AttributeName = attributeName,
        Candidates = candidates,
        ControlValue = controlValue
    };

    private static LdapConnectorUniquenessProbe Probe(Mock<ILdapOperationExecutor> executor) =>
        new(executor.Object, new LoggerConfiguration().CreateLogger());

    private static Mock<ILdapOperationExecutor> ExecutorReturning(List<SearchRequest> requests, SearchResponse response)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
            .Returns((DirectoryRequest r, TimeSpan _) =>
            {
                requests.Add((SearchRequest)r);
                return response;
            });
        return executor;
    }

    private static Mock<ILdapOperationExecutor> ExecutorThrowing(Exception exception)
    {
        var executor = new Mock<ILdapOperationExecutor>();
        executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>())).Throws(exception);
        return executor;
    }
}
