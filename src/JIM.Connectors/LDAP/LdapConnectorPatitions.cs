using CPI.DirectoryServices;
using JIM.Models.Staging;
using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP
{
    internal class LdapConnectorPartitions
    {
        private readonly LdapConnection _connection;
        private readonly string _root;

        internal LdapConnectorPartitions(LdapConnection ldapConnection, string rootDistinguishedName) 
        {
            _connection = ldapConnection;
            _root = rootDistinguishedName;
        }

        internal async Task<List<ConnectorPartition>> GetPartitionsAsync()
        {
            return await Task.Run(() => {
                var dn = $"CN=Partitions,CN=Configuration,{_root}";
                var request = new SearchRequest(dn, "(objectClass=crossRef)", SearchScope.OneLevel);
                var response = (SearchResponse)_connection.SendRequest(request);
                var partitions = new List<ConnectorPartition>();

                foreach (SearchResultEntry entry in response.Entries)
                {
                    var partition = new ConnectorPartition
                    {
                        Id = entry.DistinguishedName,
                        Name = LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "ncname"),
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
            var containers = new List<ConnectorContainer>();

            // copy the search result entries to a list we can manipulate
            var entries = new List<SearchResultEntry>();
            foreach (SearchResultEntry entry in response.Entries)
                entries.Add(entry);
            var totalEntries = entries.Count;

            // move top-level containers to the new list
            var topLevelContainers = entries.Where(q => new DN(q.DistinguishedName).Parent.ToString().Equals(_root, StringComparison.CurrentCultureIgnoreCase)).ToList();
            entries.RemoveAll(q => topLevelContainers.Contains(q));
            foreach (var topLevelContainer in topLevelContainers)
                containers.Add(new ConnectorContainer(topLevelContainer.DistinguishedName, LdapConnectorUtilities.GetEntryAttributeStringValue(topLevelContainer, "name")));

            // keep track of how many entries we've processed so we can validate completion
            var entriesProcessedCounter = containers.Count;

            // loop over the higher-level containers we've already moved into the hierarchy, so we can look for children of them
            foreach (var container in containers)
                ProcessContainerNodeForHierarchyRecursively(entries, container, ref entriesProcessedCounter);

            return containers;
        }

        private static void ProcessContainerNodeForHierarchyRecursively(List<SearchResultEntry> entries, ConnectorContainer containerToLookForChildrenFor, ref int entriesProcessedCounter)
        {
            foreach (var entry in entries.Where(q => new DN(q.DistinguishedName).Parent.ToString().Equals(containerToLookForChildrenFor.Id, StringComparison.CurrentCultureIgnoreCase)))
            {
                var newChildContainer = new ConnectorContainer(entry.DistinguishedName, LdapConnectorUtilities.GetEntryAttributeStringValue(entry, "name"));
                containerToLookForChildrenFor.ChildContainers.Add(newChildContainer);
                entriesProcessedCounter++;
                ProcessContainerNodeForHierarchyRecursively(entries, newChildContainer, ref entriesProcessedCounter);
            }
        }
    }
}
