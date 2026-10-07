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
/// The LDAP Connector's live uniqueness probe of forest-wide attributes through a Global Catalog (#1940). An attribute
/// unique across an Active Directory forest (userPrincipalName above all) is searched through a Global Catalog when
/// the forest has more than one domain, or when the administrator names one; otherwise, or when the Global Catalog
/// cannot be searched, the Connected System's own partition is searched and the answer says what it could not reach.
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
    /// A firewall that drops Global Catalog traffic rather than refusing it leaves the platform LDAP client waiting on
    /// the operating system's TCP retries (over two minutes, measured against a black-holed address), whatever its own
    /// timeout says; the probe's whole batch gets thirty seconds. Whichever way an unroutable address fails here
    /// (dropped, or refused as unreachable), it has to fail as a directory fault, and inside the time allowed.
    /// </summary>
    [Test]
    public void EnsureAcceptsConnections_ServerDoesNotAnswer_FailsAsADirectoryFaultWithinTheTimeout()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Assert.That(() => LdapConnectorUtilities.EnsureAcceptsConnections("10.255.255.1", 3268, TimeSpan.FromSeconds(1)),
            Throws.InstanceOf<LdapException>());
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void EnsureAcceptsConnections_PortRefused_FailsAsADirectoryFault()
    {
        // Bind and release a loopback port, so nothing is listening on it.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.That(() => LdapConnectorUtilities.EnsureAcceptsConnections("127.0.0.1", port, TimeSpan.FromSeconds(5)),
            Throws.InstanceOf<LdapException>());
    }

    [Test]
    public void EnsureAcceptsConnections_Listening_Returns()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            Assert.That(() => LdapConnectorUtilities.EnsureAcceptsConnections("127.0.0.1", port, TimeSpan.FromSeconds(5)), Throws.Nothing);
        }
        finally
        {
            listener.Stop();
        }
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
            return LdapTestResponses.SearchResponseWithEntries(values
                .Where(v => request.Filter.ToString()!.Contains($"({attribute}={v})", StringComparison.OrdinalIgnoreCase))
                .Select(v => LdapTestResponses.Entry($"CN={v},{request.DistinguishedName}", (attribute, v)))
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
                .ToArray());
        }
    }
}
