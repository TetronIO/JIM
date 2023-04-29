using JIM.Models.Core;
using JIM.Models.Staging;
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
            return (bool)entry.Attributes[attributeName][0];
        }

        internal static DateTime? GetEntryAttributeDateTimeValue(SearchResultEntry entry, string attributeName)
        {
            if (entry == null) return null;
            if (!entry.Attributes.Contains(attributeName)) return null;
            if (entry.Attributes[attributeName].Count != 1) return null;
            return (DateTime)entry.Attributes[attributeName][0];
        }

        internal static List<string>? GetEntryAttributeStringValues(SearchResultEntry entry, string attributeName)
        {
            if (entry == null) return null;
            if (!entry.Attributes.Contains(attributeName)) return null;
            if (entry.Attributes[attributeName].Count == 0) return null;
            return (from string value in entry.Attributes[attributeName].GetValues(typeof(string))
                    select value).ToList();
        }

        internal static List<byte[]>? GetEntryAttributeBinaryValues(SearchResultEntry entry, string attributeName)
        {
            if (entry == null) return null;
            if (!entry.Attributes.Contains(attributeName)) return null;
            if (entry.Attributes[attributeName].Count == 0) return null;
            return (from byte[] value in entry.Attributes[attributeName].GetValues(typeof(byte[]))
                    select value).ToList();
        }

        internal static List<int>? GetEntryAttributeIntValues(SearchResultEntry entry, string attributeName)
        {
            if (entry == null) return null;
            if (!entry.Attributes.Contains(attributeName)) return null;
            if (entry.Attributes[attributeName].Count == 0) return null;
            return (from int value in entry.Attributes[attributeName].GetValues(typeof(int))
                    select value).ToList();
        }

        internal static int? GetEntryAttributeIntValue(SearchResultEntry entry, string attributeName)
        {
            if (entry == null) return null;
            if (!entry.Attributes.Contains(attributeName)) return null;
            if (entry.Attributes[attributeName].Count != 1) return null;
            var stringValue = (string)entry.Attributes[attributeName][0];
            return int.Parse(stringValue);
        }

        internal static SearchResultEntry? GetSchemaEntry(LdapConnection connection, string root, string query)
        {
            var dn = $"CN=Schema,CN=Configuration,{root}";
            var request = new SearchRequest(dn, query, SearchScope.OneLevel);
            var response = (SearchResponse)connection.SendRequest(request);
            return response != null && response.Entries.Count == 1 ? response.Entries[0] : null;
        }

        internal static string GetPaginationTokenName(ConnectedSystemContainer connectedSystemContainer, ConnectedSystemObjectType connectedSystemObjectType)
        {
            return $"{connectedSystemContainer.ExternalId}|{connectedSystemObjectType.Id}";
        }

        internal static AttributeDataType GetLdapAttributeDataType(int omSyntax)
        {
            // map the directory omSyntax to an attribute data type
            // https://social.technet.microsoft.com/wiki/contents/articles/52570.active-directory-syntaxes-of-attributes.aspx
            return omSyntax switch
            {
                1 or 10 => AttributeDataType.Bool,
                2 or 65 => AttributeDataType.Number,
                3 => AttributeDataType.Binary,
                6 or 18 or 19 or 20 or 22 or 27 or 64 => AttributeDataType.String,
                23 or 24 => AttributeDataType.DateTime,
                127 => AttributeDataType.Reference,
                _ => throw new InvalidDataException("Unsupported omSyntax value: " + omSyntax),
            };
        }
    }
}
