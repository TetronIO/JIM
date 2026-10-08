// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The LDAP Connector's live uniqueness probe of forest-wide attributes through a Global Catalog (#1940). An attribute
/// unique across an Active Directory forest (userPrincipalName above all) is searched through a Global Catalog when
/// the forest has more than one domain, or when the administrator names one; otherwise, or when the Global Catalog
/// cannot be searched, the Connected System's own partition is searched and the answer says what it could not reach.
/// In Active Directory a probe of mail also searches proxyAddresses, where an address used as an alias is kept.
/// </summary>
[TestFixture]
public class LdapConnectorUniquenessProbeGlobalCatalogTests
{
    private const string Root = "DC=corp,DC=local";
    private const string ConfigurationNamingContext = "CN=Configuration,DC=corp,DC=local";
    private const string SchemaNamingContext = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    private const string PartitionsContainer = "CN=Partitions,CN=Configuration,DC=corp,DC=local";
    private const string ConnectedServer = "dc1.corp.local";

    // ---- Which attributes are forest-wide ----

    [TestCase("userPrincipalName", true)]
    [TestCase("UserPrincipalName", true)]
    [TestCase("servicePrincipalName", true)]
    [TestCase("mail", true)]
    [TestCase("proxyAddresses", true)]
    [TestCase("sAMAccountName", false)]
    [TestCase("uid", false)]
    [TestCase("employeeID", false)]
    public void IsForestWideAttribute_ReturnsWhetherTheAttributeIsUniqueAcrossTheForest(string attributeName, bool expected)
    {
        Assert.That(LdapConnectorUniquenessProbe.IsForestWideAttribute(attributeName), Is.EqualTo(expected));
    }

    [TestCase(false, 3268)]
    [TestCase(true, 3269)]
    public void GlobalCatalogPort_FollowsTheSecureConnectionSetting(bool useSecureConnection, int expected)
    {
        Assert.That(LdapConnectorUniquenessProbe.GlobalCatalogPort(useSecureConnection), Is.EqualTo(expected));
    }

    /// <summary>
    /// The Connection Timeout bounds each address a name resolves to (#2003), so an ordinary connection still reaches a
    /// domain controller that answers after one that does not. The Global Catalog cannot afford that: it has a third of
    /// the probe's thirty seconds, and the LDAP client would then wait about two minutes on the silent address before
    /// trying the next. So the probe's check gives up at the first address that does not answer, and the probe falls
    /// back to the domain within its budget.
    /// </summary>
    [Test]
    public void EnsureAcceptsConnections_FailOnUnansweredAddress_GivesUpAtAnAddressThatDoesNotAnswer()
    {
        using var listener = Listen(out var port);
        var addresses = new[] { IPAddress.Parse(BlackHoledAddress), IPAddress.Loopback };
        var stopwatch = Stopwatch.StartNew();

        LdapException? thrown = null;
        try
        {
            LdapConnectorUtilities.EnsureAcceptsConnections("gc.corp.local", addresses, port, TimeSpan.FromSeconds(1), Logger(), failOnUnansweredAddress: true);
        }
        catch (LdapException ex)
        {
            thrown = ex;
        }

        stopwatch.Stop();

        // Where the black-holed address is refused as unreachable rather than dropped, the LDAP client would not wait on
        // it either, so passing over it to the address that accepts is right.
        if (thrown == null)
            Assert.Ignore("This network refuses the black-holed address outright, so a silent address cannot be exercised here.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown.ErrorCode, Is.EqualTo(81));
            Assert.That(thrown.Message, Does.Contain(BlackHoledAddress), "the failure names the address that did not answer");
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        }
    }

    /// <summary>
    /// A refused address costs the LDAP client nothing, so it does not stop the Global Catalog being used.
    /// </summary>
    [Test]
    public void EnsureAcceptsConnections_FailOnUnansweredAddress_PassesOverARefusedAddress()
    {
        using var listener = Listen(out var port);
        var addresses = new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback };

