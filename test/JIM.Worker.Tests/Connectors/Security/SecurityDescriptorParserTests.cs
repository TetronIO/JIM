// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP.Security;
using NUnit.Framework;
using static JIM.Worker.Tests.Connectors.Security.SecurityDescriptorTestData;

namespace JIM.Worker.Tests.Connectors.Security;

/// <summary>
/// Covers parsing of binary security descriptors read from a directory.
/// <para>
/// This parser exists because .NET's own security descriptor and SID types throw
/// PlatformNotSupportedException on Linux, where JIM runs, even for pure binary parsing with no Windows call
/// behind it. Every byte offset here is therefore JIM's own responsibility, and the bytes come from a system JIM
/// does not control, so malformed input must produce null rather than an exception or a plausible wrong answer.
/// </para>
/// </summary>
[TestFixture]
public class SecurityDescriptorParserTests
{
    private const string ServiceAccount = "S-1-5-21-1111111111-2222222222-3333333333-1104";
    private const string HelpDeskGroup = "S-1-5-21-1111111111-2222222222-3333333333-1105";

    #region security identifiers

    [Test]
    public void SecurityIdentifier_WithATypicalDomainSid_ParsesToItsCanonicalString()
    {
        var result = SecurityIdentifier.TryParse(Sid(ServiceAccount), 0);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.EqualTo(ServiceAccount));
    }

    /// <summary>
    /// The identifier authority is the one big-endian field in the structure. Reading it little-endian yields a
    /// wrong but well-formed SID, which would silently never match anything.
    /// </summary>
    [Test]
    public void SecurityIdentifier_WithAMultiByteIdentifierAuthority_ReadsItBigEndian()
    {
        // S-1-5-... has authority 5, which is byte-order-agnostic in the low byte. Authority 16 (0x10) in the
        // mandatory-label range exercises the same path; use a value that would differ if read the wrong way.
        var result = SecurityIdentifier.TryParse(Sid("S-1-16-12288"), 0);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.EqualTo("S-1-16-12288"));
    }

    [Test]
    public void SecurityIdentifier_WithAWellKnownSid_ParsesToItsCanonicalString()
    {
        var result = SecurityIdentifier.TryParse(Sid("S-1-1-0"), 0);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value, Is.EqualTo("S-1-1-0"));
    }

    [Test]
    public void SecurityIdentifier_ReportsHowManyBytesItConsumed()
    {
        // 8 byte header plus four sub-authorities in S-1-5-21-a-b-c-d.
        var result = SecurityIdentifier.TryParse(Sid(ServiceAccount), 0);

        Assert.That(result!.BinaryLength, Is.EqualTo(8 + (5 * 4)));
    }

    [Test]
    public void SecurityIdentifier_WithATruncatedBuffer_ReturnsNullRatherThanThrowing()
    {
        var truncated = Sid(ServiceAccount)[..10];

        Assert.That(SecurityIdentifier.TryParse(truncated, 0), Is.Null);
    }

    [Test]
    public void SecurityIdentifier_WithAnUnknownRevision_ReturnsNull()
    {
        var bytes = Sid(ServiceAccount);
        bytes[0] = 2;

        Assert.That(SecurityIdentifier.TryParse(bytes, 0), Is.Null);
    }

    [Test]
    public void SecurityIdentifier_WithAnImpossibleSubAuthorityCount_ReturnsNull()
    {
        var bytes = Sid(ServiceAccount);
        bytes[1] = 200;

        Assert.That(SecurityIdentifier.TryParse(bytes, 0), Is.Null);
    }

    #endregion

    #region security descriptors

    [Test]
    public void Parse_WithASingleObjectAce_ReadsItsMaskObjectTypeAndSid()
    {
        var bytes = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd, Is.Not.Null);
        Assert.That(sd!.DaclPresent, Is.True);
        Assert.That(sd.Aces, Has.Count.EqualTo(1));

        var ace = sd.Aces[0];
        Assert.That(ace.IsAllow, Is.True);
        Assert.That(ace.AccessMask, Is.EqualTo(ControlAccess));
        Assert.That(ace.ObjectType, Is.EqualTo(ResetPassword));
        Assert.That(ace.Sid!.Value, Is.EqualTo(ServiceAccount));
    }

    /// <summary>
    /// The two GUID fields in an object ACE are each present only when their flag is set, so the SID's offset
    /// shifts by 16 bytes per absent GUID. An implementation that assumes both are present reads the SID from the
    /// wrong place and produces a wrong-but-plausible answer.
    /// </summary>
    [Test]
    public void Parse_WithAnObjectAceCarryingOnlyAnInheritedObjectType_StillFindsTheSid()
    {
        var bytes = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, inheritedObjectType: ResetPassword));

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd!.Aces[0].ObjectType, Is.Null, "No ObjectType flag was set, so there is no ObjectType.");
        Assert.That(sd.Aces[0].Sid!.Value, Is.EqualTo(ServiceAccount));
    }

    [Test]
    public void Parse_WithAnObjectAceCarryingBothGuids_ReadsTheObjectTypeFromTheFirst()
    {
        var bytes = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount,
                objectType: ResetPassword, inheritedObjectType: ChangePassword));

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd!.Aces[0].ObjectType, Is.EqualTo(ResetPassword));
        Assert.That(sd.Aces[0].Sid!.Value, Is.EqualTo(ServiceAccount));
    }

    [Test]
    public void Parse_WithAnObjectAceCarryingNoGuids_ReportsNoObjectType()
    {
        var bytes = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, GenericAll, ServiceAccount));

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd!.Aces[0].ObjectType, Is.Null);
        Assert.That(sd.Aces[0].Sid!.Value, Is.EqualTo(ServiceAccount));
    }

    [Test]
    public void Parse_WithANonObjectAce_ReadsItsMaskAndSidAndReportsNoObjectType()
    {
        var bytes = SecurityDescriptor(Ace(AccessAllowedAceType, GenericAll, ServiceAccount));

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd!.Aces[0].AccessMask, Is.EqualTo(GenericAll));
        Assert.That(sd.Aces[0].ObjectType, Is.Null);
        Assert.That(sd.Aces[0].Sid!.Value, Is.EqualTo(ServiceAccount));
    }

    [Test]
    public void Parse_WithADenyAce_MarksItAsNotAnAllow()
    {
        var bytes = SecurityDescriptor(
            ObjectAce(AccessDeniedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword),
            Ace(AccessDeniedAceType, GenericAll, HelpDeskGroup));

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd!.Aces.Select(a => a.IsAllow), Is.All.False);
    }

    [Test]
    public void Parse_WithSeveralAces_KeepsThemInDaclOrder()
    {
        var bytes = SecurityDescriptor(
            Ace(AccessDeniedAceType, ControlAccess, HelpDeskGroup),
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword),
            Ace(AccessAllowedAceType, GenericAll, "S-1-5-32-544"));

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd!.Aces, Has.Count.EqualTo(3));
        Assert.That(sd.Aces.Select(a => a.Sid!.Value),
            Is.EqualTo(new[] { HelpDeskGroup, ServiceAccount, "S-1-5-32-544" }));
    }

    [Test]
    public void Parse_PreservesTheInheritOnlyFlag()
    {
        var bytes = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount,
                objectType: ResetPassword, aceFlags: InheritOnlyAce));

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd!.Aces[0].IsInheritOnly, Is.True);
    }

    /// <summary>
    /// A descriptor with no DACL grants everyone everything, which is the opposite of a descriptor with an empty
    /// DACL. Reporting them the same way would turn "no restrictions" into "no access" or the reverse.
    /// </summary>
    [Test]
    public void Parse_WithNoDaclPresent_SaysSoRatherThanReportingAnEmptyOne()
    {
        var bytes = SecurityDescriptorWithControl(SelfRelative);

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd, Is.Not.Null);
        Assert.That(sd!.DaclPresent, Is.False);
        Assert.That(sd.Aces, Is.Empty);
    }

    [Test]
    public void Parse_WithAnEmptyDacl_ReportsItPresentAndEmpty()
    {
        var bytes = SecurityDescriptor();

        var sd = SecurityDescriptorParser.TryParse(bytes);

        Assert.That(sd!.DaclPresent, Is.True);
        Assert.That(sd.Aces, Is.Empty);
    }

    #endregion

    #region malformed input

    /// <summary>
    /// These bytes arrive from a directory JIM does not control, over a protocol where a truncated or hostile
    /// value is entirely possible. Every one of these must produce null, not an exception that surfaces as a
    /// failed preflight for the wrong reason, and not a partial parse that answers the rights question wrongly.
    /// </summary>
    [Test]
    public void Parse_WithMalformedInput_ReturnsNullRatherThanThrowing()
    {
        var valid = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        var cases = new Dictionary<string, byte[]>
        {
            ["empty"] = [],
            ["shorter than the header"] = valid[..10],
            ["truncated mid-DACL"] = valid[..^6],
            ["wrong revision"] = WithByte(valid, 0, 9)
        };

        foreach (var (description, bytes) in cases)
        {
            object? result = null;
            Assert.DoesNotThrow(() => result = SecurityDescriptorParser.TryParse(bytes), $"Threw on input {description}.");
            Assert.That(result, Is.Null, $"Should not have parsed input {description}.");
        }
    }

    /// <summary>
    /// A DACL whose declared ACE count exceeds what its bytes hold. Trusting the count and walking off the end is
    /// the classic parser bug; the count must be treated as a claim to be checked, not a fact.
    /// </summary>
    [Test]
    public void Parse_WithADaclClaimingMoreAcesThanItHolds_ReturnsNullRatherThanReadingPastTheEnd()
    {
        var bytes = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        // The DACL sits after the 20 byte header and two 16 byte SIDs; its ACE count is at offset 4 within it.
        var daclOffset = 20 + 16 + 16;
        bytes[daclOffset + 4] = 50;

        object? result = null;
        Assert.DoesNotThrow(() => result = SecurityDescriptorParser.TryParse(bytes));
        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// An ACE declaring a size of zero would make a naive walk loop forever rather than fail.
    /// </summary>
    [Test]
    public void Parse_WithAnAceDeclaringZeroSize_ReturnsNullRatherThanLoopingForever()
    {
        var bytes = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        var firstAceOffset = 20 + 16 + 16 + 8;
        bytes[firstAceOffset + 2] = 0;
        bytes[firstAceOffset + 3] = 0;

        object? result = null;
        Assert.DoesNotThrow(() => result = SecurityDescriptorParser.TryParse(bytes));
        Assert.That(result, Is.Null);
    }

    private static byte[] WithByte(byte[] source, int index, byte value)
    {
        var copy = (byte[])source.Clone();
        copy[index] = value;
        return copy;
    }

    #endregion
}
