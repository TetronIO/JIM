// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.LDAP;

/// <summary>
/// Searches a directory for generated values already in use (Unique Value Generation, #242, release 3, plan Phase 7
/// item 3). One search per partition root, from the root down, with one OR filter carrying the object's candidates
/// and the control value, asking for the probed attribute and nothing else. Several attributes of one object are
/// probed with one search per route rather than one per attribute (Generated Value Forms, #2023; see
/// <see cref="ProbeAttributesAsync"/>).
/// <para>
/// The partition root, never the selected containers: a value held by an account outside JIM's import scope is
/// exactly the collision the probe exists to catch, since JIM already knows about everything inside it.
/// </para>
/// <para>
/// An attribute Active Directory holds unique across the whole forest rather than one domain (see
/// <see cref="IsForestWideAttribute"/>) is searched through a Global Catalog instead, from the empty base, which is
/// every domain of the forest at once (#1940): when the administrator names one, or when the forest has more than one
/// domain and the domain controller the probe reached is one. When a Global Catalog is needed and cannot be searched,
/// the partition is searched as before and the result carries a caveat saying what it could not reach, so the run
/// says so rather than presenting a domain's answer as the forest's.
/// </para>
/// <para>
/// In Active Directory and Samba AD a probe of <c>mail</c> also searches <c>proxyAddresses</c> for the same addresses
/// with the <c>smtp:</c> prefix, because an address held as another object's alias is in use too, and is invisible to a
/// search of mail alone.
/// </para>
/// </summary>
internal sealed partial class LdapConnectorUniquenessProbe
{
    /// <summary>
    /// The most entries one search may return. A probed attribute is meant to be unique, so a batch of at most ten
    /// candidates and a control should match a handful of entries; anything near this means the attribute is not
    /// unique in the directory, and the answer would be cut short rather than trusted.
    /// </summary>
    internal const int SizeLimit = 100;

    /// <summary>
    /// The crossRef systemFlags bit marking a domain's naming context (FLAG_CR_NTDS_DOMAIN), as opposed to the
    /// Configuration, Schema and application partitions, which a forest has however many domains it holds.
    /// </summary>
    private const int CrossRefDomainFlag = 0x2;

    /// <summary>
    /// Where Active Directory, with Exchange, keeps every address an object receives mail at: <c>SMTP:</c> marks the
    /// primary address and <c>smtp:</c> the rest, beside other kinds such as <c>SIP:</c> and <c>X500:</c>.
    /// </summary>
    private const string ProxyAddressesAttribute = "proxyAddresses";

    private const string SmtpProxyAddressPrefix = "smtp:";