        Assert.That(() => LdapConnectorUtilities.EnsureAcceptsConnections("gc.corp.local", addresses, port, TimeSpan.FromSeconds(5), Logger(), failOnUnansweredAddress: true),
            Throws.Nothing);
    }

    [Test]
    public void LdapConnector_GetSettings_OffersAnOptionalGlobalCatalogServer()
    {
        var setting = new LdapConnector().GetSettings().SingleOrDefault(s => s.Name == "Global Catalog Server");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(setting, Is.Not.Null);
            Assert.That(setting!.Required, Is.False);
            Assert.That(setting!.Type, Is.EqualTo(ConnectedSystemSettingType.String));
            Assert.That(setting!.Category, Is.EqualTo(ConnectedSystemSettingCategory.Connectivity));
        }
    }

    // ---- Routing ----

    /// <summary>
    /// An attribute unique only within a domain never reads the forest's layout or opens a Global Catalog: the cost of
    /// #1940 falls only on the attributes it is for.
    /// </summary>
    [Test]
    public async Task ProbeAsync_AttributeNotForestWide_SearchesThePartitionAloneAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true).Holding(Root, "asmith");
        var globalCatalog = new FakeGlobalCatalog();

        var result = await Probe(directory, globalCatalog).ProbeAsync(Request(["jbloggs"], "asmith", "sAMAccountName"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(directory.Requests.Select(r => r.DistinguishedName), Is.EqualTo(new[] { Root }));
            Assert.That(globalCatalog.OpenedServers, Is.Empty);
        }
    }

    /// <summary>
    /// mail is forest-wide in Active Directory and nothing of the kind in OpenLDAP, where it is the attribute a
    /// partition search exists for.
    /// </summary>
    [Test]
    public async Task ProbeAsync_ForestWideNameOutsideActiveDirectory_SearchesThePartitionWithoutACaveatAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true, activeDirectory: false).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog();

        var result = await Probe(directory, globalCatalog, configuredServer: "gc.corp.local").ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local", "mail"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(result.Caveat, Is.Null);
            Assert.That(globalCatalog.OpenedServers, Is.Empty);
        }
    }

    /// <summary>
    /// A forest of one domain is that domain: searching it is already searching everywhere the value must be unique,
    /// so a second connection would only add a way to fail.
    /// </summary>
    [Test]
    public async Task ProbeAsync_SingleDomainForest_SearchesThePartitionWithoutACaveatAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: true).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog();

        var result = await Probe(directory, globalCatalog).ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(result.Caveat, Is.Null);
            Assert.That(globalCatalog.OpenedServers, Is.Empty);
        }
    }

    [Test]
    public async Task ProbeAsync_MultiDomainForestAndTheDomainControllerIsAGlobalCatalog_SearchesTheWholeForestThroughItAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog().Holding("asmith@corp.local", "jbloggs@corp.local");

        var result = await Probe(directory, globalCatalog).ProbeAsync(Request(["jbloggs@corp.local", "jbloggs2@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }), "a value held in another domain is found");
            Assert.That(result.Caveat, Is.Null);
            Assert.That(globalCatalog.OpenedServers, Is.EqualTo(new[] { ConnectedServer }));
            Assert.That(globalCatalog.Requests, Has.Count.EqualTo(1));
            Assert.That(globalCatalog.Requests[0].DistinguishedName, Is.Empty, "the empty base is the whole forest");
            Assert.That(globalCatalog.Requests[0].Scope, Is.EqualTo(SearchScope.Subtree));
            Assert.That(globalCatalog.Requests[0].Filter, Is.EqualTo("(|(userPrincipalName=jbloggs@corp.local)(userPrincipalName=jbloggs2@corp.local)(userPrincipalName=asmith@corp.local))"));
            Assert.That(globalCatalog.Requests[0].Attributes, Is.EqualTo(new[] { "userPrincipalName" }));
            Assert.That(globalCatalog.Requests[0].SizeLimit, Is.EqualTo(LdapConnectorUniquenessProbe.SizeLimit));
            Assert.That(directory.Requests.Where(r => r.DistinguishedName == Root), Is.Empty, "the Global Catalog already covers this domain");
        }
    }

    /// <summary>
    /// An administrator who names a Global Catalog has said where forest-wide values are to be looked for; JIM does not
    /// second-guess that from how many domains it can see.
    /// </summary>
    [Test]
    public async Task ProbeAsync_GlobalCatalogServerSet_SearchesThroughItEvenInASingleDomainForestAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: false).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog().Holding("asmith@corp.local");

        await Probe(directory, globalCatalog, configuredServer: "gc.corp.local").ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        Assert.That(globalCatalog.OpenedServers, Is.EqualTo(new[] { "gc.corp.local" }));
    }

    [Test]
    public async Task ProbeAsync_MultiDomainForestWithNoGlobalCatalog_SearchesThePartitionAndSaysWhatItMissedAsync()
    {
        var directory = new FakeDomainController(domainCount: 3, isGlobalCatalog: false).Holding(Root, "asmith@corp.local", "jbloggs@corp.local");
        var globalCatalog = new FakeGlobalCatalog();

        var result = await Probe(directory, globalCatalog).ProbeAsync(Request(["jbloggs@corp.local", "jbloggs2@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }), "the domain's own answer stands");
            Assert.That(result.Caveat, Does.Contain("userPrincipalName").And.Contain("Global Catalog Server"));
            Assert.That(globalCatalog.OpenedServers, Is.Empty);
        }
    }

    // ---- When the Global Catalog cannot answer ----

    [Test]
    public async Task ProbeAsync_GlobalCatalogUnreachable_FallsBackToThePartitionAndDoesNotTryAgainAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog { OpenThrows = new LdapException(81, "The LDAP server is unavailable.") };
        var probe = Probe(directory, globalCatalog);

        var first = await probe.ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);
        var second = await probe.ProbeAsync(Request(["ada.lovelace@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(first.Caveat, Does.Contain("dc1.corp.local:3268").And.Contain("The LDAP server is unavailable"));
            Assert.That(second.Caveat, Is.EqualTo(first.Caveat), "the same caveat, so the run reports it once");
            Assert.That(globalCatalog.OpenAttempts, Is.EqualTo(1), "an unreachable Global Catalog is not retried per object");
            Assert.That(directory.Requests.Count(r => r.DistinguishedName == Root), Is.EqualTo(2));
        }
    }

    /// <summary>
    /// In a forest of one domain the domain's own search already covers everywhere the value must be unique, so a
    /// Global Catalog the administrator named but JIM could not reach costs nothing, and warning that values in
    /// another domain went unseen would be false.
    /// </summary>
    [Test]
    public async Task ProbeAsync_NamedGlobalCatalogUnreachableInASingleDomainForest_AnswersWithoutACaveatAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: true).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog { OpenThrows = new LdapException(81, "The LDAP server is unavailable.") };

        var result = await Probe(directory, globalCatalog, configuredServer: "gc.corp.local").ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(result.Caveat, Is.Null);
            Assert.That(globalCatalog.OpenAttempts, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ProbeAsync_GlobalCatalogRefusesTheSearch_FallsBackToThePartitionAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog
        {
            SearchThrows = new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.InsufficientAccessRights), "insufficient access rights")
        };

        var result = await Probe(directory, globalCatalog).ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(result.IsFailure, Is.False, "the Connected System itself could still be searched");
            Assert.That(result.Caveat, Does.Contain("InsufficientAccessRights"));
        }
    }

    [Test]
    public async Task ProbeAsync_AttributeNotReplicatedToTheGlobalCatalog_SearchesThePartitionWithoutOpeningItAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true, inPartialAttributeSet: false).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog();

        var result = await Probe(directory, globalCatalog).ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(result.Caveat, Does.Contain("not replicated to the Global Catalog"));
            Assert.That(globalCatalog.OpenedServers, Is.Empty);
        }
    }

    /// <summary>
    /// The control-value check holds on the Global Catalog too: one that cannot see a value JIM knows is in this very
    /// domain proves nothing by returning nothing, so the partition answers instead.
    /// </summary>
    [Test]
    public async Task ProbeAsync_GlobalCatalogDoesNotReturnTheControl_FallsBackAndStopsAskingItForThatAttributeAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog();
        var probe = Probe(directory, globalCatalog);

        var first = await probe.ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);
        await probe.ProbeAsync(Request(["ada.lovelace@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound }));
            Assert.That(first.Reason, Is.Null);
            Assert.That(first.Caveat, Does.Contain("did not return"));
            Assert.That(globalCatalog.Requests, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task ProbeAsync_GlobalCatalogSizeLimitExceeded_IsUndeterminedAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog
        {
            SearchThrows = new DirectoryOperationException(LdapTestResponses.Create<SearchResponse>(ResultCode.SizeLimitExceeded), "size limit exceeded")
        };

        var result = await Probe(directory, globalCatalog).ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.IsFailure, Is.False);
            Assert.That(result.Reason, Does.Contain("not unique"));
        }
    }

    // ---- Email addresses and proxyAddresses ----

    /// <summary>
    /// In Active Directory an address is in use if any mailbox, group or contact routes mail for it, and Exchange keeps
    /// those addresses in proxyAddresses (<c>SMTP:</c> for the primary, <c>smtp:</c> for the rest), not only in mail.
    /// A secondary alias on someone else's mailbox is invisible to a search of mail alone.
    /// </summary>
    [Test]
    public async Task ProbeAsync_MailInActiveDirectory_FindsAnAddressHeldAsAnotherMailboxsAliasAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: true)
            .Holding(Root, "asmith@corp.local")
            .HoldingProxyAddresses(Root, "SMTP:joe.b@corp.local", "smtp:jbloggs@corp.local");
        var globalCatalog = new FakeGlobalCatalog();

        var result = await Probe(directory, globalCatalog).ProbeAsync(Request(["jbloggs@corp.local", "jbloggs2@corp.local"], "asmith@corp.local", "mail"), [Root], CancellationToken.None);

        var search = directory.Requests.Single(r => r.DistinguishedName == Root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
            Assert.That(search.Filter, Is.EqualTo("(|(mail=jbloggs@corp.local)(proxyAddresses=smtp:jbloggs@corp.local)(mail=jbloggs2@corp.local)(proxyAddresses=smtp:jbloggs2@corp.local)(mail=asmith@corp.local)(proxyAddresses=smtp:asmith@corp.local))"));
            Assert.That(search.Attributes, Is.EqualTo(new[] { "mail", "proxyAddresses" }));
        }
    }

    [Test]
    public async Task ProbeAsync_MailInActiveDirectory_FindsAnAddressHeldAsAPrimarySmtpAddressAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: true)
            .Holding(Root, "asmith@corp.local")
            .HoldingProxyAddresses(Root, "SMTP:jbloggs@corp.local");

        var result = await Probe(directory, new FakeGlobalCatalog()).ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local", "mail"), [Root], CancellationToken.None);

        Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
    }

    /// <summary>
    /// proxyAddresses also holds addresses that are not email addresses (<c>SIP:</c>, <c>X500:</c>); one of those
    /// matching a candidate's text does not make the email address taken.
    /// </summary>
    [Test]
    public async Task ProbeAsync_MailInActiveDirectory_IgnoresProxyAddressesThatAreNotEmailAddressesAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: true)
            .Holding(Root, "asmith@corp.local")
            .HoldingProxyAddresses(Root, "SMTP:alias@corp.local", "sip:jbloggs@corp.local");

        var result = await Probe(directory, new FakeGlobalCatalog()).ProbeAsync(Request(["jbloggs@corp.local", "alias@corp.local"], "asmith@corp.local", "mail"), [Root], CancellationToken.None);

        Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound, UniquenessProbeOutcome.Found }));
    }

    [Test]
    public async Task ProbeAsync_MailOutsideActiveDirectory_SearchesMailAloneAsync()
    {
        var directory = new FakeDomainController(domainCount: 1, isGlobalCatalog: false, activeDirectory: false).Holding(Root, "asmith@corp.local");

        await Probe(directory, new FakeGlobalCatalog()).ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local", "mail"), [Root], CancellationToken.None);

        var search = directory.Requests.Single(r => r.DistinguishedName == Root);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(search.Filter, Is.EqualTo("(|(mail=jbloggs@corp.local)(mail=asmith@corp.local))"));
            Assert.That(search.Attributes, Is.EqualTo(new[] { "mail" }));
        }
    }

    [Test]
    public async Task ProbeAsync_MailThroughTheGlobalCatalog_FindsAnAliasInAnotherDomainAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog()
            .Holding("asmith@corp.local")
            .HoldingProxyAddresses("SMTP:joe.b@emea.corp.local", "smtp:jbloggs@corp.local");

        var result = await Probe(directory, globalCatalog).ProbeAsync(Request(["jbloggs@corp.local"], "asmith@corp.local", "mail"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found }));
            Assert.That(globalCatalog.Requests.Single().Attributes, Is.EqualTo(new[] { "mail", "proxyAddresses" }));
        }
    }

    // ---- Cost ----

    [Test]
    public async Task ProbeAsync_SeveralBatches_ReadsTheForestOnceAndOpensTheGlobalCatalogOnceAsync()
    {
        var directory = new FakeDomainController(domainCount: 2, isGlobalCatalog: true).Holding(Root, "asmith@corp.local");
        var globalCatalog = new FakeGlobalCatalog().Holding("asmith@corp.local");
        var probe = Probe(directory, globalCatalog);

        foreach (var candidate in new[] { "jbloggs@corp.local", "ada.lovelace@corp.local", "alan.turing@corp.local" })
            await probe.ProbeAsync(Request([candidate], "asmith@corp.local"), [Root], CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(directory.Requests.Count(r => r.Scope == SearchScope.Base), Is.EqualTo(1), "rootDSE");
            Assert.That(directory.Requests.Count(r => r.DistinguishedName == PartitionsContainer), Is.EqualTo(1), "the forest's domains");
            Assert.That(directory.Requests.Count(r => r.DistinguishedName == SchemaNamingContext), Is.EqualTo(1), "the attribute's schema entry");
            Assert.That(globalCatalog.OpenAttempts, Is.EqualTo(1));
            Assert.That(globalCatalog.Requests, Has.Count.EqualTo(3));
        }
    }

    private const string BlackHoledAddress = "10.255.255.1";

    private static ILogger Logger() => new LoggerConfiguration().CreateLogger();

    private static TcpListener Listen(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static UniquenessProbeRequest Request(IReadOnlyList<string> candidates, string? controlValue, string attributeName = "userPrincipalName") => new()
    {
        ObjectTypeName = "user",
        AttributeName = attributeName,
        Candidates = candidates,
        ControlValue = controlValue
    };

    private static LdapConnectorUniquenessProbe Probe(FakeDomainController directory, FakeGlobalCatalog globalCatalog, string? configuredServer = null) =>
        new(directory.Executor, new LoggerConfiguration().CreateLogger(),
            new LdapGlobalCatalogProbeOptions(configuredServer, ConnectedServer, LdapConnectorUniquenessProbe.GlobalCatalogPort(false), globalCatalog.Open));

    /// <summary>
    /// The domain controller the probe connection is bound to: answers the rootDSE, the forest's domain crossRefs and
    /// the attribute's schema entry, and the partition search from the values it holds.
    /// </summary>
    private sealed class FakeDomainController
    {
        private readonly int _domainCount;
        private readonly bool _isGlobalCatalog;
        private readonly bool _activeDirectory;
        private readonly bool _inPartialAttributeSet;
        private readonly Dictionary<string, List<string>> _held = [];
        private readonly Dictionary<string, List<string[]>> _proxyEntries = [];

        public FakeDomainController(int domainCount, bool isGlobalCatalog, bool activeDirectory = true, bool inPartialAttributeSet = true)
        {
            _domainCount = domainCount;
            _isGlobalCatalog = isGlobalCatalog;
            _activeDirectory = activeDirectory;
            _inPartialAttributeSet = inPartialAttributeSet;

            var executor = new Mock<ILdapOperationExecutor>();
            executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
                .Returns((DirectoryRequest r, TimeSpan _) => Answer((SearchRequest)r));
            executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>()))
                .Returns((DirectoryRequest r) => Answer((SearchRequest)r));
            Executor = executor.Object;
        }

        public ILdapOperationExecutor Executor { get; }

        public List<SearchRequest> Requests { get; } = [];

        public FakeDomainController Holding(string partition, params string[] values)
        {
            _held[partition] = [.. values];
            return this;
        }

        /// <summary>
        /// Adds one entry under <paramref name="partition"/> whose proxyAddresses holds <paramref name="proxyAddresses"/>.
        /// </summary>
        public FakeDomainController HoldingProxyAddresses(string partition, params string[] proxyAddresses)
        {
            if (!_proxyEntries.TryGetValue(partition, out var entries))
                _proxyEntries[partition] = entries = [];
            entries.Add(proxyAddresses);
            return this;
        }

        private SearchResponse Answer(SearchRequest request)
        {
            Requests.Add(request);

            if (request.Scope == SearchScope.Base)
                return RootDse();

            if (request.DistinguishedName == PartitionsContainer)
            {
                // Every domain, plus the Configuration and Schema partitions, which are not domains (systemFlags 1).
                return LdapTestResponses.SearchResponseWithEntries(Enumerable.Range(1, _domainCount)
                    .Select(i => LdapTestResponses.Entry($"CN=DOMAIN{i},{PartitionsContainer}", ("nCName", $"DC=domain{i},DC=local"), ("systemFlags", "3")))
                    .Append(LdapTestResponses.Entry($"CN=Enterprise Configuration,{PartitionsContainer}", ("nCName", ConfigurationNamingContext), ("systemFlags", "1")))
                    .Append(LdapTestResponses.Entry($"CN=Enterprise Schema,{PartitionsContainer}", ("nCName", SchemaNamingContext), ("systemFlags", "1")))
                    .ToArray());
            }

            if (request.DistinguishedName == SchemaNamingContext)
            {
                return LdapTestResponses.SearchResponseWith("CN=User-Principal-Name," + SchemaNamingContext,
                    ("isMemberOfPartialAttributeSet", _inPartialAttributeSet ? "TRUE" : "FALSE"));
            }

            var attribute = request.Attributes[0]!;
            var values = _held.TryGetValue(request.DistinguishedName, out var held) ? held : [];
            var proxyEntries = _proxyEntries.TryGetValue(request.DistinguishedName, out var proxies) ? proxies : [];
            return LdapTestResponses.SearchResponseWithEntries(values
                .Where(v => request.Filter.ToString()!.Contains($"({attribute}={v})", StringComparison.OrdinalIgnoreCase))
                .Select(v => LdapTestResponses.Entry($"CN={v},{request.DistinguishedName}", (attribute, v)))
                .Concat(MatchingProxyEntries(request, proxyEntries, request.DistinguishedName))
                .ToArray());
        }

        private SearchResponse RootDse()
        {
            var capabilities = _activeDirectory ? new[] { LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_OID } : ["1.3.6.1.4.1.4203.1.5.1"];
            return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues("",
                ("supportedCapabilities", capabilities),
                ("isGlobalCatalogReady", [_isGlobalCatalog ? "TRUE" : "FALSE"]),
                ("configurationNamingContext", [ConfigurationNamingContext]),
                ("schemaNamingContext", [SchemaNamingContext])));
        }
    }

    /// <summary>
    /// The Global Catalog the probe opens on demand: records where it was asked to connect, and answers the forest-wide
    /// search from the values it holds.
    /// </summary>
    private sealed class FakeGlobalCatalog
    {
        private readonly List<string> _held = [];
        private readonly List<string[]> _proxyEntries = [];

        public Exception? OpenThrows { get; init; }

        public Exception? SearchThrows { get; init; }

        public int OpenAttempts { get; private set; }

        public List<string> OpenedServers { get; } = [];

        public List<SearchRequest> Requests { get; } = [];

        public FakeGlobalCatalog Holding(params string[] values)
        {
            _held.AddRange(values);
            return this;
        }

        /// <summary>
        /// Adds one entry, in another domain, whose proxyAddresses holds <paramref name="proxyAddresses"/>.
        /// </summary>
        public FakeGlobalCatalog HoldingProxyAddresses(params string[] proxyAddresses)
        {
            _proxyEntries.Add(proxyAddresses);
            return this;
        }

        public ILdapOperationExecutor Open(string server, TimeSpan timeout)
        {
            OpenAttempts++;
            if (OpenThrows != null)
                throw OpenThrows;

            OpenedServers.Add(server);
            var executor = new Mock<ILdapOperationExecutor>();
            executor.Setup(x => x.SendRequest(It.IsAny<DirectoryRequest>(), It.IsAny<TimeSpan>()))
                .Returns((DirectoryRequest r, TimeSpan _) => Answer((SearchRequest)r));
            return executor.Object;
        }

        private SearchResponse Answer(SearchRequest request)
        {
            Requests.Add(request);
            if (SearchThrows != null)
                throw SearchThrows;

            var attribute = request.Attributes[0]!;
            return LdapTestResponses.SearchResponseWithEntries(_held
                .Where(v => request.Filter.ToString()!.Contains($"({attribute}={v})", StringComparison.OrdinalIgnoreCase))
                .Select(v => LdapTestResponses.Entry($"CN={v},DC=elsewhere,DC=local", (attribute, v)))
                .Concat(MatchingProxyEntries(request, _proxyEntries, "DC=elsewhere,DC=local"))
                .ToArray());
        }
    }

    /// <summary>
    /// The entries whose proxyAddresses the filter asks for, each returned with all of its proxy addresses, as a
    /// directory returns them. Matching ignores case, as Active Directory's does for this attribute, so a filter for
    /// <c>smtp:x</c> finds a primary <c>SMTP:x</c>.
    /// </summary>
    private static IEnumerable<SearchResultEntry> MatchingProxyEntries(SearchRequest request, List<string[]> entries, string container) =>
        entries
            .Where(e => e.Any(v => request.Filter.ToString()!.Contains($"(proxyAddresses={v})", StringComparison.OrdinalIgnoreCase)))
            .Select((e, n) => LdapTestResponses.EntryWithValues($"CN=Mailbox {n},{container}", ("proxyAddresses", e)));
}
