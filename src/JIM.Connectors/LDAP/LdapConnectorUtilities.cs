using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
namespace JIM.Connectors.LDAP;

internal static class LdapConnectorUtilities
{
    internal static string? GetEntryAttributeStringValue(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count != 1) return null;
        // Strip null bytes and treat empty strings as "no value"
        var value = ((string)entry.Attributes[attributeName][0]).Replace("\0", string.Empty);
        return string.IsNullOrEmpty(value) ? null : value;
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

    /// <summary>
    /// Returns all values of an LDAP SearchResultEntry attribute, cast to Guid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method assumes the binary attribute uses <b>Microsoft GUID byte order</b> (little-endian
    /// for the first three components: time_low, time_mid, time_hi_version). This is correct for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Active Directory <c>objectGUID</c></item>
    ///   <item>Samba AD <c>objectGUID</c></item>
    ///   <item>Any attribute stored in Microsoft GUID binary format</item>
    /// </list>
    /// <para>
    /// <b>Do NOT use this method</b> for RFC 4122 UUID binary attributes (big-endian first three
    /// components), such as custom binary UUID attributes in OpenLDAP or 389DS. For those, use
    /// <see cref="JIM.Utilities.IdentifierParser.FromRfc4122Bytes"/> after retrieving the raw bytes.
    /// </para>
    /// <para>
    /// Note: OpenLDAP's <c>entryUUID</c> is a string attribute (RFC 4530), not binary, so standard
    /// string parsing applies.
    /// </para>
    /// </remarks>
    internal static List<Guid>? GetEntryAttributeGuidValues(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count == 0) return null;

        var guidValues = new List<Guid>();
        foreach (byte[] byteValue in entry.Attributes[attributeName])
            guidValues.Add(IdentifierParser.FromMicrosoftBytes(byteValue));

        if (guidValues.Count == 0)
            return null;

        // Deduplicate values defensively - LDAP multi-valued attributes should not contain duplicates
        var uniqueValues = guidValues.Distinct().ToList();
        if (uniqueValues.Count < guidValues.Count)
        {
            var duplicateCount = guidValues.Count - uniqueValues.Count;
            Log.Warning("GetEntryAttributeGuidValues: Detected and removed {DuplicateCount} duplicate value(s) from attribute '{AttributeName}' on entry '{EntryDn}'. " +
                "Original count: {OriginalCount}, Unique count: {UniqueCount}",
                duplicateCount, attributeName, entry.DistinguishedName, guidValues.Count, uniqueValues.Count);
        }

