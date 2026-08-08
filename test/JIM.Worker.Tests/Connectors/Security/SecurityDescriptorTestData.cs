// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers.Binary;

namespace JIM.Worker.Tests.Connectors.Security;

/// <summary>
/// Builds binary security descriptors for tests, field by field.
/// <para>
/// Deliberately built from the structure definitions in [MS-DTYP] rather than captured as opaque hex blobs from a
/// live directory. A blob proves the parser agrees with whatever produced it; building each field here means the
/// test states what the bytes are supposed to mean, so a parser that reads the right value from the wrong offset
/// still fails.
/// </para>
/// </summary>
internal static class SecurityDescriptorTestData
{
    internal const int AccessAllowedAceType = 0x00;
    internal const int AccessDeniedAceType = 0x01;
    internal const int AccessAllowedObjectAceType = 0x05;
    internal const int AccessDeniedObjectAceType = 0x06;

    internal const uint ControlAccess = 0x00000100;
    internal const uint GenericAll = 0x10000000;
    internal const uint WriteProperty = 0x00000020;

    internal const byte InheritOnlyAce = 0x08;

    internal const uint ObjectTypePresent = 0x00000001;
    internal const uint InheritedObjectTypePresent = 0x00000002;

    internal const ushort DaclPresent = 0x0004;
    internal const ushort SelfRelative = 0x8000;

    /// <summary>The Reset Password control access right, [MS-ADTS] 5.1.3.2.1.</summary>
    internal static readonly Guid ResetPassword = new("00299570-246d-11d0-a768-00aa006e0529");

    /// <summary>An unrelated control access right, for proving the ObjectType is actually compared.</summary>
    internal static readonly Guid ChangePassword = new("ab721a53-1e2f-11d0-9819-00aa0040529b");

    /// <summary>
    /// Encodes a SID in the binary form of [MS-DTYP] 2.4.2.2 from its S-1-... string.
    /// </summary>
    internal static byte[] Sid(string sddl)
    {
        var parts = sddl.Split('-');
        var identifierAuthority = ulong.Parse(parts[2]);
        var subAuthorities = parts.Skip(3).Select(uint.Parse).ToArray();

        var bytes = new byte[8 + (subAuthorities.Length * 4)];
        bytes[0] = 1;
        bytes[1] = (byte)subAuthorities.Length;

        // Identifier authority is big-endian, unlike every other multi-byte field in the structure.
        for (var i = 0; i < 6; i++)
            bytes[2 + i] = (byte)(identifierAuthority >> ((5 - i) * 8));

        for (var i = 0; i < subAuthorities.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (i * 4), 4), subAuthorities[i]);

        return bytes;
    }

    /// <summary>
    /// Builds a non-object ACE (ACCESS_ALLOWED_ACE / ACCESS_DENIED_ACE, [MS-DTYP] 2.4.4.2 and 2.4.4.4).
    /// </summary>
    internal static byte[] Ace(int aceType, uint mask, string sid, byte aceFlags = 0)
    {
        var sidBytes = Sid(sid);
        var size = 8 + sidBytes.Length;
        var ace = new byte[size];

        ace[0] = (byte)aceType;
        ace[1] = aceFlags;
        BinaryPrimitives.WriteUInt16LittleEndian(ace.AsSpan(2, 2), (ushort)size);
        BinaryPrimitives.WriteUInt32LittleEndian(ace.AsSpan(4, 4), mask);
        sidBytes.CopyTo(ace.AsSpan(8));

        return ace;
    }

    /// <summary>
    /// Builds an object ACE (ACCESS_ALLOWED_OBJECT_ACE / ACCESS_DENIED_OBJECT_ACE, [MS-DTYP] 2.4.4.3 and 2.4.4.5).
    /// <para>
    /// The two GUID fields are each present only when their flag is set, so the SID's offset depends on the flags.
    /// Getting that conditional layout wrong is the single easiest way to misparse a directory ACL, which is why
    /// the builder can produce every combination.
    /// </para>
    /// </summary>
    internal static byte[] ObjectAce(int aceType, uint mask, string sid, Guid? objectType = null,
        Guid? inheritedObjectType = null, byte aceFlags = 0)
    {
        var sidBytes = Sid(sid);
        uint flags = 0;
        if (objectType.HasValue) flags |= ObjectTypePresent;
        if (inheritedObjectType.HasValue) flags |= InheritedObjectTypePresent;

        var guidCount = (objectType.HasValue ? 1 : 0) + (inheritedObjectType.HasValue ? 1 : 0);
        var size = 12 + (guidCount * 16) + sidBytes.Length;
        var ace = new byte[size];

        ace[0] = (byte)aceType;
        ace[1] = aceFlags;
        BinaryPrimitives.WriteUInt16LittleEndian(ace.AsSpan(2, 2), (ushort)size);
        BinaryPrimitives.WriteUInt32LittleEndian(ace.AsSpan(4, 4), mask);
        BinaryPrimitives.WriteUInt32LittleEndian(ace.AsSpan(8, 4), flags);

        var position = 12;
        if (objectType.HasValue)
        {
            objectType.Value.ToByteArray().CopyTo(ace.AsSpan(position));
            position += 16;
        }
        if (inheritedObjectType.HasValue)
        {
            inheritedObjectType.Value.ToByteArray().CopyTo(ace.AsSpan(position));
            position += 16;
        }
        sidBytes.CopyTo(ace.AsSpan(position));

        return ace;
    }

    /// <summary>
    /// Wraps ACEs in an ACL ([MS-DTYP] 2.4.5) and then in a self-relative SECURITY_DESCRIPTOR ([MS-DTYP] 2.4.6),
    /// laid out owner, group, then DACL.
    /// </summary>
    internal static byte[] SecurityDescriptor(params byte[][] aces) =>
        SecurityDescriptorWithControl(DaclPresent | SelfRelative, aces);

    /// <summary>
    /// As <see cref="SecurityDescriptor"/>, but with the control field supplied, so a descriptor with no DACL
    /// present can be built.
    /// </summary>
    internal static byte[] SecurityDescriptorWithControl(ushort control, params byte[][] aces)
    {
        var owner = Sid("S-1-5-32-544");
        var group = Sid("S-1-5-32-544");

        var aceBytes = aces.SelectMany(a => a).ToArray();
        var aclSize = 8 + aceBytes.Length;
        var acl = new byte[aclSize];
        acl[0] = 4; // ACL_REVISION_DS, required for object ACEs
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(2, 2), (ushort)aclSize);
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(4, 2), (ushort)aces.Length);
        aceBytes.CopyTo(acl.AsSpan(8));

        var daclPresent = (control & DaclPresent) == DaclPresent;

        const int headerLength = 20;
        var ownerOffset = headerLength;
        var groupOffset = ownerOffset + owner.Length;
        var daclOffset = groupOffset + group.Length;

        var total = daclOffset + (daclPresent ? acl.Length : 0);
        var sd = new byte[total];

        sd[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(sd.AsSpan(2, 2), control);
        BinaryPrimitives.WriteUInt32LittleEndian(sd.AsSpan(4, 4), (uint)ownerOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(sd.AsSpan(8, 4), (uint)groupOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(sd.AsSpan(12, 4), 0); // no SACL
        BinaryPrimitives.WriteUInt32LittleEndian(sd.AsSpan(16, 4), daclPresent ? (uint)daclOffset : 0);

        owner.CopyTo(sd.AsSpan(ownerOffset));
        group.CopyTo(sd.AsSpan(groupOffset));
        if (daclPresent)
            acl.CopyTo(sd.AsSpan(daclOffset));

        return sd;
    }
}
