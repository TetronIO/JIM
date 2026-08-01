// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
namespace JIM.Connectors.LDAP.Security;

/// <summary>
/// A Windows security identifier, parsed from its binary form.
/// <para>
/// Hand-rolled because .NET's <c>System.Security.Principal.SecurityIdentifier</c> throws
/// <see cref="PlatformNotSupportedException"/> on Linux, even for the pure binary parsing path with no Windows
/// call behind it, and JIM runs in Linux containers. Only parsing and formatting are needed here: nothing
/// translates a SID to an account name, which is the part that genuinely requires Windows.
/// </para>
/// <para>
/// Binary layout per [MS-DTYP] 2.4.2.2: Revision (1 byte), SubAuthorityCount (1 byte), IdentifierAuthority
/// (6 bytes, <b>big-endian</b>, unlike everything around it), then SubAuthorityCount sub-authorities of 4 bytes
/// each in little-endian.
/// </para>
/// </summary>
internal sealed class SecurityIdentifier : IEquatable<SecurityIdentifier>
{
    /// <summary>The canonical S-1-... string form, which is what everything else compares on.</summary>
    internal string Value { get; }

    /// <summary>How many bytes of the source buffer this SID occupied, so a caller can walk past it.</summary>
    internal int BinaryLength { get; }

    private SecurityIdentifier(string value, int binaryLength)
    {
        Value = value;
        BinaryLength = binaryLength;
    }

    /// <summary>
    /// The largest a SID can be: the 8 byte header plus 15 sub-authorities.
    /// </summary>
    internal const int MaximumBinaryLength = 8 + (15 * 4);

    private const int HeaderLength = 8;
    private const int MaximumSubAuthorities = 15;

    /// <summary>
    /// Reads a SID from <paramref name="offset"/> in <paramref name="buffer"/>.
    /// </summary>
    /// <returns>The SID, or null when the buffer does not hold a well-formed one at that offset.</returns>
    internal static SecurityIdentifier? TryParse(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset < 0 || offset > buffer.Length - HeaderLength)
            return null;

        var sid = buffer[offset..];
        var revision = sid[0];
        var subAuthorityCount = sid[1];

        // Revision is fixed at 1, and more than 15 sub-authorities cannot be expressed. Either means this is not a
        // SID, which matters because these bytes come from a directory JIM does not control.
        if (revision != 1 || subAuthorityCount > MaximumSubAuthorities)
            return null;

        var binaryLength = HeaderLength + (subAuthorityCount * 4);
        if (binaryLength > sid.Length)
            return null;

        // The identifier authority is the one big-endian field in the structure: six bytes, most significant first.
        ulong identifierAuthority = 0;
        for (var i = 0; i < 6; i++)
            identifierAuthority = (identifierAuthority << 8) | sid[2 + i];

        var text = new StringBuilder("S-1-", 64);

        // Authorities that do not fit in 32 bits are written in hexadecimal, per the string form in [MS-DTYP] 2.4.2.1.
        if (identifierAuthority < 0x100000000)
            text.Append(identifierAuthority.ToString(CultureInfo.InvariantCulture));
        else
            text.Append("0x").Append(identifierAuthority.ToString("x12", CultureInfo.InvariantCulture));

        for (var i = 0; i < subAuthorityCount; i++)
        {
            var subAuthority = BinaryPrimitives.ReadUInt32LittleEndian(sid.Slice(HeaderLength + (i * 4), 4));
            text.Append('-').Append(subAuthority.ToString(CultureInfo.InvariantCulture));
        }

        return new SecurityIdentifier(text.ToString(), binaryLength);
    }

    public bool Equals(SecurityIdentifier? other) =>
        other != null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SecurityIdentifier);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