        return uniqueValues;
    }

    /// <summary>
    /// Returns the first value of an LDAP SearchResultEntry attribute, cast to Guid.
    /// If there are multiple values, only the first is returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method assumes the binary attribute uses <b>Microsoft GUID byte order</b> (little-endian
    /// for the first three components: time_low, time_mid, time_hi_version). This is correct for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Active Directory <c>objectGUID</c></item>
    ///   <item>Samba AD <c>objectGUID</c></item>
    ///   <item>Any attribute stored in Microsoft GUID binary format</item>
    /// </list>
    /// <para>
    /// <b>Do NOT use this method</b> for RFC 4122 UUID binary attributes (big-endian first three
    /// components), such as custom binary UUID attributes in OpenLDAP or 389DS. For those, use
    /// <see cref="JIM.Utilities.IdentifierParser.FromRfc4122Bytes"/> after retrieving the raw bytes.
    /// </para>
    /// <para>
    /// Note: OpenLDAP's <c>entryUUID</c> is a string attribute (RFC 4530), not binary, so standard
    /// string parsing applies.
    /// </para>
    /// </remarks>
    internal static Guid? GetEntryAttributeGuidValue(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count != 1) return null;
        return IdentifierParser.FromMicrosoftBytes((byte[])entry.Attributes[attributeName][0]);
    }

    
    internal static DateTime? GetEntryAttributeDateTimeValue(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count != 1) return null;

        var value = entry.Attributes[attributeName][0];

        // LDAP returns DateTime values as strings in GeneralizedTime format (RFC 4517)
        // Format: yyyyMMddHHmmss[.fraction][Z|±hhmm]
        if (value is string stringValue)
        {
            var result = ParseLdapGeneralizedTime(stringValue);
            if (result.HasValue)
                return result;

            // Fallback: try standard ISO 8601 parsing
            if (DateTime.TryParse(stringValue, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsedDate))
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

    /// <summary>
    /// Parses LDAP GeneralizedTime format (RFC 4517).
    /// Supports: yyyyMMddHHmmss[.fraction][Z|±hhmm|±hh]
    /// Examples: "20231215143000Z", "20231215143000.123456Z", "20231215143000+0530", "20231215143000-05"
    /// </summary>
    private static DateTime? ParseLdapGeneralizedTime(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 14)
            return null;

        // Extract the base datetime part (yyyyMMddHHmmss)
        var basePart = value[..14];
        var remaining = value[14..];

        if (!DateTime.TryParseExact(basePart, "yyyyMMddHHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dateTime))
        {
            return null;
        }

        // Handle fractional seconds and timezone
        var fractionTicks = 0L;
        var offset = TimeSpan.Zero;
        var isUtc = false;

        if (remaining.Length > 0)
        {
            // Handle fractional seconds (starts with '.')
            if (remaining[0] == '.')
            {
                var fractionEnd = 1;
                while (fractionEnd < remaining.Length && char.IsDigit(remaining[fractionEnd]))
                    fractionEnd++;

                var fractionStr = remaining[1..fractionEnd];
                // Pad or truncate to 7 digits for .NET ticks precision
                fractionStr = fractionStr.PadRight(7, '0')[..7];
                if (long.TryParse(fractionStr, out fractionTicks))
                {
                    // fractionTicks is in 100-nanosecond units
                }
                remaining = remaining[fractionEnd..];
            }

            // Handle timezone: Z, +hhmm, -hhmm, +hh, -hh
            if (remaining.Length > 0)
            {
                if (remaining == "Z")
                {
                    isUtc = true;
                }
                else if (remaining[0] == '+' || remaining[0] == '-')
                {
                    var sign = remaining[0] == '+' ? 1 : -1;
                    var tzPart = remaining[1..];

                    int hours = 0, minutes = 0;
                    if (tzPart.Length >= 2 && int.TryParse(tzPart[..2], out hours))
                    {
                        if (tzPart.Length >= 4 && int.TryParse(tzPart[2..4], out minutes))
                        {
                            // Format: ±hhmm
                        }
                        // Format: ±hh (minutes remain 0)
                        offset = new TimeSpan(sign * hours, sign * minutes, 0);
                        isUtc = true; // Has explicit timezone
                    }
                }
            }
        }

        // Add fractional ticks
        dateTime = dateTime.AddTicks(fractionTicks);

        // Convert to UTC
        if (isUtc)
        {
            // Subtract offset to get UTC (if +0530, subtract 5:30 to get UTC)
            dateTime = dateTime - offset;
            return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
        }

        // No timezone specified - assume UTC (most LDAP servers use UTC)
        return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
    }

    internal static List<string>? GetEntryAttributeStringValues(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count == 0) return null;

        // Strip null bytes and filter out empty strings (treat as "no value")
        var values = (from string value in entry.Attributes[attributeName].GetValues(typeof(string))
            let cleanedValue = value.Replace("\0", string.Empty)
            where !string.IsNullOrEmpty(cleanedValue)
            select cleanedValue).ToList();

        if (values.Count == 0)
            return null;

        // Deduplicate values defensively - LDAP multi-valued attributes should not contain duplicates
        // but corrupt data or bugs in source systems can cause this. Log when duplicates are detected.
        var uniqueValues = values.Distinct(StringComparer.Ordinal).ToList();
        if (uniqueValues.Count < values.Count)
        {
            var duplicateCount = values.Count - uniqueValues.Count;
            Log.Warning("GetEntryAttributeStringValues: Detected and removed {DuplicateCount} duplicate value(s) from attribute '{AttributeName}' on entry '{EntryDn}'. " +
                "Original count: {OriginalCount}, Unique count: {UniqueCount}",
                duplicateCount, attributeName, entry.DistinguishedName, values.Count, uniqueValues.Count);
        }

        return uniqueValues;
    }

    internal static List<byte[]>? GetEntryAttributeBinaryValues(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count == 0) return null;

        var binaryValues = (from byte[] value in entry.Attributes[attributeName].GetValues(typeof(byte[]))
            select value).ToList();

        if (binaryValues.Count == 0)
            return null;

        // Deduplicate values defensively - LDAP multi-valued attributes should not contain duplicates
        // Use a custom comparer for byte arrays since default equality doesn't work for arrays
        var uniqueValues = binaryValues.Distinct(ByteArrayComparer.Instance).ToList();
        if (uniqueValues.Count < binaryValues.Count)
        {
            var duplicateCount = binaryValues.Count - uniqueValues.Count;
            Log.Warning("GetEntryAttributeBinaryValues: Detected and removed {DuplicateCount} duplicate value(s) from attribute '{AttributeName}' on entry '{EntryDn}'. " +
                "Original count: {OriginalCount}, Unique count: {UniqueCount}",
                duplicateCount, attributeName, entry.DistinguishedName, binaryValues.Count, uniqueValues.Count);
        }

        return uniqueValues;
    }

    /// <summary>
    /// Comparer for byte arrays that compares by content rather than reference.
    /// </summary>
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            if (x.Length != y.Length) return false;
            return x.SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            if (obj == null) return 0;
            // Use a simple hash combining the length and first/last bytes
            var hash = obj.Length;
            if (obj.Length > 0) hash = (hash * 31) + obj[0];
            if (obj.Length > 1) hash = (hash * 31) + obj[^1];
            return hash;
        }
    }

    internal static List<int>? GetEntryAttributeIntValues(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count == 0) return null;

        // DirectoryAttribute.GetValues() only supports string or byte[] types, so get as strings and parse
        var result = new List<int>();
        foreach (string value in entry.Attributes[attributeName].GetValues(typeof(string)))
        {
            if (int.TryParse(value, out var intValue))
            {
                result.Add(intValue);
            }
        }

        if (result.Count == 0)
            return null;

        // Deduplicate values defensively - LDAP multi-valued attributes should not contain duplicates
        var uniqueValues = result.Distinct().ToList();
        if (uniqueValues.Count < result.Count)
        {
            var duplicateCount = result.Count - uniqueValues.Count;
            Log.Warning("GetEntryAttributeIntValues: Detected and removed {DuplicateCount} duplicate value(s) from attribute '{AttributeName}' on entry '{EntryDn}'. " +
                "Original count: {OriginalCount}, Unique count: {UniqueCount}",
                duplicateCount, attributeName, entry.DistinguishedName, result.Count, uniqueValues.Count);
        }

        return uniqueValues;
    }

    internal static List<long>? GetEntryAttributeLongValues(SearchResultEntry entry, string attributeName)
    {
        if (entry == null) return null;
        if (!entry.Attributes.Contains(attributeName)) return null;
        if (entry.Attributes[attributeName].Count == 0) return null;

        // DirectoryAttribute.GetValues() only supports string or byte[] types, so get as strings and parse
        var result = new List<long>();
        foreach (string value in entry.Attributes[attributeName].GetValues(typeof(string)))
        {
            if (long.TryParse(value, out var longValue))
            {
                result.Add(longValue);
            }
        }

        if (result.Count == 0)
            return null;

        // Deduplicate values defensively - LDAP multi-valued attributes should not contain duplicates
        var uniqueValues = result.Distinct().ToList();
        if (uniqueValues.Count < result.Count)
        {
            var duplicateCount = result.Count - uniqueValues.Count;
            Log.Warning("GetEntryAttributeLongValues: Detected and removed {DuplicateCount} duplicate value(s) from attribute '{AttributeName}' on entry '{EntryDn}'. " +
                "Original count: {OriginalCount}, Unique count: {UniqueCount}",
                duplicateCount, attributeName, entry.DistinguishedName, result.Count, uniqueValues.Count);
        }

        return uniqueValues;
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

    /// <summary>
    /// Queries the rootDSE to detect directory type (Active Directory, Samba AD, or generic LDAP).
    /// Used by schema discovery to apply directory-specific attribute overrides.
    /// </summary>
    internal static LdapConnectorRootDse GetBasicRootDseInformation(LdapConnection connection, ILogger logger)
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.AddRange(["supportedCapabilities", "vendorName"]);

        var response = (SearchResponse)connection.SendRequest(request);

        if (response?.Entries.Count == 0 || response == null)
        {
            logger.Warning("GetBasicRootDseInformation: Could not query rootDSE. Directory type detection unavailable.");
            return new LdapConnectorRootDse();
        }

        var rootDseEntry = response.Entries[0];

        var capabilities = GetEntryAttributeStringValues(rootDseEntry, "supportedCapabilities");
        var isActiveDirectory = capabilities != null &&
            (capabilities.Contains(LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_OID) ||
             capabilities.Contains(LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_ADAM_OID));

        var vendorName = GetEntryAttributeStringValue(rootDseEntry, "vendorName");

        var isSambaAd = vendorName != null &&
            vendorName.Contains("Samba", StringComparison.OrdinalIgnoreCase);
        var supportsPaging = isActiveDirectory && !isSambaAd;

        var rootDse = new LdapConnectorRootDse
        {
            IsActiveDirectory = isActiveDirectory,
            VendorName = vendorName,
            SupportsPaging = supportsPaging
        };

        logger.Debug("GetBasicRootDseInformation: IsActiveDirectory={IsAd}, VendorName={VendorName}",
            rootDse.IsActiveDirectory, rootDse.VendorName ?? "(not set)");

        return rootDse;
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

    /// <summary>
    /// Determines whether an attribute's plurality should be overridden from multi-valued to single-valued
    /// based on Active Directory SAM layer enforcement rules.
    /// </summary>
    /// <param name="attributeName">The LDAP attribute name (e.g., "description").</param>
    /// <param name="objectTypeName">The structural object class name (e.g., "user", "group").</param>
    /// <param name="isActiveDirectory">Whether the directory is Active Directory (AD-DS, AD-LDS, or Samba AD).</param>
    /// <returns>True if the attribute should be treated as single-valued despite the LDAP schema declaring it as multi-valued.</returns>
    internal static bool ShouldOverridePluralityToSingleValued(string attributeName, string objectTypeName, bool isActiveDirectory)
    {
        return isActiveDirectory &&
               LdapConnectorConstants.SAM_ENFORCED_SINGLE_VALUED_ATTRIBUTES.Contains(attributeName) &&
               LdapConnectorConstants.SAM_MANAGED_OBJECT_CLASSES.Contains(objectTypeName);
    }

    /// <summary>
    /// Determines the writability of an LDAP attribute based on its schema metadata.
    /// An attribute is read-only if any of the following are true:
    /// - systemOnly is TRUE (server-managed attribute, e.g. objectGUID, whenCreated)
    /// - systemFlags has the constructed bit set (0x4) (computed attribute, e.g. canonicalName, tokenGroups)
    /// - linkID is an odd number (back-link attribute, e.g. memberOf — must be modified from the forward-link side)
    /// </summary>
    /// <param name="systemOnly">The value of the systemOnly attribute on the attributeSchema entry (TRUE/FALSE string, or null).</param>
    /// <param name="systemFlags">The value of the systemFlags attribute on the attributeSchema entry (integer, or null).</param>
    /// <param name="linkId">The value of the linkID attribute on the attributeSchema entry (integer, or null).</param>
    /// <returns>The writability classification for the attribute.</returns>
    internal static AttributeWritability DetermineAttributeWritability(bool? systemOnly, int? systemFlags, int? linkId)
    {
        if (systemOnly == true)
            return AttributeWritability.ReadOnly;

        if (systemFlags.HasValue && (systemFlags.Value & LdapConnectorConstants.SYSTEM_FLAGS_CONSTRUCTED) != 0)
            return AttributeWritability.ReadOnly;

        if (linkId.HasValue && linkId.Value % 2 != 0)
            return AttributeWritability.ReadOnly;

        return AttributeWritability.Writable;
    }

    internal static AttributeDataType GetLdapAttributeDataType(int omSyntax)
    {
        // map the directory omSyntax to an attribute data type
        // https://social.technet.microsoft.com/wiki/contents/articles/52570.active-directory-syntaxes-of-attributes.aspx
        return omSyntax switch
        {
            1 or 10 => AttributeDataType.Boolean,
            2 => AttributeDataType.Number,  // Integer (32-bit)
            65 => AttributeDataType.LongNumber,  // Large Integer (64-bit) - accountExpires, pwdLastSet, lastLogon, etc.
            3 or 4 or 66 => AttributeDataType.Binary, // 3 = Binary, 4 = OctetString (photo, objectSid, logonHours), 66 = Object(Replica-Link) (nTSecurityDescriptor)
            6 or 18 or 19 or 20 or 22 or 27 or 64 => AttributeDataType.Text,
            23 or 24 => AttributeDataType.DateTime,
            127 => AttributeDataType.Reference,
            _ => throw new InvalidDataException("Unsupported omSyntax value: " + omSyntax),
        };
    }

    /// <summary>
    /// Parses a distinguished name into its RDN and parent DN components.
    /// For example: "CN=John Smith,OU=Users,DC=example,DC=com" returns ("CN=John Smith", "OU=Users,DC=example,DC=com")
    /// </summary>
    internal static (string? Rdn, string? ParentDn) ParseDistinguishedName(string dn)
    {
        if (string.IsNullOrEmpty(dn))
            return (null, null);

        // Find the first unescaped comma to split RDN from parent
        var commaIndex = FindUnescapedComma(dn);

        if (commaIndex == -1)
        {
            // No comma found - the entire DN is the RDN (root object)
            return (dn, null);
        }

        var rdn = dn.Substring(0, commaIndex);
        var parentDn = dn.Substring(commaIndex + 1);

        return (rdn, parentDn);
    }

    /// <summary>
    /// Finds the index of the first unescaped comma in a DN string.
    /// Commas can be escaped with backslash (\,) in LDAP DNs.
    /// </summary>
    internal static int FindUnescapedComma(string dn)
    {
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == ',')
            {
                // Check if this comma is escaped (preceded by backslash)
                if (i == 0 || dn[i - 1] != '\\')
                {
                    return i;
                }
            }
        }
        return -1;
    }
}