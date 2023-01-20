using System.DirectoryServices.Protocols;

namespace JIM.Connectors.LDAP
{
    internal static class LdapConnectorUtilities
    {
        internal static string? GetEntryAttributeStringValue(SearchResultEntry entry, string attributeName)
        {
            if (entry == null) return null;
            if (!entry.Attributes.Contains(attributeName)) return null;
            if (entry.Attributes[attributeName].Count != 1) return null;
            return (string)entry.Attributes[attributeName][0];
        }

        internal static bool? GetEntryAttributeBooleanValue(SearchResultEntry entry, string attributeName)
        {
            if (entry == null) return null;
            if (!entry.Attributes.Contains(attributeName)) return null;
            if (entry.Attributes[attributeName].Count != 1) return null;
            var stringValue = (string)entry.Attributes[attributeName][0];
            return bool.Parse(stringValue);
        }

        internal static List<string>? GetEntryAttributeStringValues(SearchResultEntry entry, string attributeName)
        {
            if (entry == null) return null;
            if (!entry.Attributes.Contains(attributeName)) return null;
            if (entry.Attributes[attributeName].Count == 0) return null;
            return (from string value in entry.Attributes[attributeName].GetValues(typeof(string))
                    select value).ToList();
        }

        internal static SearchResultEntry? GetSchemaEntry(LdapConnection connection, string root, string query)
        {
            var dn = $"CN=Schema,CN=Configuration,{root}";
            var request = new SearchRequest(dn, query, SearchScope.OneLevel);
            var response = (SearchResponse)connection.SendRequest(request);
            return response != null && response.Entries.Count == 1 ? response.Entries[0] : null;
        }
    }
}
