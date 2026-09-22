// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

internal class LdapConnectorPartitions
{
    private readonly ILdapOperationExecutor _executor;
    private readonly ILogger _logger;
    private readonly LdapDirectoryType _directoryType;
    private string _partitionsDn = null!;

    internal LdapConnectorPartitions(ILdapOperationExecutor executor, ILogger logger, LdapDirectoryType directoryType)
    {
        _executor = executor;
        _logger = logger;
        _directoryType = directoryType;
    }

    /// <summary>
    /// True where a partition search failed because the bind account cannot see the naming context at all: it is
    /// missing (<see cref="ResultCode.NoSuchObject"/>, which some directories return instead of an access-rights
    /// error to avoid confirming the object's existence to an unprivileged caller) or the account has no rights to
    /// read it (<see cref="ResultCode.InsufficientAccessRights"/>). Both are an ordinary, expected shape for a
    /// least-privilege service account bound against a directory that hosts several suffixes and grants it rights on
    /// only one of them; every other result code is a genuine fault and must still propagate.
    /// </summary>
    private static bool IsUnreadablePartitionResult(DirectoryOperationException ex) =>
        ex.Response?.ResultCode is ResultCode.NoSuchObject or ResultCode.InsufficientAccessRights;

    /// <summary>
    /// The attribute carrying the directory's own immutable identifier for an entry, which is what container
    /// identity is keyed on across hierarchy refreshes. Mirrors <see cref="LdapConnectorRootDse.ExternalIdAttributeName"/>,
    /// which is not available here because partition discovery runs from the directory type alone.
    /// </summary>
    private string StableIdAttributeName => _directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD
        ? "objectGUID"
        : "entryUUID";

    /// <summary>
    /// Reads the directory's immutable identifier from a container entry, returning null when the directory did not
    /// supply one so that the merge falls back to Distinguished Name matching.
    /// </summary>
    /// <remarks>
    /// Active Directory returns objectGUID as 16 binary bytes in Microsoft byte order; OpenLDAP returns entryUUID as
    /// an RFC 4530 string. Both are normalised to the same canonical GUID string so a directory that changes its
    /// representation cannot silently orphan every stored container.
    /// </remarks>
    private static string? ReadStableId(SearchResultEntry entry, string stableIdAttribute)
    {
        if (!entry.Attributes.Contains(stableIdAttribute))
            return null;

        var value = entry.Attributes[stableIdAttribute][0];
        return value switch
        {
            byte[] { Length: 16 } guidBytes => IdentifierParser.FromMicrosoftBytes(guidBytes).ToString(),
            string text when Guid.TryParse(text.Trim(), out var parsed) => parsed.ToString(),
            _ => null
        };
    }

    internal async Task<List<ConnectorPartition>> GetPartitionsAsync(bool skipHiddenPartitions = true)
    {
        return _directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD
            ? await GetActiveDirectoryPartitionsAsync(skipHiddenPartitions)
            : await GetNamingContextPartitionsAsync();
    }

