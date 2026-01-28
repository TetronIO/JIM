namespace JIM.Utilities;

/// <summary>
/// Centralised utility for parsing and converting GUIDs/UUIDs across different byte order conventions.
/// </summary>
/// <remarks>
/// <para>
/// GUIDs (Microsoft) and UUIDs (RFC 4122) are both 128-bit identifiers with identical string representations,
/// but their binary representations differ in byte ordering for the first three components:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Component</term>
///     <description>RFC 4122 (Big-endian) vs Microsoft (Little-endian)</description>
///   </listheader>
///   <item>
///     <term>time_low (4 bytes)</term>
///     <description>Big-endian in RFC 4122, Little-endian in Microsoft</description>
///   </item>
///   <item>
///     <term>time_mid (2 bytes)</term>
///     <description>Big-endian in RFC 4122, Little-endian in Microsoft</description>
///   </item>
///   <item>
///     <term>time_hi_version (2 bytes)</term>
///     <description>Big-endian in RFC 4122, Little-endian in Microsoft</description>
///   </item>
///   <item>
///     <term>clock_seq + node (8 bytes)</term>
///     <description>Big-endian in both formats</description>
///   </item>
/// </list>
/// <para>
/// <b>When to use which method:</b>
/// </para>
/// <list type="bullet">
///   <item><see cref="FromMicrosoftBytes"/>: Active Directory objectGUID, SQL Server uniqueidentifier</item>
///   <item><see cref="FromRfc4122Bytes"/>: OpenLDAP binary UUID attributes, PostgreSQL binary uuid, Oracle RAW(16)</item>
///   <item><see cref="FromString"/>/<see cref="TryFromString"/>: CSV files, JSON APIs, SCIM, OpenLDAP entryUUID (string)</item>
/// </list>
/// </remarks>
public static class IdentifierParser
{
    /// <summary>
    /// Parses a GUID from its string representation.
    /// </summary>
    /// <param name="value">The string representation of the GUID. Supports standard formats:
    /// hyphenated (550e8400-e29b-41d4-a716-446655440000), braced ({550e8400-e29b-41d4-a716-446655440000}),
    /// no hyphens (550e8400e29b41d4a716446655440000), and URN (urn:uuid:550e8400-e29b-41d4-a716-446655440000).</param>
    /// <returns>The parsed GUID.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is not a valid GUID string.</exception>
    public static Guid FromString(string value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        var trimmed = value.Trim();

        // Handle URN format: urn:uuid:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        if (trimmed.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(9);

        if (Guid.TryParse(trimmed, out var result))
            return result;

        throw new ArgumentException($"The value '{value}' is not a valid GUID/UUID string.", nameof(value));
    }

    /// <summary>
    /// Attempts to parse a GUID from its string representation.
    /// </summary>
    /// <param name="value">The string representation of the GUID.</param>
    /// <param name="result">When this method returns, contains the parsed GUID if successful; otherwise, <see cref="Guid.Empty"/>.</param>
    /// <returns><c>true</c> if the parsing was successful; otherwise, <c>false</c>.</returns>
    public static bool TryFromString(string? value, out Guid result)
    {
        result = Guid.Empty;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();

        // Handle URN format: urn:uuid:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
        if (trimmed.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(9);

        return Guid.TryParse(trimmed, out result);
    }

    /// <summary>
    /// Creates a GUID from a 16-byte array in Microsoft GUID byte order.
    /// </summary>
    /// <remarks>
    /// Microsoft GUID byte order uses little-endian for the first three components (time_low, time_mid, time_hi_version).
    /// This is the native format for:
    /// <list type="bullet">
    ///   <item>Active Directory <c>objectGUID</c></item>
    ///   <item>Samba AD <c>objectGUID</c></item>
    ///   <item>SQL Server <c>uniqueidentifier</c></item>
    ///   <item>.NET <see cref="Guid.ToByteArray()"/> output</item>
    /// </list>
    /// </remarks>
    /// <param name="bytes">A 16-byte array in Microsoft GUID byte order.</param>
    /// <returns>The GUID.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="bytes"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static Guid FromMicrosoftBytes(byte[] bytes)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));

        if (bytes.Length != 16)
            throw new ArgumentException($"GUID byte array must be exactly 16 bytes, but was {bytes.Length} bytes.", nameof(bytes));

        return new Guid(bytes);
    }

