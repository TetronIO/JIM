using CPI.DirectoryServices;
using JIM.Models.Staging;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

internal class LdapConnectorPartitions
{
    private readonly LdapConnection _connection;
    private readonly ILogger _logger;
    private string _partitionsDn = null!;

    internal LdapConnectorPartitions(LdapConnection ldapConnection, ILogger logger)
    {
        _connection = ldapConnection;
        _logger = logger;
    }

    internal async Task<List<ConnectorPartition>> GetPartitionsAsync()
    {
        return await Task.Run(() =>
        {
            // get the partitions DN by deriving it from the configuration naming context
            var configurationNamingContext = GetConfigurationNamingContext();
            if (string.IsNullOrEmpty(configurationNamingContext))
                throw new Exception($"Couldn't get configuration naming context from rootDSE.");
            _partitionsDn = $"CN=Partitions,{configurationNamingContext}";

            var request = new SearchRequest(_partitionsDn, "(objectClass=crossRef)", SearchScope.OneLevel);
            var response = (SearchResponse)_connection.SendRequest(request);
            var partitions = new List<ConnectorPartition>();

            foreach (SearchResultEntry entry in response.Entries)
            {
                // ncName is the actual naming context DN (e.g., "DC=subatomic,DC=local")
                // entry.DistinguishedName is the crossRef object DN (e.g., "CN=subatomic,CN=Partitions,CN=Configuration,DC=subatomic,DC=local")
                // We use ncName as the Id because container DNs end with the naming context, not the crossRef DN
                var ncName = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "ncname") ?? entry.DistinguishedName;
                var partition = new ConnectorPartition
                {
                    Id = ncName,
                    Name = ncName,
                    Hidden = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "systemflags") != LdapConnectorConstants.SYSTEM_FLAGS_DOMAIN_PARTITION,
                };

                partition.Containers = GetPartitionContainers(partition);

                // only return partitions that have containers. Discard the rest.
                if (partition.Containers.Count > 0)
                    partitions.Add(partition);
            }

            return partitions;
        });
    }

    private List<ConnectorContainer> GetPartitionContainers(ConnectorPartition partition)
    {
        var request = new SearchRequest(partition.Name, "(|(objectClass=organizationalUnit)(objectClass=container))", SearchScope.Subtree);
        var response = (SearchResponse)_connection.SendRequest(request);

        // Convert SearchResultEntry objects to simple DTOs for the hierarchy builder
        var entries = response.Entries.Cast<SearchResultEntry>()
            .Select(e => new ContainerEntry(
                e.DistinguishedName,
                LdapConnectorUtilities.GetEntryAttributeStringValue(e, "name") ?? e.DistinguishedName))
            .ToList();

        return BuildContainerHierarchy(entries, partition.Name);
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
            var parentDn = new DN(entry.DistinguishedName).Parent.ToString();
            dnToParent[entry.DistinguishedName] = parentDn;
        }

        // Step 2: Group entries by their parent DN (O(n))
        var childrenByParent = entries
            .GroupBy(e => dnToParent[e.DistinguishedName], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Step 3: Create all ConnectorContainer objects upfront (O(n))
        var containerByDn = entries
            .ToDictionary(e => e.DistinguishedName, e => new ConnectorContainer(e.DistinguishedName, e.Name), StringComparer.OrdinalIgnoreCase);

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
    internal record ContainerEntry(string DistinguishedName, string Name);

    private string? GetConfigurationNamingContext()
    {
        // get the configuration naming context from an attribute on the rootDSE
        var request = new SearchRequest() { Scope = SearchScope.Base };
        request.Attributes.Add("configurationNamingContext");
        var response = (SearchResponse)_connection.SendRequest(request);

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