// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using static JIM.Worker.Tests.Connectors.LdapProbeForest;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector probing several attributes of one object at once (Generated Value Forms, #2023, Phase 2): one
/// search per route rather than one per attribute. Attributes searched in the Connected System's own partitions share
/// one search per partition root; forest-wide attributes routed through a Global Catalog share one search of it. Each
/// attribute is still answered on its own terms: from its own values, against its own control value, so one
/// attribute's trouble (a size limit, a blind Global Catalog) is settled for that attribute alone.
/// </summary>
[TestFixture]
public class LdapConnectorUniquenessProbeBatchTests
{
    // ---- One search per route ----

    [Test]
    public async Task ProbeAttributesAsync_DomainAndForestWideAttributesInAMultiDomainForest_SearchesTheDomainOnceAndTheForestOnceAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true)
            .HoldingIn(Root, "sAMAccountName", "asmith", "jbloggs");
        var globalCatalog = new FakeGlobalCatalog()
            .HoldingIn("userPrincipalName", "asmith@corp.local")
            .HoldingIn("mail", "asmith@corp.local", "jbloggs@corp.local");

        var results = await Probe(directory, globalCatalog).ProbeAttributesAsync(
        [
            Request("userPrincipalName", "asmith@corp.local", "jbloggs@corp.local"),
            Request("sAMAccountName", "asmith", "jbloggs", "jbloggs1"),
            Request("mail", "asmith@corp.local", "jbloggs@corp.local")
        ], [Root], CancellationToken.None);

        var forestSearch = globalCatalog.Requests.Single();
        var domainSearch = directory.PartitionSearches.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Outcomes), Is.EqualTo(new[]
            {
                new[] { UniquenessProbeOutcome.NotFound },
                new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound },
                new[] { UniquenessProbeOutcome.Found }
            }), "each attribute answered in the order asked");
            Assert.That(results.Select(r => r.Caveat), Is.All.Null);
            Assert.That(forestSearch.Filter, Is.EqualTo("(|(userPrincipalName=jbloggs@corp.local)(userPrincipalName=asmith@corp.local)"
                + "(mail=jbloggs@corp.local)(proxyAddresses=smtp:jbloggs@corp.local)(mail=asmith@corp.local)(proxyAddresses=smtp:asmith@corp.local))"));
            Assert.That(forestSearch.Attributes, Is.EqualTo(new[] { "userPrincipalName", "mail", "proxyAddresses" }));
            Assert.That(domainSearch.Filter, Is.EqualTo("(|(sAMAccountName=jbloggs)(sAMAccountName=jbloggs1)(sAMAccountName=asmith))"));
            Assert.That(domainSearch.Attributes, Is.EqualTo(new[] { "sAMAccountName" }));
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_SingleDomainForest_SearchesThePartitionOnceForEveryAttributeAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: true)
            .HoldingIn(Root, "sAMAccountName", "asmith")
            .HoldingIn(Root, "userPrincipalName", "asmith@corp.local", "jbloggs@corp.local")
            .HoldingIn(Root, "mail", "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog();

        var results = await Probe(directory, globalCatalog).ProbeAttributesAsync(
        [
            Request("sAMAccountName", "asmith", "jbloggs"),
            Request("userPrincipalName", "asmith@corp.local", "jbloggs@corp.local"),
            Request("mail", "asmith@corp.local", "jbloggs@corp.local")
        ], [Root], CancellationToken.None);

        var search = directory.PartitionSearches.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Outcomes), Is.EqualTo(new[]
            {
                new[] { UniquenessProbeOutcome.NotFound },
                new[] { UniquenessProbeOutcome.Found },
                new[] { UniquenessProbeOutcome.NotFound }
            }));
            Assert.That(search.Filter, Is.EqualTo("(|(sAMAccountName=jbloggs)(sAMAccountName=asmith)"
                + "(userPrincipalName=jbloggs@corp.local)(userPrincipalName=asmith@corp.local)"
                + "(mail=jbloggs@corp.local)(proxyAddresses=smtp:jbloggs@corp.local)(mail=asmith@corp.local)(proxyAddresses=smtp:asmith@corp.local))"));
            Assert.That(search.Attributes, Is.EqualTo(new[] { "sAMAccountName", "userPrincipalName", "mail", "proxyAddresses" }));
            Assert.That(globalCatalog.OpenAttempts, Is.Zero, "a forest of one domain is that domain");
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_DirectoryThatIsNotActiveDirectory_SearchesEachRootOnceForEveryAttributeAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: false, activeDirectory: false)
            .HoldingIn(Root, "uid", "asmith")
            .HoldingIn(Root, "mail", "asmith@corp.local")
            .HoldingIn(SecondRoot, "uid", "jbloggs");

        var results = await Probe(directory, new FakeGlobalCatalog()).ProbeAttributesAsync(
        [
            Request("uid", "asmith", "jbloggs", "jbloggs1"),
            Request("mail", "asmith@corp.local", "jbloggs@corp.local")
        ], [Root, SecondRoot], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Outcomes), Is.EqualTo(new[]
            {
                new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound },
                new[] { UniquenessProbeOutcome.NotFound }
            }));
            Assert.That(directory.PartitionSearches.Select(r => r.DistinguishedName), Is.EqualTo(new[] { Root, SecondRoot }));
            Assert.That(directory.PartitionSearches.Select(r => r.Attributes.Cast<string>()), Is.All.EqualTo(new[] { "uid", "mail" }),
                "outside Active Directory, mail is an ordinary attribute");
        }
    }

    /// <summary>
    /// Every value a combined search returns is genuinely held, but only by the attribute it was returned in: an account
    /// name taken as a sAMAccountName says nothing about whether it is free as an employeeID.
    /// </summary>
    [Test]
    public async Task ProbeAttributesAsync_ValueHeldInAnotherAttribute_IsNotFoundForThisOneAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: false)
            .HoldingIn(Root, "sAMAccountName", "asmith", "jbloggs")
            .HoldingIn(Root, "employeeID", "asmith");

        var results = await Probe(directory, new FakeGlobalCatalog()).ProbeAttributesAsync(
        [
            Request("sAMAccountName", "asmith", "jbloggs"),
            Request("employeeID", "asmith", "jbloggs")
        ], [Root], CancellationToken.None);

        Assert.That(results.Select(r => r.Outcomes), Is.EqualTo(new[]
        {
            new[] { UniquenessProbeOutcome.Found },
            new[] { UniquenessProbeOutcome.NotFound }
        }));
    }

    // ---- One attribute's trouble stays with that attribute ----

    [Test]
    public async Task ProbeAttributesAsync_ControlValueMissingForOneAttribute_LeavesOnlyThatAttributeUndeterminedAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: false)
            .HoldingIn(Root, "sAMAccountName", "asmith", "jbloggs");

        var results = await Probe(directory, new FakeGlobalCatalog()).ProbeAttributesAsync(
        [
            Request("sAMAccountName", "asmith", "jbloggs"),
            Request("employeeID", "E1001", "E2002")
        ], [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
            Assert.That(results[0].Reason, Is.Null);
            Assert.That(results[1].Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(results[1].Reason, Does.Contain("employeeID"));
            Assert.That(results[1].IsFailure, Is.False);
            Assert.That(directory.PartitionSearches.Count(), Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A Global Catalog that does not return one attribute's control value is blind to that attribute, not to the
    /// others it answered in the same search: only that attribute is searched for in the domain, with a caveat saying
    /// what the domain cannot see.
    /// </summary>
    [Test]
    public async Task ProbeAttributesAsync_GlobalCatalogBlindToOneAttribute_SearchesTheDomainForThatAttributeOnlyAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true)
            .HoldingIn(Root, "mail", "asmith@corp.local", "jbloggs@corp.local");
        var globalCatalog = new FakeGlobalCatalog()
            .HoldingIn("userPrincipalName", "asmith@corp.local", "jbloggs@corp.local");

        var results = await Probe(directory, globalCatalog).ProbeAttributesAsync(
        [
            Request("userPrincipalName", "asmith@corp.local", "jbloggs@corp.local"),
            Request("mail", "asmith@corp.local", "jbloggs@corp.local")
        ], [Root], CancellationToken.None);

        var domainSearch = directory.PartitionSearches.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
            Assert.That(results[0].Caveat, Is.Null, "the Global Catalog answered for userPrincipalName");
            Assert.That(results[1].Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }), "the domain answered for mail");
            Assert.That(results[1].Caveat, Does.Contain("mail").And.Contain("did not return"));
            Assert.That(globalCatalog.Requests, Has.Count.EqualTo(1));
            Assert.That(domainSearch.Attributes, Is.EqualTo(new[] { "mail", "proxyAddresses" }));
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_CombinedSearchExceedsTheSizeLimit_SearchesEachAttributeOnItsOwnAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: false)
        {
            CombinedSearchThrows = SizeLimitExceeded()
        }
            .HoldingIn(Root, "sAMAccountName", "asmith", "jbloggs")
            .HoldingIn(Root, "employeeID", "E1001");

        var results = await Probe(directory, new FakeGlobalCatalog()).ProbeAttributesAsync(
        [
            Request("sAMAccountName", "asmith", "jbloggs"),
            Request("employeeID", "E1001", "E2002")
        ], [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Outcomes), Is.EqualTo(new[]
            {
                new[] { UniquenessProbeOutcome.Found },
                new[] { UniquenessProbeOutcome.NotFound }
            }), "a limit the attributes reach together says nothing about either alone");
            Assert.That(directory.PartitionSearches.Select(r => r.Attributes.Count), Is.EqualTo(new[] { 2, 1, 1 }));
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_CombinedSearchRefused_SearchesEachAttributeOnItsOwnAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: false)
        {
            CombinedSearchThrows = Refused()
        }
            .HoldingIn(Root, "sAMAccountName", "asmith", "jbloggs")
            .HoldingIn(Root, "employeeID", "E1001");

        var results = await Probe(directory, new FakeGlobalCatalog()).ProbeAttributesAsync(
        [
            Request("sAMAccountName", "asmith", "jbloggs"),
            Request("employeeID", "E1001", "E2002")
        ], [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.IsFailure), Is.All.False);
            Assert.That(results[0].Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
            Assert.That(directory.PartitionSearches.Count(), Is.EqualTo(3));
        }
    }

    /// <summary>
    /// A directory that cannot be reached cannot be reached for any attribute; asking again for each would only multiply
    /// the wait before the run carries on with JIM's own records.
    /// </summary>
    [Test]
    public async Task ProbeAttributesAsync_DirectoryUnreachable_FailsEveryAttributeWithoutSearchingAgainAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: false)
        {
            SearchThrows = new LdapException(81, "The LDAP server is unavailable.")
        };

        var results = await Probe(directory, new FakeGlobalCatalog()).ProbeAttributesAsync(
        [
            Request("sAMAccountName", "asmith", "jbloggs"),
            Request("employeeID", "E1001", "E2002")
        ], [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.IsFailure), Is.All.True);
            Assert.That(results.Select(r => r.Reason), Is.All.Contains("unavailable"));
            Assert.That(directory.PartitionSearches.Count(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_GlobalCatalogRefusesTheSearch_SearchesTheDomainOnceForEveryAttributeAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true)
            .HoldingIn(Root, "userPrincipalName", "asmith@corp.local", "jbloggs@corp.local")
            .HoldingIn(Root, "mail", "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog { SearchThrows = Refused() };

        var results = await Probe(directory, globalCatalog).ProbeAttributesAsync(
        [
            Request("userPrincipalName", "asmith@corp.local", "jbloggs@corp.local"),
            Request("mail", "asmith@corp.local", "jbloggs@corp.local")
        ], [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Outcomes), Is.EqualTo(new[]
            {
                new[] { UniquenessProbeOutcome.Found },
                new[] { UniquenessProbeOutcome.NotFound }
            }), "the domain's own answer stands");
            Assert.That(results[0].Caveat, Does.Contain("userPrincipalName").And.Contain("refused"));
            Assert.That(results[1].Caveat, Does.Contain("mail").And.Contain("refused"));
            Assert.That(globalCatalog.Requests, Has.Count.EqualTo(1), "not asked again for each attribute");
            Assert.That(directory.PartitionSearches.Single().Attributes, Is.EqualTo(new[] { "userPrincipalName", "mail", "proxyAddresses" }));
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_CombinedForestSearchExceedsTheSizeLimit_SearchesTheForestForEachAttributeOnItsOwnAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true);
        var globalCatalog = new FakeGlobalCatalog { CombinedSearchThrows = SizeLimitExceeded() }
            .HoldingIn("userPrincipalName", "asmith@corp.local", "jbloggs@corp.local")
            .HoldingIn("mail", "asmith@corp.local");

        var results = await Probe(directory, globalCatalog).ProbeAttributesAsync(
        [
            Request("userPrincipalName", "asmith@corp.local", "jbloggs@corp.local"),
            Request("mail", "asmith@corp.local", "jbloggs@corp.local")
        ], [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(r => r.Outcomes), Is.EqualTo(new[]
            {
                new[] { UniquenessProbeOutcome.Found },
                new[] { UniquenessProbeOutcome.NotFound }
            }));
            Assert.That(results.Select(r => r.Caveat), Is.All.Null, "the Global Catalog answered for both");
            Assert.That(globalCatalog.Requests, Has.Count.EqualTo(3));
            Assert.That(directory.PartitionSearches, Is.Empty);
        }
    }

    [Test]
    public async Task ProbeAttributesAsync_OneAttributeNameJimCannotProbeFor_FailsThatAttributeAndSearchesTheRestAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: false)
            .HoldingIn(Root, "sAMAccountName", "asmith");

        var results = await Probe(directory, new FakeGlobalCatalog()).ProbeAttributesAsync(
        [
            Request("bad)(attr", "asmith", "jbloggs"),
            Request("sAMAccountName", "asmith", "jbloggs")
        ], [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].IsFailure, Is.True);
            Assert.That(results[1].Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(directory.PartitionSearches.Single().Attributes, Is.EqualTo(new[] { "sAMAccountName" }));
        }
    }

    private static UniquenessProbeRequest Request(string attributeName, string? controlValue, params string[] candidates) => new()
    {
        ObjectTypeName = "user",
        AttributeName = attributeName,
        Candidates = candidates,
        ControlValue = controlValue
    };

    private static DirectoryOperationException SizeLimitExceeded() =>
        new(LdapTestResponses.Create<SearchResponse>(ResultCode.SizeLimitExceeded), "size limit exceeded");

    private static DirectoryOperationException Refused() =>
        new(LdapTestResponses.Create<SearchResponse>(ResultCode.UnwillingToPerform), "unwilling to perform");

    private static LdapConnectorUniquenessProbe Probe(FakeDomainController directory, FakeGlobalCatalog globalCatalog, string? configuredServer = null) =>
        new(directory.Executor, new LoggerConfiguration().CreateLogger(),
            new LdapGlobalCatalogProbeOptions(configuredServer, ConnectedServer, LdapConnectorUniquenessProbe.GlobalCatalogPort(false), globalCatalog.Open));
}