    /// <summary>
    /// Naming attributes whose values are unique only within their container, plus the Distinguished Name itself.
    /// Two "John Smith" entries in different OUs are not a collision, so a partition-wide search on these would
    /// reject values that are free and add needless suffixes (plan Phase 7 item 3). <c>uid</c> is deliberately not
    /// here: where it names entries it is still conventionally unique across the directory, and it is the attribute
    /// a brownfield OpenLDAP estate most needs probed.
    /// </summary>
    private static readonly HashSet<string> ContainerScopedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "distinguishedName", "dn", "entryDN", "cn", "ou", "o", "dc", "c", "l", "st"
    };

    /// <summary>
    /// Attributes unique across an Active Directory forest rather than one domain (#1940). Active Directory itself
    /// enforces userPrincipalName and servicePrincipalName forest-wide; mail and proxyAddresses route mail, so two
    /// accounts anywhere in one forest holding the same address is a collision whether or not a mail system is there
    /// to refuse it. Not every attribute in the Global Catalog's partial attribute set belongs here: sAMAccountName is
    /// replicated to it but unique per domain, and searching the forest for it would reject values that are free.
    /// </summary>
    private static readonly HashSet<string> ForestWideAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "userPrincipalName", "servicePrincipalName", "mail", "proxyAddresses"
    };

    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;
    private readonly LdapGlobalCatalogProbeOptions? _globalCatalogOptions;

    /// <summary>
    /// The forest as the probe connection sees it; read on the first forest-wide probe and kept for the session.
    /// </summary>
    private ForestLayout? _forest;

    /// <summary>
    /// Whether each forest-wide attribute is in the Global Catalog's partial attribute set; null where its schema
    /// entry could not be read.
    /// </summary>
    private readonly Dictionary<string, bool?> _replicatedToGlobalCatalog = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The caveat for each attribute the Global Catalog cannot answer for (it did not return the control value), so
    /// the rest of the session searches the partition for it without asking the Global Catalog again.
    /// </summary>
    private readonly Dictionary<string, string> _attributeCaveats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Global Catalog connection's searcher, once opened.
    /// </summary>
    private ILdapOperationExecutor? _globalCatalog;

    /// <summary>
    /// Why the Global Catalog cannot be used for the rest of the session (it could not be reached, or refused or
    /// failed a search), as a clause for <see cref="ForestCaveat"/>; null while it can be.
    /// </summary>
    private string? _globalCatalogUnavailable;

    internal LdapConnectorUniquenessProbe(ILdapOperationExecutor executor, ILogger logger, LdapGlobalCatalogProbeOptions? globalCatalog = null)
    {
        _executor = executor;
        _logger = logger;
        _globalCatalogOptions = globalCatalog;
    }

    /// <summary>
    /// Whether a partition-wide search for <paramref name="attributeName"/> means anything (see
    /// <see cref="ContainerScopedAttributes"/>).
    /// </summary>
    internal static bool CanProbeAttribute(string attributeName) =>
        !string.IsNullOrWhiteSpace(attributeName) && !ContainerScopedAttributes.Contains(attributeName);

    /// <summary>
    /// Whether <paramref name="attributeName"/> is unique across an Active Directory forest rather than one domain,
    /// and so is searched through a Global Catalog where one is needed (see <see cref="ForestWideAttributes"/>).
    /// </summary>
    internal static bool IsForestWideAttribute(string attributeName) => ForestWideAttributes.Contains(attributeName);

    /// <summary>
    /// The port a Global Catalog answers on: 3269 over LDAPS, 3268 otherwise, following the Connected System's own
    /// secure connection setting.
    /// </summary>
    internal static int GlobalCatalogPort(bool useSecureConnection) =>
        useSecureConnection ? LdapConnectorConstants.GLOBAL_CATALOG_SSL_PORT : LdapConnectorConstants.GLOBAL_CATALOG_PORT;

    /// <summary>
    /// Whether <paramref name="attributeName"/> is a plain RFC 4512 descriptor (a letter, then letters, digits and
    /// hyphens) or a numeric OID. The attribute name is not an assertion value, so RFC 4515 escaping does not apply
    /// to it; it must instead be proven harmless before it is put into a filter.
    /// </summary>
    internal static bool IsValidAttributeDescription(string attributeName) =>
        !string.IsNullOrEmpty(attributeName) && AttributeDescriptionPattern().IsMatch(attributeName);

    /// <summary>
    /// The OR filter for one batch: an equality assertion per value, every value escaped per RFC 4515.
    /// </summary>
    internal static string BuildFilter(string attributeName, IEnumerable<string> values) => BuildFilter(attributeName, values, includeProxyAddresses: false);

    /// <summary>
    /// The OR filter for one batch, with a <c>proxyAddresses</c> assertion beside each value's when
    /// <paramref name="includeProxyAddresses"/> (see <see cref="IsEmailAttribute"/>). The directory compares that
    /// attribute without regard to case, so <c>smtp:</c> also finds a primary <c>SMTP:</c> address.
    /// </summary>
    internal static string BuildFilter(string attributeName, IEnumerable<string> values, bool includeProxyAddresses) =>
        AppendAssertions(new StringBuilder("(|"), attributeName, values, includeProxyAddresses).Append(')').ToString();

    /// <summary>
    /// Whether <paramref name="attributeName"/> holds an email address, which in Active Directory may also be held in any
    /// object's <c>proxyAddresses</c>.
    /// </summary>
    internal static bool IsEmailAttribute(string attributeName) => string.Equals(attributeName, "mail", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Searches for the batch, and reports an outcome per candidate (see <see cref="ProbeAttributesAsync"/>, which this
    /// is with one attribute).
    /// </summary>
    internal Task<UniquenessProbeResult> ProbeAsync(UniquenessProbeRequest request, IReadOnlyList<string> searchBases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(Probe([request], searchBases, cancellationToken)[0]);
    }

    /// <summary>
    /// Searches for several attributes' batches of one object at once (Generated Value Forms, #2023), and reports an
    /// outcome per candidate of each: one search per route rather than one per attribute. The attributes searched in
    /// the partitions share one search of each partition root in <paramref name="searchBases"/>; the forest-wide
    /// attributes that need a Global Catalog, where one can be searched, share one search of it.
    /// <para>
    /// Each attribute is answered from its own values only, and against its own control value, so a Global Catalog
    /// blind to one attribute sends that attribute alone to the partitions. A search the attributes cannot complete
    /// together (more entries between them than <see cref="SizeLimit"/>, or a refusal) is made again for each attribute
    /// on its own, which answers each exactly as a search for it alone would have. A directory that cannot be reached
    /// fails every attribute at once instead: asking again for each would only multiply the wait.
    /// </para>
    /// <para>
    /// Never throws for a directory fault: a refused, failed or timed-out search of the partitions is a
    /// <see cref="UniquenessProbeResult.Failed"/> result naming why, so the synchronisation carries on with JIM's own
    /// records, and one of the Global Catalog falls back to the partitions with a <see cref="UniquenessProbeResult.Caveat"/>.
    /// </para>
    /// </summary>
    internal Task<IReadOnlyList<UniquenessProbeResult>> ProbeAttributesAsync(IReadOnlyList<UniquenessProbeRequest> requests, IReadOnlyList<string> searchBases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        return Task.FromResult(Probe(requests, searchBases, cancellationToken));
    }

    private IReadOnlyList<UniquenessProbeResult> Probe(IReadOnlyList<UniquenessProbeRequest> requests, IReadOnlyList<string> searchBases, CancellationToken cancellationToken)
    {
        foreach (var request in requests)
            request.Validate();

        var probes = requests.Select((request, index) => new PendingProbe(index, request)).ToList();
        var throughForest = new List<PendingProbe>();
        var inPartitions = new List<PendingProbe>();
        ForestRoute forestRoute = default;

        foreach (var probe in probes)
        {
            var request = probe.Request;
            if (!IsValidAttributeDescription(request.AttributeName))
            {
                probe.Result = UniquenessProbeResult.Failed(request.Candidates.Count,
                    $"\"{request.AttributeName}\" is not an LDAP attribute name JIM can probe for");
                continue;
            }

            if (searchBases.Count == 0)
            {
                probe.Result = UniquenessProbeResult.Failed(request.Candidates.Count, "No partition is selected, so there is nowhere to probe");
                continue;
            }

            // An email address is in use in Active Directory whether an object holds it as mail or as one of its
            // proxyAddresses, so both are searched; outside Active Directory, mail is an ordinary attribute.
            probe.IncludesProxyAddresses = IsEmailAttribute(request.AttributeName)
                && (_forest ??= ReadForestLayout(ForestTimeout(request.Timeout))).IsActiveDirectory;

            if (_globalCatalogOptions != null && IsForestWideAttribute(request.AttributeName))
            {
                var route = ChooseForestRoute(request);
                probe.Caveat = route.Caveat;

                // The probe opens one Global Catalog a session, so every attribute routed through it shares the route.
                if (route.GlobalCatalog != null)
                {
                    forestRoute = route;
                    throughForest.Add(probe);
                    continue;
                }
            }

            inPartitions.Add(probe);
        }

        if (throughForest.Count > 0)
            SearchGlobalCatalog(forestRoute.GlobalCatalog!, forestRoute.Endpoint!, throughForest, inPartitions, cancellationToken);

        if (inPartitions.Count > 0)
        {
            SearchPartitions([.. inPartitions.OrderBy(p => p.Index)], searchBases, cancellationToken);

            // A forest of one domain is that domain, so its search missed nothing, whatever kept a Global Catalog the
            // administrator named out of reach: that is logged where it happened, and the answer is complete.
            if (_forest?.DomainCount != 1)
            {
                foreach (var probe in inPartitions.Where(p => p.Caveat != null))
                    probe.Result = probe.Result!.WithCaveat(probe.Caveat!);
            }
        }

        return [.. probes.Select(p => p.Result!)];
    }

    /// <summary>
    /// Searches every partition root for <paramref name="group"/> at once, and answers each probe in it (the release 3
    /// search, unchanged by #1940, for a group of one).
    /// </summary>
    private void SearchPartitions(IReadOnlyList<PendingProbe> group, IReadOnlyList<string> searchBases, CancellationToken cancellationToken)
    {
        var timeout = group.Max(p => p.Request.Timeout);
        var search = BuildSearch(group);
        var responses = new List<SearchResponse>(searchBases.Count);

        foreach (var searchBase in searchBases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SearchResponse response;
            try
            {
                response = (SearchResponse)_executor.SendRequest(CreateSearchRequest(searchBase, search, timeout), timeout);
            }
            catch (DirectoryOperationException ex) when (group.Count > 1)
            {
                // A limit, or a refusal, the attributes meet together says nothing about any one of them.
                _logger.Warning(ex, "LdapConnectorUniquenessProbe: The search of {SearchBase} for {Attributes} values at once did not complete ({ResultCode}); searching for each attribute on its own.",
                    LogSanitiser.Sanitise(searchBase), LogSanitiser.Sanitise(DescribeAttributes(group)), ex.Response?.ResultCode.ToString() ?? "an unknown result");
                foreach (var probe in group)
                    SearchPartitions([probe], searchBases, cancellationToken);
                return;
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
            {
                _logger.Warning("LdapConnectorUniquenessProbe: More than {SizeLimit} entries under {SearchBase} hold {Attribute} values in this batch; the attribute is not unique there, so the probe cannot answer for it.",
                    SizeLimit, LogSanitiser.Sanitise(searchBase), LogSanitiser.Sanitise(group[0].Request.AttributeName));
                group[0].Result = NotUnique(group[0].Request);
                return;
            }
            catch (DirectoryOperationException ex)
            {
                var resultCode = ex.Response?.ResultCode.ToString() ?? "an unknown result";
                _logger.Warning(ex, "LdapConnectorUniquenessProbe: The search of {SearchBase} for {Attribute} values was refused with {ResultCode}.",
                    LogSanitiser.Sanitise(searchBase), LogSanitiser.Sanitise(group[0].Request.AttributeName), resultCode);
                group[0].Result = UniquenessProbeResult.Failed(group[0].Request.Candidates.Count,
                    $"The directory refused the probe of {searchBase} with {resultCode}");
                return;
            }
            catch (LdapException ex)
            {
                // The directory could not be reached, or did not answer: it will not for the next attribute either.
                _logger.Warning(ex, "LdapConnectorUniquenessProbe: The search of {SearchBase} for {Attributes} values failed (LDAP error {ErrorCode}).",
                    LogSanitiser.Sanitise(searchBase), LogSanitiser.Sanitise(DescribeAttributes(group)), ex.ErrorCode);
                foreach (var probe in group)
                    probe.Result = UniquenessProbeResult.Failed(probe.Request.Candidates.Count, DescribeLdapFailure(ex, timeout));
                return;
            }

            if (response.ResultCode != ResultCode.Success)
            {
                if (group.Count > 1)
                {
                    foreach (var probe in group)
                        SearchPartitions([probe], searchBases, cancellationToken);
                    return;
                }

                group[0].Result = UniquenessProbeResult.Failed(group[0].Request.Candidates.Count,
                    $"The directory answered the probe of {searchBase} with {response.ResultCode}");
                return;
            }

            responses.Add(response);
        }

        foreach (var probe in group)
        {
            var valuesFound = responses.SelectMany(r => ValuesIn(r, probe.Request.AttributeName, probe.IncludesProxyAddresses)).ToList();
            probe.Result = UniquenessProbeResult.FromValuesFound(probe.Request, valuesFound);
        }

        _logger.Debug("LdapConnectorUniquenessProbe: Searched {PartitionCount} partition(s) for {CandidateCount} candidate(s) of {Attributes}; {EntryCount} entries returned.",
            searchBases.Count, group.Sum(p => p.Request.Candidates.Count), LogSanitiser.Sanitise(DescribeAttributes(group)), responses.Sum(r => r.Entries.Count));
    }

    /// <summary>
    /// Searches the whole forest through the Global Catalog for <paramref name="group"/> at once, and answers each probe
    /// in it that the Global Catalog can; each it cannot is added to <paramref name="forPartitions"/>, with its caveat
    /// saying why, for the partitions to answer instead.
    /// </summary>
    private void SearchGlobalCatalog(ILdapOperationExecutor globalCatalog, string endpoint, IReadOnlyList<PendingProbe> group, List<PendingProbe> forPartitions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A share of the batch's time only, so a Global Catalog that does not answer still leaves the partition search
        // time to give the domain's own answer before the caller gives up on the whole Connected System.
        var timeout = ForestTimeout(group.Max(p => p.Request.Timeout));

        SearchResponse response;
        try
        {
            response = (SearchResponse)globalCatalog.SendRequest(CreateSearchRequest(string.Empty, BuildSearch(group), timeout), timeout);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded && group.Count > 1)
        {
            // A limit the attributes reach together says nothing about any one of them.
            _logger.Warning("LdapConnectorUniquenessProbe: More than {SizeLimit} entries in the forest hold {Attributes} values in this batch between them; searching the forest for each attribute on its own.",
                SizeLimit, LogSanitiser.Sanitise(DescribeAttributes(group)));
            foreach (var probe in group)
                SearchGlobalCatalog(globalCatalog, endpoint, [probe], forPartitions, cancellationToken);
            return;
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
        {
            _logger.Warning("LdapConnectorUniquenessProbe: More than {SizeLimit} entries in the forest hold {Attribute} values in this batch; the attribute is not unique there, so the probe cannot answer for it.",
                SizeLimit, LogSanitiser.Sanitise(group[0].Request.AttributeName));
            group[0].Result = NotUnique(group[0].Request);
            return;
        }
        catch (DirectoryOperationException ex)
        {
            var resultCode = ex.Response?.ResultCode.ToString() ?? "an unknown result";
            _logger.Warning(ex, "LdapConnectorUniquenessProbe: The Global Catalog on {Endpoint} refused the search for {Attributes} values with {ResultCode}; searching the domain only for the rest of the run.",
                LogSanitiser.Sanitise(endpoint), LogSanitiser.Sanitise(DescribeAttributes(group)), resultCode);
            GlobalCatalogUnavailable(group, forPartitions, $"the Global Catalog on {endpoint} refused the search with {resultCode}");
            return;
        }
        catch (LdapException ex)
        {
            _logger.Warning(ex, "LdapConnectorUniquenessProbe: Searching the Global Catalog on {Endpoint} for {Attributes} values failed (LDAP error {ErrorCode}); searching the domain only for the rest of the run.",
                LogSanitiser.Sanitise(endpoint), LogSanitiser.Sanitise(DescribeAttributes(group)), ex.ErrorCode);
            GlobalCatalogUnavailable(group, forPartitions, ex.ErrorCode == 85
                ? string.Create(CultureInfo.InvariantCulture, $"the Global Catalog on {endpoint} did not answer within {timeout.TotalSeconds:0} seconds")
                : $"searching the Global Catalog on {endpoint} failed ({ex.Message.TrimEnd('.')})");
            return;
        }

        if (response.ResultCode != ResultCode.Success)
        {
            GlobalCatalogUnavailable(group, forPartitions, $"the Global Catalog on {endpoint} answered the search with {response.ResultCode}");
            return;
        }

        foreach (var probe in group)
        {
            var attributeName = probe.Request.AttributeName;
            var result = UniquenessProbeResult.FromValuesFound(probe.Request, ValuesIn(response, attributeName, probe.IncludesProxyAddresses));
            if (result.Reason == null)
            {
                probe.Result = result;
                continue;
            }

            // The control value is in this very domain, which the Global Catalog holds; not seeing it means the Global
            // Catalog is blind to this attribute (a permission, or an attribute withdrawn from its partial attribute
            // set since the schema was read). The domain can still answer, so it does, for the rest of the session.
            _logger.Warning("LdapConnectorUniquenessProbe: The Global Catalog on {Endpoint} did not return a {Attribute} value JIM knows is there; searching the domain only for this attribute for the rest of the run.",
                LogSanitiser.Sanitise(endpoint), LogSanitiser.Sanitise(attributeName));
            probe.Caveat = ForestCaveat(attributeName, $"the Global Catalog on {endpoint} did not return a {attributeName} value JIM knows is in this domain");
            _attributeCaveats[attributeName] = probe.Caveat;
            forPartitions.Add(probe);
        }

        _logger.Debug("LdapConnectorUniquenessProbe: Searched the forest through the Global Catalog on {Endpoint} for {CandidateCount} candidate(s) of {Attributes}; {EntryCount} entries returned.",
            LogSanitiser.Sanitise(endpoint), group.Sum(p => p.Request.Candidates.Count), LogSanitiser.Sanitise(DescribeAttributes(group)), response.Entries.Count);
    }

    /// <summary>
    /// Records that the Global Catalog cannot be used for the rest of the session, because of <paramref name="why"/>,
    /// and hands every probe in <paramref name="group"/> to the partitions with its caveat.
    /// </summary>
    private void GlobalCatalogUnavailable(IReadOnlyList<PendingProbe> group, List<PendingProbe> forPartitions, string why)
    {
        foreach (var probe in group)
        {
            probe.Caveat = GlobalCatalogUnavailable(probe.Request.AttributeName, why);
            forPartitions.Add(probe);
        }
    }

    /// <summary>
    /// Decides where a forest-wide attribute is searched: through a Global Catalog (opening it on first use), or the
    /// partitions, with a caveat when they are not enough.
    /// </summary>
    private ForestRoute ChooseForestRoute(UniquenessProbeRequest request)
    {
        var attributeName = request.AttributeName;
        if (_attributeCaveats.TryGetValue(attributeName, out var latched))
            return ForestRoute.Partitions(latched);

        var timeout = ForestTimeout(request.Timeout);
        var forest = _forest ??= ReadForestLayout(timeout);

        // mail outside Active Directory is the ordinary attribute a partition search exists for.
        if (!forest.IsActiveDirectory)
            return ForestRoute.Partitions(null);

        var options = _globalCatalogOptions!;
        var server = options.ConfiguredServer;
        if (server == null)
        {
            // A forest of one domain is that domain, so the partition already is everywhere the value must be unique.
            if (forest.DomainCount == 1)
                return ForestRoute.Partitions(null);

            if (!forest.IsGlobalCatalog)
            {
                return ForestRoute.Partitions(ForestCaveat(attributeName,
                    "no Global Catalog was available to search (the domain controller JIM connects to is not one, and the Global Catalog Server setting is blank)"));
            }

            server = options.ConnectedServer;
        }

        if (_globalCatalogUnavailable != null)
            return ForestRoute.Partitions(ForestCaveat(attributeName, _globalCatalogUnavailable));

        if (IsReplicatedToGlobalCatalog(attributeName, forest, timeout) == false)
        {
            var caveat = ForestCaveat(attributeName, "it is not replicated to the Global Catalog");
            _attributeCaveats[attributeName] = caveat;
            return ForestRoute.Partitions(caveat);
        }

        var endpoint = string.Create(CultureInfo.InvariantCulture, $"{server}:{options.Port}");
        if (_globalCatalog == null)
        {
            try
            {
                _globalCatalog = options.Open(server, timeout);
                _logger.Information("LdapConnectorUniquenessProbe: Probing forest-wide attributes through the Global Catalog on {Endpoint}.", LogSanitiser.Sanitise(endpoint));
            }
            catch (DirectoryException ex)
            {
                // LdapException and DirectoryOperationException alike: the connection or its bind failed. The
                // Connected System itself is still searchable, so its own domain answers, with the gap reported.
                _logger.Warning(ex, "LdapConnectorUniquenessProbe: Could not connect to the Global Catalog on {Endpoint}; searching the domain only for the rest of the run.",
                    LogSanitiser.Sanitise(endpoint));
                return ForestRoute.Partitions(GlobalCatalogUnavailable(attributeName,
                    $"JIM could not connect to the Global Catalog on {endpoint} ({ex.Message.TrimEnd('.')})"));
            }
        }

        return ForestRoute.Through(_globalCatalog, endpoint);
    }

    /// <summary>
    /// Records why the Global Catalog cannot be used for the rest of the session, and returns the caveat for
    /// <paramref name="attributeName"/>.
    /// </summary>
    private string GlobalCatalogUnavailable(string attributeName, string why)
    {
        _globalCatalogUnavailable = why;
        return ForestCaveat(attributeName, why);
    }

    /// <summary>
    /// Reads, over the probe connection, whether the directory is Active Directory, whether the domain controller is a
    /// Global Catalog, and how many domains the forest has. A rootDSE that cannot be read is treated as not Active
    /// Directory: the partition search that follows goes over the same connection and reports the fault itself.
    /// </summary>
    private ForestLayout ReadForestLayout(TimeSpan timeout)
    {
        var rootDseRequest = new SearchRequest { Scope = SearchScope.Base };
        rootDseRequest.Attributes.AddRange(["supportedCapabilities", "vendorName", "vendorVersion", "structuralObjectClass",
            "isGlobalCatalogReady", "configurationNamingContext", "schemaNamingContext"]);

        SearchResultEntry rootDse;
        try
        {
            var response = (SearchResponse)_executor.SendRequest(rootDseRequest, timeout);
            if (response.Entries.Count == 0)
                return ForestLayout.NotActiveDirectory;

            rootDse = response.Entries[0];
        }
        catch (DirectoryException ex)
        {
            _logger.Warning(ex, "LdapConnectorUniquenessProbe: Could not read the rootDSE to find out whether forest-wide attributes need a Global Catalog; searching the partition only.");
            return ForestLayout.NotActiveDirectory;
        }

        var directoryType = LdapConnectorUtilities.DetectDirectoryType(
            LdapConnectorUtilities.GetEntryAttributeStringValues(rootDse, "supportedCapabilities"),
            LdapConnectorUtilities.GetEntryAttributeStringValue(rootDse, "vendorName"),
            LdapConnectorUtilities.GetEntryAttributeStringValue(rootDse, "structuralObjectClass"),
            LdapConnectorUtilities.GetEntryAttributeStringValue(rootDse, "vendorVersion"));

        if (directoryType is not (LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD))
            return ForestLayout.NotActiveDirectory;

        var isGlobalCatalog = string.Equals(LdapConnectorUtilities.GetEntryAttributeStringValue(rootDse, "isGlobalCatalogReady"), "TRUE", StringComparison.OrdinalIgnoreCase);
        var domainCount = CountDomains(LdapConnectorUtilities.GetEntryAttributeStringValue(rootDse, "configurationNamingContext"), timeout);
        var forest = new ForestLayout(true, isGlobalCatalog, LdapConnectorUtilities.GetEntryAttributeStringValue(rootDse, "schemaNamingContext"), domainCount);

        _logger.Debug("LdapConnectorUniquenessProbe: Domains in the forest: {DomainCount} (null when unknown); the domain controller is a Global Catalog: {IsGlobalCatalog}.",
            domainCount, isGlobalCatalog);

        return forest;
    }

    /// <summary>
    /// How many domains the forest has, from the domain crossRefs in its Configuration partition, which every domain
    /// controller holds. Null when they cannot be read, which routes as though the forest had several: it is the
    /// cautious reading.
    /// </summary>
    private int? CountDomains(string? configurationNamingContext, TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(configurationNamingContext))
            return null;

        // Read every crossRef and test the flag here rather than in a bitwise matching-rule filter, which not every
        // Active Directory-compatible directory implements.
        var request = new SearchRequest($"CN=Partitions,{configurationNamingContext}", "(objectClass=crossRef)", SearchScope.OneLevel, "nCName", "systemFlags");
        try
        {
            var response = (SearchResponse)_executor.SendRequest(request, timeout);
            var domains = response.Entries.Cast<SearchResultEntry>().Count(e =>
                int.TryParse(LdapConnectorUtilities.GetEntryAttributeStringValue(e, "systemFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags)
                && (flags & CrossRefDomainFlag) != 0);

            return domains > 0 ? domains : null;
        }
        catch (DirectoryException ex)
        {
            _logger.Warning(ex, "LdapConnectorUniquenessProbe: Could not read the forest's domains from {Container}; treating the forest as having several.",
                LogSanitiser.Sanitise(request.DistinguishedName));
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="attributeName"/> is in the Global Catalog's partial attribute set, from its schema
    /// entry; null when that cannot be read, in which case the Global Catalog is searched anyway and the control value
    /// shows whether it can see the attribute.
    /// </summary>
    private bool? IsReplicatedToGlobalCatalog(string attributeName, ForestLayout forest, TimeSpan timeout)
    {
        if (_replicatedToGlobalCatalog.TryGetValue(attributeName, out var known))
            return known;

        bool? replicated = null;
        if (!string.IsNullOrEmpty(forest.SchemaNamingContext))
        {
            var request = new SearchRequest(forest.SchemaNamingContext,
                $"(&(objectClass=attributeSchema)(lDAPDisplayName={LdapConnectorUtilities.EscapeLdapFilterValue(attributeName)}))",
                SearchScope.OneLevel, "isMemberOfPartialAttributeSet");
            try
            {
                var response = (SearchResponse)_executor.SendRequest(request, timeout);
                if (response.Entries.Count == 1)
                {
                    // Active Directory leaves the attribute off an attribute outside the set rather than storing FALSE.
                    replicated = string.Equals(LdapConnectorUtilities.GetEntryAttributeStringValue(response.Entries[0], "isMemberOfPartialAttributeSet"),
                        "TRUE", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (DirectoryException ex)
            {
                _logger.Warning(ex, "LdapConnectorUniquenessProbe: Could not read whether {Attribute} is replicated to the Global Catalog; searching the Global Catalog for it anyway.",
                    LogSanitiser.Sanitise(attributeName));
            }
        }

        _replicatedToGlobalCatalog[attributeName] = replicated;
        return replicated;
    }

    /// <summary>
    /// Appends an equality assertion per value to <paramref name="filter"/>, every value escaped per RFC 4515, with a
    /// <c>proxyAddresses</c> assertion beside each when <paramref name="includeProxyAddresses"/>.
    /// </summary>
    private static StringBuilder AppendAssertions(StringBuilder filter, string attributeName, IEnumerable<string> values, bool includeProxyAddresses)
    {
        foreach (var escaped in values.Select(LdapConnectorUtilities.EscapeLdapFilterValue))
        {
            filter.Append('(').Append(attributeName).Append('=').Append(escaped).Append(')');
            if (includeProxyAddresses)
                filter.Append('(').Append(ProxyAddressesAttribute).Append('=').Append(SmtpProxyAddressPrefix).Append(escaped).Append(')');
        }

        return filter;
    }

    /// <summary>
    /// One search for every probe in <paramref name="group"/>: an OR of each probe's assertions, in the order asked,
    /// returning each probed attribute once.
    /// </summary>
    private static ProbeSearch BuildSearch(IReadOnlyList<PendingProbe> group)
    {
        var filter = new StringBuilder("(|");
        var attributes = new List<string>();
        foreach (var probe in group)
        {
            AppendAssertions(filter, probe.Request.AttributeName, probe.Values, probe.IncludesProxyAddresses);
            attributes.Add(probe.Request.AttributeName);
            if (probe.IncludesProxyAddresses)
                attributes.Add(ProxyAddressesAttribute);
        }

        return new ProbeSearch(filter.Append(')').ToString(), [.. attributes.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    private static string DescribeAttributes(IReadOnlyList<PendingProbe> group) =>
        string.Join(", ", group.Select(p => p.Request.AttributeName));

    private static SearchRequest CreateSearchRequest(string searchBase, ProbeSearch search, TimeSpan timeout) =>
        new(searchBase, search.Filter, SearchScope.Subtree, search.Attributes)
        {
            SizeLimit = SizeLimit,
            TimeLimit = timeout
        };

    /// <summary>
    /// The values a search returned for the probed attribute and, where it was searched, the email addresses among the
    /// returned <c>proxyAddresses</c> without their <c>smtp:</c> prefix. Other kinds of proxy address are not email
    /// addresses, and are left out.
    /// </summary>
    private static IEnumerable<string> ValuesIn(SearchResponse response, string attributeName, bool includeProxyAddresses) =>
        response.Entries.Cast<SearchResultEntry>().SelectMany(entry =>
        {
            var values = LdapConnectorUtilities.GetEntryAttributeStringValues(entry, attributeName) ?? [];
            if (!includeProxyAddresses)
                return values;

            var addresses = (LdapConnectorUtilities.GetEntryAttributeStringValues(entry, ProxyAddressesAttribute) ?? [])
                .Where(a => a.StartsWith(SmtpProxyAddressPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(a => a[SmtpProxyAddressPrefix.Length..]);
            return values.Concat(addresses);
        });

    private static UniquenessProbeResult NotUnique(UniquenessProbeRequest request) =>
        UniquenessProbeResult.Undetermined(request.Candidates.Count,
            $"More than {SizeLimit} entries hold the {request.AttributeName} values probed for, so the attribute is not unique in the directory");

    /// <summary>
    /// The time a Global Catalog connection, search or forest read may take: a third of the batch's, leaving the rest
    /// for the partition search should the Global Catalog not answer.
    /// </summary>
    private static TimeSpan ForestTimeout(TimeSpan batchTimeout) => batchTimeout / 3;

    /// <summary>
    /// The caveat for a forest-wide attribute searched in this domain only, as a sentence an administrator can act
    /// on: <paramref name="why"/> names what kept the Global Catalog out of reach.
    /// </summary>
    private static string ForestCaveat(string attributeName, string why) =>
        $"The {attributeName} attribute is unique across the Active Directory forest, but {why}, so JIM searched only this Connected System's own domain and could not see a value in use in another domain";

    /// <summary>
    /// A sentence for an LDAP client failure: a client-side timeout (85) gets its own wording, since "did not answer
    /// in time" and "could not be reached" lead an administrator to different places.
    /// </summary>
    private static string DescribeLdapFailure(LdapException ex, TimeSpan timeout) =>
        ex.ErrorCode == 85
            ? $"The directory did not answer within {timeout.TotalSeconds:0} seconds"
            : $"The probe failed: {ex.Message.TrimEnd('.')}";

    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9-]*|[0-9]+(?:\.[0-9]+)+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeDescriptionPattern();

    /// <summary>
    /// One search: the filter, and the attributes to return.
    /// </summary>
    private sealed record ProbeSearch(string Filter, string[] Attributes);

    /// <summary>
    /// One attribute's batch on its way through <see cref="Probe"/>: where it is searched, what the route could not
    /// reach, and its answer once it has one.
    /// </summary>
    private sealed class PendingProbe(int index, UniquenessProbeRequest request)
    {
        public int Index { get; } = index;

        public UniquenessProbeRequest Request { get; } = request;

        /// <summary>
        /// The candidates, then the control value when there is one.
        /// </summary>
        public IEnumerable<string> Values => Request.ControlValue == null ? Request.Candidates : Request.Candidates.Append(Request.ControlValue);

        /// <summary>
        /// Whether proxyAddresses is searched beside the attribute (see <see cref="IsEmailAttribute"/>).
        /// </summary>
        public bool IncludesProxyAddresses { get; set; }

        /// <summary>
        /// What the partitions could not reach, for a forest-wide attribute searched there; null when nothing.
        /// </summary>
        public string? Caveat { get; set; }

        public UniquenessProbeResult? Result { get; set; }
    }

    /// <summary>
    /// What the probe connection showed of the forest (#1940).
    /// </summary>
    private sealed record ForestLayout(bool IsActiveDirectory, bool IsGlobalCatalog, string? SchemaNamingContext, int? DomainCount)
    {
        public static readonly ForestLayout NotActiveDirectory = new(false, false, null, null);
    }

    /// <summary>
    /// Where one forest-wide batch is searched: through <see cref="GlobalCatalog"/>, or the partitions with
    /// <see cref="Caveat"/> (null when they cover everywhere the value must be unique).
    /// </summary>
    private readonly record struct ForestRoute(ILdapOperationExecutor? GlobalCatalog, string? Endpoint, string? Caveat)
    {
        public static ForestRoute Partitions(string? caveat) => new(null, null, caveat);

        public static ForestRoute Through(ILdapOperationExecutor globalCatalog, string endpoint) => new(globalCatalog, endpoint, null);
    }
}
