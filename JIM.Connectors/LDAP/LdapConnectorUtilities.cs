using JIM.Models.Core;
using JIM.Models.Staging;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

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

        var value = entry.Attributes[attributeName][0];

        // LDAP returns Boolean values as strings ("TRUE"/"FALSE")
        if (value is string stringValue)
        {
            return stringValue.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        }

        if (value is bool boolValue)
        {
            return boolValue;
        }

        return null;
    }

    internal static List<Guid>? GetEntryAttributeGuidValues(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count == 0) return null;

        var guidValues = new List<Guid>();
        foreach (byte[] byteValue in entry.Attributes[attributeName])
            guidValues.Add(new Guid(byteValue));

        return guidValues;
    }

    /// <summary>
    /// Returns the first value of an LDAP SearchResultEntry attribute, cast to Guid.
    /// If there are multiple values, only the first is returned.
    /// </summary>
    internal static Guid? GetEntryAttributeGuidValue(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count != 1) return null;
        return new Guid((byte[])entry.Attributes[attributeName][0]);
    }

    
    internal static DateTime? GetEntryAttributeDateTimeValue(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count != 1) return null;

        var value = entry.Attributes[attributeName][0];

        // LDAP returns DateTime values as strings in GeneralizedTime format (yyyyMMddHHmmss.fZ)
        if (value is string stringValue)
        {
            // Handle LDAP GeneralizedTime format: yyyyMMddHHmmss.fZ
            // Common formats: "20231215143000.0Z" or "20231215143000Z"
            if (DateTime.TryParseExact(stringValue.TrimEnd('Z'),
                new[] { "yyyyMMddHHmmss.f", "yyyyMMddHHmmss" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsedDate))
            {
                return parsedDate.ToUniversalTime();
            }
            // Fallback: try standard parsing
            if (DateTime.TryParse(stringValue, out parsedDate))
            {
                return parsedDate;
            }
            return null;
        }

        if (value is DateTime dateValue)
        {
            return dateValue;
        }

        return null;
    }

    internal static List<string>? GetEntryAttributeStringValues(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count == 0) return null;
        // PostgreSQL cannot store null bytes (0x00) in text columns, so strip them
        return (from string value in entry.Attributes[attributeName].GetValues(typeof(string))
            select value.Replace("\0", string.Empty)).ToList();
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
        // DirectoryAttribute.GetValues() only supports string or byte[] types, so get as strings and parse
        // Some AD attributes (like Large Integer syntax) may exceed Int32 range, so use TryParse
        var result = new List<int>();
        foreach (string value in entry.Attributes[attributeName].GetValues(typeof(string)))
        {
            if (int.TryParse(value, out var intValue))
            {
                result.Add(intValue);
            }
            // Values that overflow Int32 are silently skipped - this is a limitation
            // of JIM's current data model which doesn't have a separate Int64 type
        }
        return result.Count > 0 ? result : null;
    }

    internal static int? GetEntryAttributeIntValue(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count != 1) return null;
        var stringValue = (string)entry.Attributes[attributeName][0];
        return int.Parse(stringValue);
    }

    internal static long? GetEntryAttributeLongValue(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count != 1) return null;
        var stringValue = (string)entry.Attributes[attributeName][0];
        return long.Parse(stringValue);
    }

    internal static SearchResultEntry? GetSchemaEntry(LdapConnection connection, string schemaRootDn, string query)
    {
        var request = new SearchRequest(schemaRootDn, query, SearchScope.OneLevel);
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
            1 or 10 => AttributeDataType.Boolean,
            2 or 65 => AttributeDataType.Number,
            3 or 4 => AttributeDataType.Binary, // 3 = Binary, 4 = OctetString (photo, objectSid, logonHours)
            6 or 18 or 19 or 20 or 22 or 27 or 64 => AttributeDataType.Text,
            23 or 24 => AttributeDataType.DateTime,
            127 => AttributeDataType.Reference,
            _ => throw new InvalidDataException("Unsupported omSyntax value: " + omSyntax),
        };
    }
}