    /// <summary>
    /// Discovers partitions using the AD-specific crossRef/systemFlags mechanism.
    /// Works for both Microsoft AD and Samba AD.
    /// </summary>
    private async Task<List<ConnectorPartition>> GetActiveDirectoryPartitionsAsync(bool skipHiddenPartitions)
    {
        return await Task.Run(() =>
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // get the partitions DN by deriving it from the configuration naming context
            var configurationNamingContext = GetConfigurationNamingContext();
            if (string.IsNullOrEmpty(configurationNamingContext))
                throw new Exception($"Couldn't get configuration naming context from rootDSE.");
            _partitionsDn = $"CN=Partitions,{configurationNamingContext}";

            var request = new SearchRequest(_partitionsDn, "(objectClass=crossRef)", SearchScope.OneLevel);
            var response = (SearchResponse)_executor.SendRequest(request);
            var partitions = new List<ConnectorPartition>();
            var attemptedPartitionCount = 0;
            var unreadablePartitionCount = 0;

            _logger.Debug("GetActiveDirectoryPartitionsAsync: Found {Count} crossRef entries to process (skipHiddenPartitions={SkipHidden})",
                response.Entries.Count, skipHiddenPartitions);

            foreach (SearchResultEntry entry in response.Entries)
            {
                // ncName is the actual naming context DN (e.g., "DC=panoply,DC=local")
                // entry.DistinguishedName is the crossRef object DN (e.g., "CN=panoply,CN=Partitions,CN=Configuration,DC=panoply,DC=local")
                // We use ncName as the Id because container DNs end with the naming context, not the crossRef DN
                var ncName = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "ncname") ?? entry.DistinguishedName;
                var systemFlags = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "systemflags");

                // Determine if this is a domain partition (not hidden)
                // Domain partitions in AD have systemFlags=3, but Samba AD may differ
                // Also check if ncName is a pure domain DN (starts with DC= and doesn't contain Configuration/Schema/DnsZones)
                var isDomainPartitionByFlags = systemFlags == LdapConnectorConstants.SYSTEM_FLAGS_DOMAIN_PARTITION;
                var isDomainPartitionByName = ncName.StartsWith("DC=", StringComparison.OrdinalIgnoreCase) &&
                                               !ncName.Contains("CN=Configuration", StringComparison.OrdinalIgnoreCase) &&
                                               !ncName.Contains("CN=Schema", StringComparison.OrdinalIgnoreCase) &&
                                               !ncName.Contains("DomainDnsZones", StringComparison.OrdinalIgnoreCase) &&
                                               !ncName.Contains("ForestDnsZones", StringComparison.OrdinalIgnoreCase);
                var isHidden = !isDomainPartitionByFlags && !isDomainPartitionByName;

                _logger.Debug("GetActiveDirectoryPartitionsAsync: Partition '{Name}' - systemFlags={SystemFlags}, isDomainByFlags={ByFlags}, isDomainByName={ByName}, isHidden={IsHidden}",
                    ncName, systemFlags ?? "(null)", isDomainPartitionByFlags, isDomainPartitionByName, isHidden);

                // Skip hidden partitions early if configured - this avoids expensive LDAP subtree searches
                if (skipHiddenPartitions && isHidden)
                {
                    _logger.Debug("GetActiveDirectoryPartitionsAsync: Skipping hidden partition '{Name}'", ncName);
                    continue;
                }

                attemptedPartitionCount++;

                var partitionStopwatch = System.Diagnostics.Stopwatch.StartNew();

                var partition = new ConnectorPartition
                {
                    Id = ncName,
                    Name = ncName,
                    Hidden = isHidden,
                };

                try
                {
                    partition.Containers = GetPartitionContainers(partition);
                }
                catch (DirectoryOperationException ex) when (IsUnreadablePartitionResult(ex))
                {
                    // A least-privilege service account is commonly granted rights on its own domain partition only,
                    // not on every crossRef partition a multi-domain forest advertises (Configuration, Schema, other
                    // domains). Skipping this one partition is correct: the rest of the forest it can see must still
                    // come back rather than failing the whole hierarchy import for a partition it was never meant to read.
                    unreadablePartitionCount++;
                    _logger.Warning(
                        "GetActiveDirectoryPartitionsAsync: the bind account has no read access to crossRef partition '{Partition}' (LDAP result {ResultCode}); skipping it.",
                        partition.Name, ex.Response?.ResultCode);
                    continue;
                }

                partitionStopwatch.Stop();

                _logger.Debug("GetActiveDirectoryPartitionsAsync: Partition '{Name}' (Hidden={Hidden}) - {ContainerCount} containers retrieved in {ElapsedMs}ms",
                    partition.Name, partition.Hidden, partition.Containers.Count, partitionStopwatch.ElapsedMilliseconds);

                // only return partitions that have containers. Discard the rest.
                if (partition.Containers.Count > 0)
                    partitions.Add(partition);
            }

            // Every attempted partition being unreadable is a configuration fault, not an empty forest: reporting an
            // empty hierarchy here would look like "this directory has no organisational units" rather than "this
            // account cannot read anything", which sends an administrator looking in the wrong place entirely.
            if (attemptedPartitionCount > 0 && unreadablePartitionCount == attemptedPartitionCount)
                throw new Exception("The bind account can read none of the directory's crossRef partitions. Grant it read access to at least one domain partition, or verify its credentials.");

