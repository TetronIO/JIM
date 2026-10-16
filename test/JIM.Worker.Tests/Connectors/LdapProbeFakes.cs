// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using JIM.Connectors.LDAP;
using Moq;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The forest the LDAP probe fakes stand for: a domain under <see cref="Root"/>, its Configuration and Schema partitions,
/// and the domain controller the probe connection is bound to.
/// </summary>
internal static class LdapProbeForest
{
    public const string Root = "DC=corp,DC=local";
    public const string SecondRoot = "DC=emea,DC=corp,DC=local";
    public const string ConfigurationNamingContext = "CN=Configuration,DC=corp,DC=local";
    public const string SchemaNamingContext = "CN=Schema,CN=Configuration,DC=corp,DC=local";
    public const string PartitionsContainer = "CN=Partitions,CN=Configuration,DC=corp,DC=local";
    public const string ConnectedServer = "dc1.corp.local";

    /// <summary>
    /// The attributes a probe search asks for besides <c>proxyAddresses</c>, which rides along with <c>mail</c>.
    /// </summary>
    public static IEnumerable<string> ProbedAttributes(SearchRequest request) =>
        request.Attributes.Cast<string>().Where(a => !string.Equals(a, "proxyAddresses", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The entries holding the values a probe search asks for: one per (attribute, value) the filter asserts, from
    /// <paramref name="held"/>, where a value held under no attribute answers whichever attribute is asked for.
    /// </summary>
    public static IEnumerable<SearchResultEntry> MatchingEntries(SearchRequest request, IEnumerable<(string? Attribute, string Value)> held, string container)
    {
        var filter = request.Filter.ToString()!;
        return ProbedAttributes(request).SelectMany(attribute => held
            .Where(h => (h.Attribute == null || string.Equals(h.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
                && filter.Contains($"({attribute}={h.Value})", StringComparison.OrdinalIgnoreCase))
            .Select(h => LdapTestResponses.Entry($"CN={h.Value},{container}", (attribute, h.Value))));
    }

    /// <summary>
    /// The entries whose proxyAddresses the filter asks for, each returned with all of its proxy addresses, as a
    /// directory returns them. Matching ignores case, as Active Directory's does for this attribute, so a filter for
    /// <c>smtp:x</c> finds a primary <c>SMTP:x</c>.
    /// </summary>
    public static IEnumerable<SearchResultEntry> MatchingProxyEntries(SearchRequest request, List<string[]> entries, string container) =>
        entries
            .Where(e => e.Any(v => request.Filter.ToString()!.Contains($"(proxyAddresses={v})", StringComparison.OrdinalIgnoreCase)))
            .Select((e, n) => LdapTestResponses.EntryWithValues($"CN=Mailbox {n},{container}", ("proxyAddresses", e)));

    /// <summary>
    /// Whether a probe search carries more than one attribute's assertions.
    /// </summary>
    public static bool IsCombined(SearchRequest request) => ProbedAttributes(request).Count() > 1;
}

/// <summary>
/// The domain controller the probe connection is bound to: answers the rootDSE, the forest's domain crossRefs and the
/// attribute's schema entry, and each partition search from the values it holds, for every attribute the search asks
/// for.
/// </summary>
internal sealed class FakeDomainController
{
    private readonly int _domainCount;
    private readonly bool _isGlobalCatalog;
    private readonly bool _activeDirectory;
    private readonly bool _inPartialAttributeSet;
    private readonly Dictionary<string, List<(string? Attribute, string Value)>> _held = [];
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

    /// <summary>
    /// Thrown by every partition search carrying more than one attribute's assertions; single-attribute searches answer.
    /// </summary>
    public Exception? CombinedSearchThrows { get; init; }

    /// <summary>
    /// Thrown by every partition search.
    /// </summary>
    public Exception? SearchThrows { get; init; }

    /// <summary>
    /// The partition searches made, in order (not the rootDSE, the crossRefs or the schema).
    /// </summary>
    public IEnumerable<SearchRequest> PartitionSearches => Requests.Where(r => r.Scope == SearchScope.Subtree);

    /// <summary>
    /// Values held under <paramref name="partition"/> that answer whichever attribute a search asks for.
    /// </summary>
    public FakeDomainController Holding(string partition, params string[] values) =>
        HoldingIn(partition, null, values);

    /// <summary>
    /// Values held under <paramref name="partition"/> in <paramref name="attribute"/> only.
    /// </summary>
    public FakeDomainController HoldingIn(string partition, string? attribute, params string[] values)
    {
        if (!_held.TryGetValue(partition, out var held))
            _held[partition] = held = [];
        held.AddRange(values.Select(v => (attribute, v)));
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

        if (request.DistinguishedName == LdapProbeForest.PartitionsContainer)
        {
            // Every domain, plus the Configuration and Schema partitions, which are not domains (systemFlags 1).
            return LdapTestResponses.SearchResponseWithEntries(Enumerable.Range(1, _domainCount)
                .Select(i => LdapTestResponses.Entry($"CN=DOMAIN{i},{LdapProbeForest.PartitionsContainer}", ("nCName", $"DC=domain{i},DC=local"), ("systemFlags", "3")))
                .Append(LdapTestResponses.Entry($"CN=Enterprise Configuration,{LdapProbeForest.PartitionsContainer}", ("nCName", LdapProbeForest.ConfigurationNamingContext), ("systemFlags", "1")))
                .Append(LdapTestResponses.Entry($"CN=Enterprise Schema,{LdapProbeForest.PartitionsContainer}", ("nCName", LdapProbeForest.SchemaNamingContext), ("systemFlags", "1")))
                .ToArray());
        }

        if (request.DistinguishedName == LdapProbeForest.SchemaNamingContext)
        {
            return LdapTestResponses.SearchResponseWith("CN=User-Principal-Name," + LdapProbeForest.SchemaNamingContext,
                ("isMemberOfPartialAttributeSet", _inPartialAttributeSet ? "TRUE" : "FALSE"));
        }

        if (SearchThrows != null)
            throw SearchThrows;

        if (CombinedSearchThrows != null && LdapProbeForest.IsCombined(request))
            throw CombinedSearchThrows;

        var held = _held.TryGetValue(request.DistinguishedName, out var values) ? values : [];
        var proxyEntries = _proxyEntries.TryGetValue(request.DistinguishedName, out var proxies) ? proxies : [];
        return LdapTestResponses.SearchResponseWithEntries(LdapProbeForest.MatchingEntries(request, held, request.DistinguishedName)
            .Concat(LdapProbeForest.MatchingProxyEntries(request, proxyEntries, request.DistinguishedName))
            .ToArray());
    }

    private SearchResponse RootDse()
    {
        var capabilities = _activeDirectory ? new[] { LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_OID } : ["1.3.6.1.4.1.4203.1.5.1"];
        return LdapTestResponses.SearchResponseWithEntries(LdapTestResponses.EntryWithValues("",
            ("supportedCapabilities", capabilities),
            ("isGlobalCatalogReady", [_isGlobalCatalog ? "TRUE" : "FALSE"]),
            ("configurationNamingContext", [LdapProbeForest.ConfigurationNamingContext]),
            ("schemaNamingContext", [LdapProbeForest.SchemaNamingContext])));
    }
}

/// <summary>
/// The Global Catalog the probe opens on demand: records where it was asked to connect, and answers each forest-wide
/// search from the values it holds, for every attribute the search asks for.
/// </summary>
internal sealed class FakeGlobalCatalog
{
    private readonly List<(string? Attribute, string Value)> _held = [];
    private readonly List<string[]> _proxyEntries = [];

    public Exception? OpenThrows { get; init; }

    public Exception? SearchThrows { get; init; }

    /// <summary>
    /// Thrown by every search carrying more than one attribute's assertions; single-attribute searches answer.
    /// </summary>
    public Exception? CombinedSearchThrows { get; init; }

    public int OpenAttempts { get; private set; }

    public List<string> OpenedServers { get; } = [];

    public List<SearchRequest> Requests { get; } = [];

    /// <summary>
    /// Values held in another domain that answer whichever attribute a search asks for.
    /// </summary>
    public FakeGlobalCatalog Holding(params string[] values) => HoldingIn(null, values);

    /// <summary>
    /// Values held in another domain in <paramref name="attribute"/> only.
    /// </summary>
    public FakeGlobalCatalog HoldingIn(string? attribute, params string[] values)
    {
        _held.AddRange(values.Select(v => (attribute, v)));
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

        if (CombinedSearchThrows != null && LdapProbeForest.IsCombined(request))
            throw CombinedSearchThrows;

        return LdapTestResponses.SearchResponseWithEntries(LdapProbeForest.MatchingEntries(request, _held, "DC=elsewhere,DC=local")
            .Concat(LdapProbeForest.MatchingProxyEntries(request, _proxyEntries, "DC=elsewhere,DC=local"))
            .ToArray());
    }
}
