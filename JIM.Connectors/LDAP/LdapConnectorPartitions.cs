using CPI.DirectoryServices;
using JIM.Models.Staging;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

internal class LdapConnectorPartitions
{
    private readonly LdapConnection _connection;
    private string _partitionsDn = null!;

    internal LdapConnectorPartitions(LdapConnection ldapConnection) 
    {
        _connection = ldapConnection;
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
                var partition = new ConnectorPartition
                {
                    Id = entry.DistinguishedName,
                    Name = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "ncname") ?? entry.DistinguishedName,
                    //DnsNames = GetEntryAttributeStringValues(entry, "dnsroot"),
                    //NetbiosName = GetEntryAttributeStringValue(entry, "netbiosname"),
                    Hidden = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "systemflags") != "3", // domain
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
        // get all containers
        // work out which ones are the root containers by their DN
        // enumerate the root containers
        // recurse, finding children of the container

        var request = new SearchRequest(partition.Name, "(|(objectClass=organizationalUnit)(objectClass=container))", SearchScope.Subtree);
        var response = (SearchResponse)_connection.SendRequest(request);

        // copy the search result entries to a list we can manipulate
        var entries = response.Entries.Cast<SearchResultEntry>().ToList();

        // move top-level containers to the new list
        var topLevelContainers = entries.Where(q => new DN(q.DistinguishedName).Parent.ToString().Equals(partition.Name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        entries.RemoveAll(q => topLevelContainers.Contains(q));
        var containers = topLevelContainers.Select(topLevelContainer => new ConnectorContainer(topLevelContainer.DistinguishedName, LdapConnectorUtilities.GetEntryAttributeStringValue(topLevelContainer, "name") ?? topLevelContainer.DistinguishedName)).ToList();

        // keep track of how many entries we've processed so we can validate completion
        var entriesProcessedCounter = containers.Count;

        // loop over the higher-level containers we've already moved into the hierarchy, so we can look for children of them
        containers = containers.OrderBy(q => q.Name).ToList();
        foreach (var container in containers)
            ProcessContainerNodeForHierarchyRecursively(entries, container, ref entriesProcessedCounter);

        return containers;
    }

    private static void ProcessContainerNodeForHierarchyRecursively(List<SearchResultEntry> entries, ConnectorContainer containerToLookForChildrenFor, ref int entriesProcessedCounter)
    {
        foreach (var entry in entries.Where(q => new DN(q.DistinguishedName).Parent.ToString().Equals(containerToLookForChildrenFor.Id, StringComparison.CurrentCultureIgnoreCase)))
        {
            var newChildContainer = new ConnectorContainer(entry.DistinguishedName, LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "name") ?? entry.DistinguishedName);
            containerToLookForChildrenFor.ChildContainers.Add(newChildContainer);
            entriesProcessedCounter++;
            ProcessContainerNodeForHierarchyRecursively(entries, newChildContainer, ref entriesProcessedCounter);
            containerToLookForChildrenFor.ChildContainers = containerToLookForChildrenFor.ChildContainers.OrderBy(q => q.Name).ToList();
        }
    }

    private string? GetConfigurationNamingContext()
    {
        // get the configuration naming context from an attribute on the rootDSE
        var request = new SearchRequest() { Scope = SearchScope.Base };
        request.Attributes.Add("configurationNamingContext");
        var response = (SearchResponse)_connection.SendRequest(request);

        if (response.ResultCode != ResultCode.Success)
        {
            Console.WriteLine($"GetConfigurationNamingContext: No success. Result code: {response.ResultCode}");
            return null;
        }

        if (response.Entries.Count == 0)
        {
            Console.WriteLine("GetConfigurationNamingContext: Didn't get any results!");
            return null;
        }

        var entry = response.Entries[0];
        return LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "configurationNamingContext");
    }
}