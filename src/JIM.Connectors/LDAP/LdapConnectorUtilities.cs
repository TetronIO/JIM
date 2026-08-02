// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Text.Json;
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
                duplicateCount, attributeName, LogSanitiser.Sanitise(entry.DistinguishedName), guidValues.Count, uniqueValues.Count);
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
                duplicateCount, attributeName, LogSanitiser.Sanitise(entry.DistinguishedName), values.Count, uniqueValues.Count);
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
                duplicateCount, attributeName, LogSanitiser.Sanitise(entry.DistinguishedName), binaryValues.Count, uniqueValues.Count);
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
                duplicateCount, attributeName, LogSanitiser.Sanitise(entry.DistinguishedName), result.Count, uniqueValues.Count);
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
                duplicateCount, attributeName, LogSanitiser.Sanitise(entry.DistinguishedName), result.Count, uniqueValues.Count);
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
    /// Determines the <see cref="LdapDirectoryType"/> from rootDSE capabilities and vendor information.
    /// </summary>
    /// <param name="supportedCapabilities">OIDs from the rootDSE supportedCapabilities attribute.</param>
    /// <param name="vendorName">The vendorName attribute from rootDSE (may be null).</param>
    /// <param name="structuralObjectClass">The structuralObjectClass from rootDSE (may be null). OpenLDAP uses "OpenLDAProotDSE".</param>
    internal static LdapDirectoryType DetectDirectoryType(IEnumerable<string>? supportedCapabilities, string? vendorName, string? structuralObjectClass = null)
    {
        var hasAdCapability = supportedCapabilities != null &&
            (supportedCapabilities.Contains(LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_OID) ||
             supportedCapabilities.Contains(LdapConnectorConstants.LDAP_CAP_ACTIVE_DIRECTORY_ADAM_OID));

        if (hasAdCapability)
        {
            // Samba AD advertises the AD capability OID but has different behaviour
            var isSamba = vendorName != null &&
                vendorName.Contains("Samba", StringComparison.OrdinalIgnoreCase);
            return isSamba ? LdapDirectoryType.SambaAD : LdapDirectoryType.ActiveDirectory;
        }

        // Check vendorName first (set by some OpenLDAP configurations)
        if (vendorName != null &&
            vendorName.Contains("OpenLDAP", StringComparison.OrdinalIgnoreCase))
        {
            return LdapDirectoryType.OpenLDAP;
        }

        // Fallback: check structuralObjectClass on rootDSE — OpenLDAP uses "OpenLDAProotDSE"
        if (structuralObjectClass != null &&
            structuralObjectClass.Contains("OpenLDAP", StringComparison.OrdinalIgnoreCase))
        {
            return LdapDirectoryType.OpenLDAP;
        }

        return LdapDirectoryType.Generic;
    }

    /// <summary>
    /// Queries the rootDSE to detect directory type and basic capabilities.
    /// Used by schema discovery to apply directory-specific attribute overrides.
    /// </summary>
    internal static LdapConnectorRootDse GetBasicRootDseInformation(LdapConnection connection, ILogger logger)
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.AddRange(["supportedCapabilities", "vendorName", "structuralObjectClass", "DNSHostName"]);

        var response = (SearchResponse)connection.SendRequest(request);

        if (response?.Entries.Count == 0 || response == null)
        {
            logger.Warning("GetBasicRootDseInformation: Could not query rootDSE. Directory type detection unavailable.");
            return new LdapConnectorRootDse();
        }

        var rootDseEntry = response.Entries[0];

        var capabilities = GetEntryAttributeStringValues(rootDseEntry, "supportedCapabilities");
        var vendorName = GetEntryAttributeStringValue(rootDseEntry, "vendorName");
        var structuralObjectClass = GetEntryAttributeStringValue(rootDseEntry, "structuralObjectClass");
        var dnsHostName = GetEntryAttributeStringValue(rootDseEntry, "DNSHostName");

        var directoryType = DetectDirectoryType(capabilities, vendorName, structuralObjectClass);

        var rootDse = new LdapConnectorRootDse
        {
            DirectoryType = directoryType,
            VendorName = vendorName,
            DnsHostName = dnsHostName
        };

        logger.Debug("GetBasicRootDseInformation: DirectoryType={DirectoryType}, VendorName={VendorName}",
            rootDse.DirectoryType, rootDse.VendorName ?? "(not set)");

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
    /// <param name="directoryType">The detected directory type.</param>
    /// <returns>True if the attribute should be treated as single-valued despite the LDAP schema declaring it as multi-valued.</returns>
    internal static bool ShouldOverridePluralityToSingleValued(string attributeName, string objectTypeName, LdapDirectoryType directoryType)
    {
        return directoryType is LdapDirectoryType.ActiveDirectory or LdapDirectoryType.SambaAD &&
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

        if (!LdapDistinguishedName.TryParse(dn, out var parsedDn))
            return (null, null);

        return (parsedDn.LeafRdn.Source, parsedDn.Parent?.ToString());
    }

    /// <summary>
    /// Validates that a Distinguished Name does not contain empty RDN values.
    /// Empty RDN values (e.g., "OU=,OU=Users,...") are invalid and will be rejected by LDAP servers.
    /// </summary>
    /// <param name="dn">The Distinguished Name to validate.</param>
    /// <returns>True if the DN is valid (no empty RDN values); false otherwise.</returns>
    internal static bool HasValidRdnValues(string dn)
    {
        if (string.IsNullOrEmpty(dn))
            return false;

        if (!LdapDistinguishedName.TryParse(dn, out var parsedDn))
            return false;

        // A malformed DN fails to parse above; here we reject any component whose value is empty or whitespace.
        return parsedDn.Rdns.All(rdn => rdn.Components.All(component => !string.IsNullOrWhiteSpace(component.Value)));
    }

    /// <summary>
    /// Generates a fallback accesslog timestamp for when the cn=accesslog database is empty
    /// (e.g., after snapshot restore clears stale accesslog data). Returns the current UTC time
    /// formatted as LDAP generalised time (YYYYMMDDHHmmSS.ffffffZ), which serves as the
    /// watermark for the next delta import: "no changes happened before this point."
    /// </summary>
    internal static string GenerateAccesslogFallbackTimestamp()
    {
        return DateTime.UtcNow.ToString("yyyyMMddHHmmss.ffffffZ");
    }

    /// <summary>
    /// Guards a USN-based delta import against silently running against a different domain controller
    /// than the one that produced the persisted watermark. AD/Samba AD USNs are scoped to the issuing
    /// DC's invocationId, so a DNS round-robin Host resolving to a different DC (or a DC restored from
    /// backup, which is issued a new invocationId) would otherwise make the persisted
    /// <see cref="LdapConnectorRootDse.HighestCommittedUsn"/> meaningless: subsequent USN comparisons
    /// against the new DC can silently skip changes or re-import stale ones.
    /// </summary>
    /// <remarks>
    /// Comparison order:
    /// <list type="number">
    ///   <item>Both runs' <see cref="LdapConnectorRootDse.InvocationId"/> are present: compare directly.
    ///   A mismatch is conclusive (different DC, or the same DC restored from backup) and throws.</item>
    ///   <item>Either invocationId is missing (a baseline persisted before this guard was added, or the
    ///   current run's invocationId query failed, for example due to permissions): fall back to a
    ///   case-insensitive comparison of <see cref="LdapConnectorRootDse.DnsHostName"/>. A mismatch throws.
    ///   This catches the detectable subset of mismatches even when the stronger signal is unavailable;
    ///   it cannot detect a restore of the same DC.</item>
    ///   <item>Neither comparison is possible: proceed without failing the import, logging a warning that
    ///   identity could not be verified. Missing data must never itself fail a delta import.</item>
    /// </list>
    /// </remarks>
    /// <param name="previousRootDse">The RootDSE info persisted by the run that produced the current watermark.</param>
    /// <param name="currentRootDse">The RootDSE info just queried on the connection about to perform the delta import.</param>
    /// <param name="logger">Logger for diagnostics; identifiers sourced from the directory are sanitised before logging.</param>
    /// <exception cref="CannotPerformDeltaImportException">The domain controller identity has changed between the two runs.</exception>
    internal static void VerifyDomainControllerIdentity(LdapConnectorRootDse previousRootDse, LdapConnectorRootDse currentRootDse, ILogger logger)
    {
        var previousInvocationId = previousRootDse.InvocationId;
        var currentInvocationId = currentRootDse.InvocationId;

        if (previousInvocationId.HasValue && currentInvocationId.HasValue)
        {
            if (previousInvocationId.Value == currentInvocationId.Value)
                return;

            logger.Warning("VerifyDomainControllerIdentity: Domain controller invocationId mismatch. Previous: {PreviousInvocationId}, Current: {CurrentInvocationId}.",
                previousInvocationId.Value, currentInvocationId.Value);

            throw new CannotPerformDeltaImportException(
                $"Delta import aborted: the domain controller's invocationId has changed since the watermark was recorded " +
                $"(previous: {previousInvocationId.Value}, current: {currentInvocationId.Value}). This can happen when a domain " +
                "name configured as Host resolves to a different domain controller (DNS round-robin), or the domain controller " +
                "was restored from backup. Run a Full Import to re-establish the delta baseline.");
        }

        // The invocationId pair is incomplete (a baseline persisted before this guard was added, or the
        // current run's invocationId query failed); fall back to comparing the DC hostnames. Weaker (it
        // cannot detect a restore of the same DC) but catches the detectable subset of mismatches.
        var previousHostname = previousRootDse.DnsHostName;
        var currentHostname = currentRootDse.DnsHostName;

        if (!string.IsNullOrEmpty(previousHostname) && !string.IsNullOrEmpty(currentHostname))
        {
            if (string.Equals(previousHostname, currentHostname, StringComparison.OrdinalIgnoreCase))
                return;

            logger.Warning("VerifyDomainControllerIdentity: Domain controller hostname mismatch (invocationId not available for comparison). Previous: {PreviousHostname}, Current: {CurrentHostname}.",
                LogSanitiser.Sanitise(previousHostname), LogSanitiser.Sanitise(currentHostname));

            throw new CannotPerformDeltaImportException(
                $"Delta import aborted: the domain controller hostname has changed since the watermark was recorded " +
                $"(previous: {previousHostname}, current: {currentHostname}). This can happen when a domain name configured " +
                "as Host resolves to a different domain controller (DNS round-robin). Run a Full Import to re-establish the delta baseline.");
        }

        logger.Warning("VerifyDomainControllerIdentity: Could not verify domain controller identity between the previous watermark and the current " +
            "connection (no comparable invocationId or hostname pair was available). Proceeding without domain controller mismatch verification.");
    }

    /// <summary>
    /// Guards an AD/Samba AD import against silently running against a Partition the connected domain
    /// controller does not host. AD's crossRef-based partition discovery (CN=Partitions,
    /// CN=Configuration) lists every domain in the forest, including domains the connected domain
    /// controller does not hold a naming context for; a domain controller does not chase referrals, so an
    /// import against a foreign partition would otherwise silently return zero objects, a fast/hard
    /// failure over silent corruption is required (see Synchronisation Integrity, root CLAUDE.md).
    /// </summary>
    /// <remarks>
    /// Applies only to AD-family directories (<see cref="LdapConnectorRootDse.UseUsnDeltaImport"/>); the
    /// standard RFC 4512 namingContexts partition discovery used for other directory types has no
    /// equivalent forest-wide-visibility problem. When <paramref name="currentRootDse"/>'s
    /// <see cref="LdapConnectorRootDse.NamingContexts"/> is null or empty (the rootDSE query did not
    /// return the attribute, for example due to insufficient permissions), hosting cannot be verified: a
    /// warning is logged and the import proceeds, because missing data must never itself fail an import.
    /// Otherwise, every selected Partition whose DN is not present in <c>NamingContexts</c> (case-
    /// insensitive ordinal comparison) is reported by name in a single exception, alongside the connected
    /// server and guidance to use one Connected System per domain.
    /// </remarks>
    /// <param name="currentRootDse">The RootDSE info just queried on the connection about to perform the import.</param>
    /// <param name="selectedPartitions">The Partitions the import is about to run against.</param>
    /// <param name="logger">Logger for diagnostics; identifiers sourced from the directory are sanitised before logging.</param>
    /// <exception cref="PartitionNotHostedException">One or more selected Partitions are not hosted by the connected domain controller.</exception>
    internal static void VerifyPartitionsAreHostedByConnectedServer(
        LdapConnectorRootDse currentRootDse,
        IEnumerable<ConnectedSystemPartition> selectedPartitions,
        ILogger logger)
    {
        if (!currentRootDse.UseUsnDeltaImport)
            return;

        if (currentRootDse.NamingContexts == null || currentRootDse.NamingContexts.Count == 0)
        {
            logger.Warning("VerifyPartitionsAreHostedByConnectedServer: The connected server did not return its hosted naming contexts. " +
                "Partition hosting could not be verified. Proceeding without this check.");
            return;
        }

        var hostedNamingContexts = currentRootDse.NamingContexts;
        var unhostedPartitions = selectedPartitions
            .Where(p => !hostedNamingContexts.Contains(p.ExternalId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unhostedPartitions.Count == 0)
            return;

        var partitionNames = string.Join(", ", unhostedPartitions.Select(p => p.Name));

        logger.Warning("VerifyPartitionsAreHostedByConnectedServer: Selected Partition(s) not hosted by the connected server {Server}: {Partitions}.",
            LogSanitiser.Sanitise(currentRootDse.DnsHostName), LogSanitiser.Sanitise(partitionNames));

        throw new PartitionNotHostedException(
            $"Import aborted: the following selected Partition(s) are not hosted by the connected domain controller " +
            $"'{currentRootDse.DnsHostName}': {partitionNames}. A domain controller only holds its own domain's naming " +
            "context and does not chase referrals to other domains in the forest, so an import against a Partition it " +
            "does not host would otherwise silently return zero objects. A domain's objects must be managed through a " +
            "Connected System whose Host targets that domain's own domain controllers (one Connected System per domain " +
            "today).");
    }

    /// <summary>
    /// Reads configurationNamingContext from the rootDSE, the DN of the Configuration partition
    /// ("CN=Configuration,DC=..."), used to locate the CN=Sites subtree for domain controller discovery
    /// (issue #1167).
    /// </summary>
    internal static string? GetConfigurationNamingContext(LdapConnection connection, ILogger logger)
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.Add("configurationNamingContext");
        var response = (SearchResponse)connection.SendRequest(request);

        if (response.ResultCode != ResultCode.Success)
        {
            logger.Warning("GetConfigurationNamingContext: No success. Result code: {ResultCode}", response.ResultCode);
            return null;
        }

        if (response.Entries.Count == 0)
        {
            logger.Warning("GetConfigurationNamingContext: Didn't get any results!");
            return null;
        }

        return GetEntryAttributeStringValue(response.Entries[0], "configurationNamingContext");
    }

    /// <summary>
    /// Derives the Distinguished Name of the server object that owns an nTDSDSA (NTDS Settings) object, used
    /// during domain controller discovery (issue #1167). An nTDSDSA object's DN takes the shape
    /// "CN=NTDS Settings,CN=&lt;server&gt;,CN=Servers,CN=&lt;site&gt;,CN=Sites,CN=Configuration,..."; the
    /// server object is its immediate parent.
    /// </summary>
    /// <param name="ntdsDsaDn">The Distinguished Name of an nTDSDSA object.</param>
    /// <returns>The server object's Distinguished Name, or null if <paramref name="ntdsDsaDn"/> does not parse, or has no parent (a single-RDN DN, which an nTDSDSA object can never genuinely be).</returns>
    internal static string? GetServerDnFromNtdsDsaDn(string ntdsDsaDn)
    {
        return LdapDistinguishedName.TryParse(ntdsDsaDn, out var parsed) ? parsed.Parent?.ToString() : null;
    }

    /// <summary>
    /// Derives the Active Directory Site name an nTDSDSA object belongs to, used during domain controller
    /// discovery (issue #1167). The Site name is the DN component three levels up from the nTDSDSA object:
    /// "CN=NTDS Settings,CN=&lt;server&gt;,CN=Servers,CN=&lt;site&gt;,CN=Sites,...".
    /// </summary>
    /// <param name="ntdsDsaDn">The Distinguished Name of an nTDSDSA object.</param>
    /// <returns>The Site name, or null if <paramref name="ntdsDsaDn"/> does not parse, or does not have at least four RDN components above the nTDSDSA object itself.</returns>
    internal static string? GetSiteNameFromNtdsDsaDn(string ntdsDsaDn)
    {
        if (!LdapDistinguishedName.TryParse(ntdsDsaDn, out var parsed))
            return null;

        // parsed is "CN=NTDS Settings,...". Three levels up: Parent = server, Parent.Parent = CN=Servers,
        // Parent.Parent.Parent = CN=<site>.
        var siteRdn = parsed.Parent?.Parent?.Parent?.LeafRdn;
        return siteRdn?.Components.Count > 0 ? siteRdn.Components[0].Value : null;
    }

    /// <summary>
    /// Maps discovered nTDSDSA objects (paired with the dNSHostName read from each one's parent server object)
    /// into the <see cref="ConnectorDirectoryServer"/> list JIM's Discover Domain Controllers action shows an
    /// administrator (issue #1167). Kept independent of any live LDAP connection so the mapping is unit
    /// testable: the only inputs are the nTDSDSA DN (Site is derived from it) and whatever dNSHostName value
    /// (if any) was read for its server object.
    /// </summary>
    /// <param name="entries">Each nTDSDSA object's own DN, paired with the dNSHostName read from its parent server object (null when the server object had none, or could not be read).</param>
    /// <param name="logger">Logger for a skipped-entry warning; DNs are sanitised before logging.</param>
    /// <returns>One <see cref="ConnectorDirectoryServer"/> per entry with a usable dNSHostName, ordered by hostname. Entries with no dNSHostName are skipped: JIM has nothing to offer the administrator for a domain controller it cannot name.</returns>
    internal static List<ConnectorDirectoryServer> MapNtdsDsaEntriesToDirectoryServers(
        IEnumerable<(string NtdsDsaDn, string? DnsHostName)> entries,
        ILogger logger)
    {
        // Materialised once: it is enumerated twice below (the warning pass, then the projection), and the
        // parameter type is IEnumerable so a caller-supplied lazy sequence must not be evaluated twice over.
        var entryList = entries.ToList();

        foreach (var skipped in entryList.Where(e => string.IsNullOrEmpty(e.DnsHostName)))
        {
            logger.Warning("MapNtdsDsaEntriesToDirectoryServers: The server object for nTDSDSA object '{Dn}' has no dNSHostName. Skipping; JIM cannot offer a domain controller it cannot name.",
                LogSanitiser.Sanitise(skipped.NtdsDsaDn));
        }

        return entryList
            .Where(e => !string.IsNullOrEmpty(e.DnsHostName))
            .Select(e => new ConnectorDirectoryServer
            {
                HostName = e.DnsHostName!,
                Site = GetSiteNameFromNtdsDsaDn(e.NtdsDsaDn)
            })
            .OrderBy(s => s.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolves which server a connection should be opened against, and why (issue #230 Phase 2). This
    /// is the single point where the domain controller/directory server for a connection is decided, so
    /// that the connection factory (and therefore every parallel connection in a run) resolves to the
    /// same server.
    /// </summary>
    /// <remarks>
    /// Priority order:
    /// <list type="number">
    ///   <item>A non-blank Preferred Domain Controller setting always wins: the administrator has
    ///   explicitly chosen, so no other source is even consulted.</item>
    ///   <item>Otherwise, a domain controller pinned in <paramref name="persistedConnectorData"/> from a
    ///   previous connection is used. Malformed persisted data (an old format, or corruption) is
    ///   tolerated: the specific exception is caught, a warning logged, and resolution falls through to
    ///   Host exactly as if no pin existed - a deserialisation failure must never itself fail a
    ///   connection attempt.</item>
    ///   <item>Otherwise, the configured Host setting is used, as it always was before pinning existed.</item>
    /// </list>
    /// </remarks>
    /// <param name="preferredDomainController">The "Preferred Domain Controller" setting value, or null/blank if not configured.</param>
    /// <param name="persistedConnectorData">The persisted connector data replayed for this connection, or null.</param>
    /// <param name="host">The configured Host setting value; the final fallback.</param>
    /// <param name="logger">Logger for the resolution decision; server strings are sanitised before logging.</param>
    /// <returns>The server to connect to, and which source it came from.</returns>
    internal static (string Server, LdapServerResolutionSource Source) ResolveEffectiveServer(
        string? preferredDomainController,
        string? persistedConnectorData,
        string host,
        ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(preferredDomainController))
        {
            logger.Information("ResolveEffectiveServer: Connecting via the configured Preferred Domain Controller {Server}.",
                LogSanitiser.Sanitise(preferredDomainController));
            return (preferredDomainController, LdapServerResolutionSource.PreferredSetting);
        }

        if (!string.IsNullOrEmpty(persistedConnectorData))
        {
            LdapConnectorRootDse? previousRootDse = null;
            try
            {
                previousRootDse = JsonSerializer.Deserialize<LdapConnectorRootDse>(persistedConnectorData);
            }
            catch (JsonException ex)
            {
                logger.Warning(ex, "ResolveEffectiveServer: Failed to deserialise persisted connector data while looking for a pinned domain controller. Falling back to the configured Host.");
            }

            if (!string.IsNullOrEmpty(previousRootDse?.PinnedDirectoryServer))
            {
                logger.Information("ResolveEffectiveServer: Connecting via the pinned domain controller {Server}.",
                    LogSanitiser.Sanitise(previousRootDse.PinnedDirectoryServer));
                return (previousRootDse.PinnedDirectoryServer, LdapServerResolutionSource.Pinned);
            }
        }

        logger.Information("ResolveEffectiveServer: Connecting via the configured Host {Server}.", LogSanitiser.Sanitise(host));
        return (host, LdapServerResolutionSource.Host);
    }

    /// <summary>
    /// Decides the <see cref="LdapConnectorRootDse.PinnedDirectoryServer"/> value a full or delta import
    /// should persist this run (issue #230 Phase 2). Pinning only ever applies to AD-family directories
    /// (USN-based delta import); non-AD-family directories never pin.
    /// </summary>
    /// <remarks>
    /// When no Preferred Domain Controller is configured, this returns <paramref name="dnsHostName"/> -
    /// the domain controller the current connection actually reached, whether resolved via Host (first
    /// connection, or a prior pin was just invalidated) or via an existing pin. Because the connection
    /// was opened via whichever server was resolved, returning that same server here both creates the pin
    /// on a first-ever connection and self-heals/re-affirms it on every subsequent run.
    /// <para>
    /// When a Preferred Domain Controller IS configured, this returns null: the setting owns domain
    /// controller selection, so a pin recorded under a previous configuration (or before the setting was
    /// introduced) must not survive into the new baseline.
    /// </para>
    /// </remarks>
    /// <param name="useUsnDeltaImport">Whether the connected directory is AD-family (<see cref="LdapConnectorRootDse.UseUsnDeltaImport"/>).</param>
    /// <param name="preferredDomainController">The "Preferred Domain Controller" setting value, or null/blank if not configured.</param>
    /// <param name="dnsHostName">The dnsHostName of the domain controller this connection reached.</param>
    /// <returns>The value to persist as <see cref="LdapConnectorRootDse.PinnedDirectoryServer"/>.</returns>
    internal static string? ResolvePinnedDirectoryServerForImport(bool useUsnDeltaImport, string? preferredDomainController, string? dnsHostName)
    {
        if (!useUsnDeltaImport)
            return null;

        return string.IsNullOrWhiteSpace(preferredDomainController) ? dnsHostName : null;
    }

    /// <summary>
    /// Updates only the <see cref="LdapConnectorRootDse.PinnedDirectoryServer"/> field of persisted
    /// connector data, leaving every other field (the USN/changelog/accesslog watermarks, invocationId,
    /// directory type, vendor name) exactly as replayed (issue #230 Phase 2). Used by both the export-path
    /// pin creation (<c>CloseExportConnection</c>) and the pin invalidation on a failed pinned connection
    /// (<c>CloseImportConnection</c>/<c>CloseExportConnection</c>): the two callers differ only in whether
    /// they pass a new pin or null.
    /// </summary>
    /// <remarks>
    /// Preserving the watermark fields byte-for-byte in meaning is a correctness requirement: a regressed
    /// watermark corrupts delta imports. Malformed or absent previous data is tolerated (the specific
    /// deserialisation exception is caught and a warning logged) by starting from a minimal record that
    /// carries only the pin and <paramref name="fallbackDirectoryType"/> - there is nothing else to
    /// recover from data that could not be read.
    /// </remarks>
    /// <param name="persistedConnectorData">The persisted connector data to update, or null if none exists yet.</param>
    /// <param name="newPinnedDirectoryServer">The new pin value; null clears the pin.</param>
    /// <param name="fallbackDirectoryType">The directory type to record when no previous data can be recovered.</param>
    /// <param name="logger">Logger for a deserialisation failure warning.</param>
    /// <returns>The updated persisted connector data JSON.</returns>
    internal static string MergePinnedDirectoryServerIntoPersistedData(
        string? persistedConnectorData,
        string? newPinnedDirectoryServer,
        LdapDirectoryType fallbackDirectoryType,
        ILogger logger)
    {
        LdapConnectorRootDse? rootDse = null;

        if (!string.IsNullOrEmpty(persistedConnectorData))
        {
            try
            {
                rootDse = JsonSerializer.Deserialize<LdapConnectorRootDse>(persistedConnectorData);
            }
            catch (JsonException ex)
            {
                logger.Warning(ex, "MergePinnedDirectoryServerIntoPersistedData: Failed to deserialise persisted connector data. Replacing it with a minimal record carrying only the pin and directory type.");
            }
        }

        rootDse ??= new LdapConnectorRootDse { DirectoryType = fallbackDirectoryType };
        rootDse.PinnedDirectoryServer = newPinnedDirectoryServer;
        return JsonSerializer.Serialize(rootDse);
    }
}