            totalStopwatch.Stop();
            _logger.Information("GetActiveDirectoryPartitionsAsync: Completed - {PartitionCount} partitions with containers in {ElapsedMs}ms total",
                partitions.Count, totalStopwatch.ElapsedMilliseconds);

            return partitions;
        });
    }

    /// <summary>
    /// Discovers partitions using the standard LDAP namingContexts rootDSE attribute (RFC 4512).
    /// Works for OpenLDAP, 389 Directory Server, and other standards-compliant directories.
    /// All naming contexts are returned as non-hidden partitions.
    /// </summary>
    private async Task<List<ConnectorPartition>> GetNamingContextPartitionsAsync()
    {
        return await Task.Run(() =>
        {
            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var namingContexts = GetNamingContexts();
            if (namingContexts == null || namingContexts.Count == 0)
                throw new Exception("Couldn't get any namingContexts from rootDSE. The directory may not expose partition information.");

            _logger.Debug("GetNamingContextPartitionsAsync: Found {Count} naming contexts", namingContexts.Count);

            var partitions = new List<ConnectorPartition>();
            var unreadableNamingContextCount = 0;

            foreach (var namingContext in namingContexts)
            {
                var partitionStopwatch = System.Diagnostics.Stopwatch.StartNew();

                var partition = new ConnectorPartition
                {
                    Id = namingContext,
                    Name = namingContext,
                    Hidden = false,
                };

                try
                {
                    partition.Containers = GetPartitionContainers(partition);
                }
                catch (DirectoryOperationException ex) when (IsUnreadablePartitionResult(ex))
                {
                    // A least-privilege service account bound against a directory that hosts several suffixes (an
                    // OpenLDAP server serving more than one organisation, or exposing operational naming contexts such
                    // as cn=config or cn=accesslog) commonly has rights on only one of them. That is not a fault in
                    // the directory or in JIM; the naming context is simply not this account's to read, and the
                    // hierarchy import must still bring back everything the account CAN see rather than failing outright.
                    unreadableNamingContextCount++;
                    _logger.Warning(
                        "GetNamingContextPartitionsAsync: the bind account has no read access to naming context '{NamingContext}' (LDAP result {ResultCode}); skipping it.",
                        partition.Name, ex.Response?.ResultCode);
                    continue;
                }

                partitionStopwatch.Stop();

                _logger.Debug("GetNamingContextPartitionsAsync: Partition '{Name}' - {ContainerCount} containers retrieved in {ElapsedMs}ms",
                    partition.Name, partition.Containers.Count, partitionStopwatch.ElapsedMilliseconds);

                // only return partitions that have containers. Discard the rest.
                if (partition.Containers.Count > 0)
                    partitions.Add(partition);
            }

            // Every naming context being unreadable is a configuration fault (wrong credentials, or an account
            // granted no rights anywhere), not a directory with nothing in it: reporting an empty hierarchy here
            // would read as "this directory is empty" rather than "this account cannot read anything".
            if (unreadableNamingContextCount == namingContexts.Count)
                throw new Exception("The bind account can read none of the directory's naming contexts. Grant it read access to at least one, or verify its credentials.");

            totalStopwatch.Stop();
            _logger.Information("GetNamingContextPartitionsAsync: Completed - {PartitionCount} partitions with containers in {ElapsedMs}ms total",
                partitions.Count, totalStopwatch.ElapsedMilliseconds);

            return partitions;
        });
    }

    private List<ConnectorContainer> GetPartitionContainers(ConnectorPartition partition)
    {
        var ldapStopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Ask for the directory's own immutable identifier alongside the name: the hierarchy merge keys container
        // identity on it, because the Distinguished Name it would otherwise match on changes on every rename and
        // move. "name" and the identifier are requested explicitly; operational attributes such as entryUUID are not
        // returned by a default search.
        var stableIdAttribute = StableIdAttributeName;
        var request = new SearchRequest(
            partition.Name,
            "(|(objectClass=organizationalUnit)(objectClass=container))",
            SearchScope.Subtree,
            "name",
            stableIdAttribute);
        var response = (SearchResponse)_executor.SendRequest(request);
        ldapStopwatch.Stop();

        var processingStopwatch = System.Diagnostics.Stopwatch.StartNew();
        // Convert SearchResultEntry objects to simple DTOs for the hierarchy builder
        var entries = response.Entries.Cast<SearchResultEntry>()
            .Select(e => new ContainerEntry(
                e.DistinguishedName,
                LdapConnectorUtilities.GetEntryAttributeStringValue(e, "name"),
                ReadStableId(e, stableIdAttribute)))
            .ToList();

        var containers = BuildContainerHierarchy(entries, partition.Name);
        processingStopwatch.Stop();

        _logger.Debug("GetPartitionContainers: '{Partition}' - LDAP query: {LdapMs}ms, Processing {EntryCount} entries: {ProcessingMs}ms",
            partition.Name, ldapStopwatch.ElapsedMilliseconds, entries.Count, processingStopwatch.ElapsedMilliseconds);

        return containers;
    }

    /// <summary>
    /// Builds a hierarchical container structure from a flat list of container entries.
    /// Uses O(n) dictionary-based lookup instead of O(n²) repeated list scanning.
    /// </summary>
    /// <param name="entries">Flat list of container entries with DN and name.</param>
    /// <param name="partitionDn">The partition DN (root) to identify top-level containers.</param>
    /// <returns>List of top-level containers with nested children.</returns>
    internal static List<ConnectorContainer> BuildContainerHierarchy(List<ContainerEntry> entries, string partitionDn)
    {
        if (entries.Count == 0)
            return new List<ConnectorContainer>();

        // Step 1: Parse all DNs once and build parent lookup (O(n))
        var dnToParent = new Dictionary<string, string>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            // Preserve the directory's own DN formatting (verbatim tail) so parent strings match child DNs
            // for the case-insensitive dictionary lookups below; a single-RDN entry has no parent (empty string).
            var parentDn = LdapDistinguishedName.Parse(entry.DistinguishedName).Parent?.ToString() ?? string.Empty;
            dnToParent[entry.DistinguishedName] = parentDn;
        }

        // Step 2: Group entries by their parent DN (O(n))
        var childrenByParent = entries
            .GroupBy(e => dnToParent[e.DistinguishedName], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Step 3: Create all ConnectorContainer objects upfront (O(n))
        var containerByDn = entries
            .ToDictionary(
                e => e.DistinguishedName,
                e => new ConnectorContainer(e.DistinguishedName, ResolveContainerDisplayName(e)) { StableId = e.StableId },
                StringComparer.OrdinalIgnoreCase);

        // Step 4: Build hierarchy using dictionary lookups (O(n))
        var topLevelContainers = new List<ConnectorContainer>();

        foreach (var entry in entries)
        {
            var container = containerByDn[entry.DistinguishedName];
            var parentDn = dnToParent[entry.DistinguishedName];

            if (parentDn.Equals(partitionDn, StringComparison.OrdinalIgnoreCase))
            {
                // This is a top-level container
                topLevelContainers.Add(container);
            }
            else if (containerByDn.TryGetValue(parentDn, out var parentContainer))
            {
                // Add to parent's children
                parentContainer.ChildContainers.Add(container);
            }
            // If parent not found in containerByDn, it means the parent is not an OU/container
            // (e.g., the partition itself), so this becomes a top-level container
            else
            {
                topLevelContainers.Add(container);
            }
        }

        // Step 5: Sort all children recursively (O(n log n) total)
        SortChildrenRecursively(topLevelContainers);

        return topLevelContainers.OrderBy(c => c.Name).ToList();
    }

    private static void SortChildrenRecursively(List<ConnectorContainer> containers)
    {
        foreach (var container in containers)
        {
            if (container.ChildContainers.Count > 0)
            {
                container.ChildContainers = container.ChildContainers.OrderBy(c => c.Name).ToList();
                SortChildrenRecursively(container.ChildContainers);
            }
        }
    }

    /// <summary>
    /// Simple DTO representing a container entry from LDAP search results.
    /// Used to decouple the hierarchy building algorithm from System.DirectoryServices.Protocols.
    /// </summary>
    /// <summary>
    /// A container as read from the directory. <paramref name="StableId"/> is the directory's own immutable
    /// identifier (objectGUID on Active Directory, entryUUID on OpenLDAP), which the hierarchy merge keys container
    /// identity on because the Distinguished Name changes on every rename and move. Null where the directory did
    /// not return one.
    /// </summary>
    /// <summary>
    /// The name to show for a container: the directory's own, where it publishes one, and otherwise the value of the
    /// Distinguished Name's leaf RDN ("Sales" from "OU=Sales,OU=Corp,DC=example,DC=com").
    /// </summary>
    /// <remarks>
    /// The fallback used to be the whole Distinguished Name, which is not a name: it restates in every row the
    /// ancestry the container tree already draws, and buries the one component that distinguishes the container.
    /// Deriving it through <see cref="LdapConnectorUtilities.GetContainerDisplayNameFromDn"/> keeps this in step with
    /// the name given to containers the Connector creates during an export, which have always been named this way.
    /// </remarks>
    private static string ResolveContainerDisplayName(ContainerEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Name)
            ? LdapConnectorUtilities.GetContainerDisplayNameFromDn(entry.DistinguishedName)
            : entry.Name;

    /// <summary>
    /// A container as the directory returned it, before its display name has been decided.
    /// </summary>
    /// <param name="Name">
    /// The directory's own name for the container, or null where it publishes none. Only Active Directory supplies
    /// the 'name' operational attribute; everywhere else this is null and the display name is derived from the
    /// Distinguished Name. Resolved in <see cref="BuildContainerHierarchy"/> rather than at the call site, so the
    /// rule is one place and can be tested without a directory connection.
    /// </param>
    internal record ContainerEntry(string DistinguishedName, string? Name, string? StableId = null);

    /// <summary>
    /// Retrieves the namingContexts attribute from the rootDSE (RFC 4512).
    /// This is the standard mechanism for discovering partitions on non-AD directories.
    /// </summary>
    /// <remarks>
    /// Returns every naming context the rootDSE advertises, unfiltered: an operational-looking suffix such as
    /// <c>cn=config</c> or <c>cn=accesslog</c> is an ordinary naming context as far as this method is concerned, and
    /// filtering it out here would risk hiding a suffix the bind account genuinely can and should read. Whether the
    /// account can actually see a given naming context is decided by attempting the read in
    /// <see cref="GetNamingContextPartitionsAsync"/>, which skips whatever comes back unreadable.
    /// </remarks>
    private List<string>? GetNamingContexts()
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.Add("namingContexts");
        var response = (SearchResponse)_executor.SendRequest(request);

        if (response.ResultCode != ResultCode.Success)
        {
            _logger.Warning("GetNamingContexts: No success. Result code: {ResultCode}", response.ResultCode);
            return null;
        }

        if (response.Entries.Count == 0)
        {
            _logger.Warning("GetNamingContexts: Didn't get any results from rootDSE!");
            return null;
        }

        var entry = response.Entries[0];
        return LdapConnectorUtilities.GetEntryAttributeStringValues(entry, "namingContexts");
    }

    private string? GetConfigurationNamingContext()
    {
        // get the configuration naming context from an attribute on the rootDSE
        var request = new SearchRequest() { Scope = SearchScope.Base };
        request.Attributes.Add("configurationNamingContext");
        var response = (SearchResponse)_executor.SendRequest(request);

        if (response.ResultCode != ResultCode.Success)
        {
            _logger.Warning("GetConfigurationNamingContext: No success. Result code: {ResultCode}", response.ResultCode);
            return null;
        }

        if (response.Entries.Count == 0)
        {
            _logger.Warning("GetConfigurationNamingContext: Didn't get any results!");
            return null;
        }

        var entry = response.Entries[0];
        return LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "configurationNamingContext");
    }
}