    /// <summary>
    /// Creates a GUID from a 16-byte array in RFC 4122 UUID byte order (big-endian first three components).
    /// </summary>
    /// <remarks>
    /// RFC 4122 UUID byte order uses big-endian for the first three components (time_low, time_mid, time_hi_version).
    /// This is the native format for:
    /// <list type="bullet">
    ///   <item>OpenLDAP binary UUID attributes (custom attributes, not <c>entryUUID</c> which is a string)</item>
    ///   <item>PostgreSQL <c>uuid</c> type when read as binary</item>
    ///   <item>Oracle <c>RAW(16)</c> UUID storage</item>
    ///   <item>Most non-Microsoft platforms</item>
    /// </list>
    /// </remarks>
    /// <param name="bytes">A 16-byte array in RFC 4122 UUID byte order.</param>
    /// <returns>The GUID.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="bytes"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bytes"/> is not exactly 16 bytes.</exception>
    public static Guid FromRfc4122Bytes(byte[] bytes)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));

        if (bytes.Length != 16)
            throw new ArgumentException($"UUID byte array must be exactly 16 bytes, but was {bytes.Length} bytes.", nameof(bytes));

        // Convert from RFC 4122 (big-endian first 3 components) to Microsoft (little-endian first 3 components)
        var converted = new byte[16];

        // time_low (bytes 0-3): reverse for endianness swap
        converted[0] = bytes[3];
        converted[1] = bytes[2];
        converted[2] = bytes[1];
        converted[3] = bytes[0];

        // time_mid (bytes 4-5): reverse for endianness swap
        converted[4] = bytes[5];
        converted[5] = bytes[4];

        // time_hi_version (bytes 6-7): reverse for endianness swap
        converted[6] = bytes[7];
        converted[7] = bytes[6];

        // clock_seq_hi_res, clock_seq_low, and node (bytes 8-15): same in both formats
        Array.Copy(bytes, 8, converted, 8, 8);

        return new Guid(converted);
    }

    /// <summary>
    /// Converts a GUID to a 16-byte array in RFC 4122 UUID byte order (big-endian first three components).
    /// </summary>
    /// <remarks>
    /// Use this method when exporting GUIDs to systems that expect RFC 4122 byte order:
    /// <list type="bullet">
    ///   <item>OpenLDAP binary UUID attributes</item>
    ///   <item>PostgreSQL binary uuid writes</item>
    ///   <item>Oracle RAW(16) UUID storage</item>
    /// </list>
    /// </remarks>
    /// <param name="guid">The GUID to convert.</param>
    /// <returns>A 16-byte array in RFC 4122 UUID byte order.</returns>
    public static byte[] ToRfc4122Bytes(Guid guid)
    {
        var msBytes = guid.ToByteArray();
        var rfc4122 = new byte[16];

        // time_low (bytes 0-3): reverse for endianness swap
        rfc4122[0] = msBytes[3];
        rfc4122[1] = msBytes[2];
        rfc4122[2] = msBytes[1];
        rfc4122[3] = msBytes[0];

        // time_mid (bytes 4-5): reverse for endianness swap
        rfc4122[4] = msBytes[5];
        rfc4122[5] = msBytes[4];

        // time_hi_version (bytes 6-7): reverse for endianness swap
        rfc4122[6] = msBytes[7];
        rfc4122[7] = msBytes[6];

        // clock_seq_hi_res, clock_seq_low, and node (bytes 8-15): same in both formats
        Array.Copy(msBytes, 8, rfc4122, 8, 8);

        return rfc4122;
    }

    /// <summary>
    /// Converts a GUID to a 16-byte array in Microsoft GUID byte order.
    /// </summary>
    /// <remarks>
    /// This is equivalent to <see cref="Guid.ToByteArray()"/> but provides a symmetric API
    /// alongside <see cref="ToRfc4122Bytes"/>. Use this when exporting to:
    /// <list type="bullet">
    ///   <item>Active Directory <c>objectGUID</c></item>
    ///   <item>SQL Server <c>uniqueidentifier</c></item>
    /// </list>
    /// </remarks>
    /// <param name="guid">The GUID to convert.</param>
    /// <returns>A 16-byte array in Microsoft GUID byte order.</returns>
    public static byte[] ToMicrosoftBytes(Guid guid)
    {
        return guid.ToByteArray();
    }

    /// <summary>
    /// Formats a GUID as an escaped byte string suitable for Active Directory LDAP search filters.
    /// </summary>
    /// <remarks>
    /// AD LDAP filters require binary values to be escaped as \xx for each byte.
    /// For example, searching for an objectGUID requires: (objectGUID=\xx\xx\xx\xx\xx\xx\xx\xx\xx\xx\xx\xx\xx\xx\xx\xx)
    /// </remarks>
    /// <param name="guid">The GUID to format.</param>
    /// <returns>An escaped byte string suitable for LDAP filters (e.g., "\a1\b2\c3...").</returns>
    public static string ToAdLdapFilterString(Guid guid)
    {
        var bytes = guid.ToByteArray(); // Microsoft byte order for AD
        return string.Concat(bytes.Select(b => $"\\{b:x2}"));
    }

    /// <summary>
    /// Normalises a GUID string to the canonical lowercase hyphenated format.
    /// </summary>
    /// <remarks>
    /// This method accepts any valid GUID string format (braced, no hyphens, URN, mixed case)
    /// and returns the standard lowercase hyphenated format: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.
    /// Useful for ensuring consistent GUID representation in logs, comparisons, and storage.
    /// </remarks>
    /// <param name="value">The GUID string to normalise.</param>
    /// <returns>The GUID in canonical lowercase hyphenated format.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is not a valid GUID string.</exception>
    public static string Normalise(string value)
    {
        var guid = FromString(value);
        return guid.ToString("D").ToLowerInvariant();
    }

    /// <summary>
    /// Attempts to normalise a GUID string to the canonical lowercase hyphenated format.
    /// </summary>
    /// <param name="value">The GUID string to normalise.</param>
    /// <param name="result">When this method returns, contains the normalised GUID string if successful; otherwise, null.</param>
    /// <returns><c>true</c> if the normalisation was successful; otherwise, <c>false</c>.</returns>
    public static bool TryNormalise(string? value, out string? result)
    {
        result = null;

        if (!TryFromString(value, out var guid))
            return false;

        result = guid.ToString("D").ToLowerInvariant();
        return true;
    }
}
