// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;
namespace JIM.Connectors.LDAP.Security;

/// <summary>
/// Parses a self-relative binary security descriptor, as read from a directory's nTSecurityDescriptor attribute.
/// <para>
/// Hand-rolled because .NET's <c>RawSecurityDescriptor</c> throws <see cref="PlatformNotSupportedException"/> on
/// Linux, where JIM runs. The exception is not limited to the Windows-interop paths: constructing one from a byte
/// array fails too, as does <c>SecurityIdentifier</c>. Parsing these structures is pure byte arithmetic with
/// nothing platform-specific about it, so it is done here.
/// </para>
/// <para>
/// Structures per [MS-DTYP]: 2.4.6 SECURITY_DESCRIPTOR, 2.4.5 ACL, 2.4.4 ACE, 2.4.2 SID. Everything is
/// little-endian apart from a SID's identifier authority.
/// </para>
/// <para>
/// <b>Every length in the input is a claim, not a fact.</b> These bytes come from a system JIM does not control,
/// so an offset, size or count that does not fit the buffer means the descriptor is rejected outright. Returning
/// a partial parse would be worse than returning nothing: the caller is deciding whether an account holds a
/// permission, and half an access control list can invert that answer.
/// </para>
/// </summary>
internal static class SecurityDescriptorParser
{
    private const int SecurityDescriptorHeaderLength = 20;
    private const int AclHeaderLength = 8;
    private const int AceHeaderLength = 4;

    private const ushort SeDaclPresent = 0x0004;

    private const byte AccessAllowedAceType = 0x00;
    private const byte AccessDeniedAceType = 0x01;
    private const byte AccessAllowedObjectAceType = 0x05;
    private const byte AccessDeniedObjectAceType = 0x06;

    private const byte InheritOnlyAce = 0x08;

    private const uint AceObjectTypePresent = 0x00000001;
    private const uint AceInheritedObjectTypePresent = 0x00000002;

    private const int GuidLength = 16;

    /// <summary>
    /// Parses a security descriptor from its binary form.
    /// </summary>
    /// <returns>The descriptor, or null when the bytes are not a well-formed self-relative security descriptor.</returns>
    internal static SecurityDescriptor? TryParse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < SecurityDescriptorHeaderLength)
            return null;

        if (buffer[0] != 1)
            return null;

        var control = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(2, 2));
        var daclOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(16, 4));
        var daclPresent = (control & SeDaclPresent) == SeDaclPresent;

        // No list at all is a valid descriptor meaning "unrestricted", so it is reported rather than rejected.
        if (!daclPresent || daclOffset == 0)
            return new SecurityDescriptor { DaclPresent = false, Aces = [] };

        if (daclOffset > int.MaxValue || daclOffset > (uint)buffer.Length)
            return null;

        var aces = TryParseAcl(buffer[(int)daclOffset..]);
        return aces == null
            ? null
            : new SecurityDescriptor { DaclPresent = true, Aces = aces };
    }

    /// <summary>
    /// Parses an access control list and everything in it. Returns null when the list does not fit its own
    /// declared bounds.
    /// </summary>
    private static List<AccessControlEntry>? TryParseAcl(ReadOnlySpan<byte> acl)
    {
        if (acl.Length < AclHeaderLength)
            return null;

        var aclSize = BinaryPrimitives.ReadUInt16LittleEndian(acl.Slice(2, 2));
        var aceCount = BinaryPrimitives.ReadUInt16LittleEndian(acl.Slice(4, 2));

        if (aclSize < AclHeaderLength || aclSize > acl.Length)
            return null;

        var aces = new List<AccessControlEntry>(aceCount);
        var position = AclHeaderLength;

        for (var i = 0; i < aceCount; i++)
        {
            if (position + AceHeaderLength > aclSize)
                return null;

            var ace = acl[position..aclSize];
            var aceType = ace[0];
            var aceFlags = ace[1];
            var aceSize = BinaryPrimitives.ReadUInt16LittleEndian(ace.Slice(2, 2));

            // A zero or undersized entry would leave the walk stuck on the same offset forever.
            if (aceSize < AceHeaderLength || position + aceSize > aclSize)
                return null;

            var parsed = TryParseAce(ace[..aceSize], aceType, aceFlags);

            // Entry types an access check does not read (audit, callback, label) are skipped rather than rejected:
            // they are legitimately present in real directories and say nothing about access.
            if (parsed != null)
                aces.Add(parsed);

            position += aceSize;
        }

        return aces;
    }

    /// <summary>
    /// Parses one entry. Returns null for entry types that carry no access decision, and for entries whose
    /// declared layout does not fit the bytes they claim to occupy.
    /// </summary>
    private static AccessControlEntry? TryParseAce(ReadOnlySpan<byte> ace, byte aceType, byte aceFlags)
    {
        var isAllow = aceType is AccessAllowedAceType or AccessAllowedObjectAceType;
        var isDeny = aceType is AccessDeniedAceType or AccessDeniedObjectAceType;
        if (!isAllow && !isDeny)
            return null;

        var isObjectAce = aceType is AccessAllowedObjectAceType or AccessDeniedObjectAceType;

        // Both shapes open with the 4 byte header and a 4 byte access mask.
        if (ace.Length < AceHeaderLength + 4)
            return null;

        var accessMask = BinaryPrimitives.ReadUInt32LittleEndian(ace.Slice(AceHeaderLength, 4));
        var position = AceHeaderLength + 4;
        Guid? objectType = null;

        if (isObjectAce)
        {
            if (ace.Length < position + 4)
                return null;

            var objectFlags = BinaryPrimitives.ReadUInt32LittleEndian(ace.Slice(position, 4));
            position += 4;

            // The two GUIDs are each present only when their flag is set, so the SID's offset depends on the
            // flags rather than being fixed. Assuming both are present is the easiest way to misread a real ACL.
            if ((objectFlags & AceObjectTypePresent) == AceObjectTypePresent)
            {
                if (ace.Length < position + GuidLength)
                    return null;

                objectType = new Guid(ace.Slice(position, GuidLength));
                position += GuidLength;
            }

            if ((objectFlags & AceInheritedObjectTypePresent) == AceInheritedObjectTypePresent)
            {
                // Read only to step past it: which child types inherit an entry says nothing about access to the
                // object actually being checked.
                if (ace.Length < position + GuidLength)
                    return null;

                position += GuidLength;
            }
        }

        var sid = SecurityIdentifier.TryParse(ace, position);
        if (sid == null)
            return null;

        return new AccessControlEntry
        {
            IsAllow = isAllow,
            IsInheritOnly = (aceFlags & InheritOnlyAce) == InheritOnlyAce,
            AccessMask = accessMask,
            ObjectType = objectType,
            Sid = sid
        };
    }
}
