// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP.Security;
using NUnit.Framework;
using static JIM.Worker.Tests.Connectors.Security.SecurityDescriptorTestData;

namespace JIM.Worker.Tests.Connectors.Security;

/// <summary>
/// Covers the access check for one ordinary access-mask right, per [MS-ADTS] 5.1.3.3.2.
/// <para>
/// The first caller is the Deleted Objects container check: an account without List Contents there is not
/// refused, its Delta Import simply finds no deletions. The asymmetry that shaped
/// <see cref="ControlAccessRightEvaluatorTests"/> holds here too: a wrong "granted" is found out at the first
/// missed deletion, a wrong "denied" tells a correctly delegated deployment to grant a right it already holds.
/// </para>
/// </summary>
[TestFixture]
public class AccessMaskEvaluatorTests
{
    private const string ServiceAccount = "S-1-5-21-1111111111-2222222222-3333333333-1104";
    private const string OperatorsGroup = "S-1-5-21-1111111111-2222222222-3333333333-1105";
    private const string SomebodyElse = "S-1-5-21-1111111111-2222222222-3333333333-9999";
    private const string DomainAdmins = "S-1-5-21-1111111111-2222222222-3333333333-512";

    private static readonly HashSet<string> CallerIsTheServiceAccount =
        new([ServiceAccount, "S-1-5-11"], StringComparer.Ordinal);

    private static AccessCheckOutcome Evaluate(byte[] descriptor, uint right = ListContents, IReadOnlySet<string>? callerSids = null, Guid? objectClass = null)
    {
        var sd = SecurityDescriptorParser.TryParse(descriptor);
        Assert.That(sd, Is.Not.Null, "The test's own descriptor should parse.");
        return AccessMaskEvaluator.Evaluate(sd!, callerSids ?? CallerIsTheServiceAccount, right, objectClass);
    }

    #region grants

    [Test]
    public void Evaluate_WithTheRightGrantedDirectly_Grants()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, ListContents, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// Delegation is made to a group in practice, so the caller's group memberships must be honoured.
    /// </summary>
    [Test]
    public void Evaluate_WithTheRightGrantedToAGroupTheCallerIsIn_Grants()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, ListContents, OperatorsGroup));

        var caller = new HashSet<string>([ServiceAccount, OperatorsGroup], StringComparer.Ordinal);

        Assert.That(Evaluate(sd, callerSids: caller), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// A grant carrying several bits, of which the one asked about is one, is a grant of that bit.
    /// </summary>
    [Test]
    public void Evaluate_WithTheRightAmongOthersInOneGrant_Grants()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, ListContents | ReadProperty | ReadControl, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// Full control carried as the generic bit. Active Directory maps generic bits to specific rights when it stores
    /// a descriptor, but a directory that stores what it was sent, or an entry written by tooling that bypasses the
    /// mapping, can still present one, and it has to be read as the administrator meant it.
    /// </summary>
    [Test]
    public void Evaluate_WithGenericAll_Grants()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, GenericAll, DomainAdmins));

        var caller = new HashSet<string>([ServiceAccount, DomainAdmins], StringComparer.Ordinal);

        Assert.That(Evaluate(sd, callerSids: caller), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// GENERIC_READ maps to the read family (List Contents, Read Property, List Object and Read Control), so it
    /// grants each of them.
    /// </summary>
    [TestCase(ListContents)]
    [TestCase(ReadProperty)]
    [TestCase(ListObject)]
    [TestCase(ReadControl)]
    public void Evaluate_WithGenericRead_GrantsTheReadFamily(uint right)
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, GenericRead, ServiceAccount));

        Assert.That(Evaluate(sd, right), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// The other half of the mapping: GENERIC_READ does not reach a write right.
    /// </summary>
    [Test]
    public void Evaluate_WithGenericRead_DoesNotGrantWriteProperty()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, GenericRead, ServiceAccount));

        Assert.That(Evaluate(sd, WriteProperty), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// GENERIC_EXECUTE maps to Read Control and List Contents ([MS-ADTS] 5.1.3.2), so an entry carrying it grants
    /// the very right the Deleted Objects container check asks about.
    /// </summary>
    [TestCase(ListContents)]
    [TestCase(ReadControl)]
    public void Evaluate_WithGenericExecute_GrantsListContentsAndReadControl(uint right)
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, GenericExecute, ServiceAccount));

        Assert.That(Evaluate(sd, right), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    [Test]
    public void Evaluate_WithGenericExecute_DoesNotGrantReadProperty()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, GenericExecute, ServiceAccount));

        Assert.That(Evaluate(sd, ReadProperty), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// GENERIC_WRITE maps to Read Control, Write Property and Validated Write ([MS-ADTS] 5.1.3.2).
    /// </summary>
    [TestCase(ReadControl)]
    [TestCase(WriteProperty)]
    [TestCase(ValidatedWrite)]
    public void Evaluate_WithGenericWrite_GrantsReadControlWritePropertyAndValidatedWrite(uint right)
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, GenericWrite, ServiceAccount));

        Assert.That(Evaluate(sd, right), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    [Test]
    public void Evaluate_WithGenericWrite_DoesNotGrantListContents()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, GenericWrite, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    #endregion

    #region denials

    [Test]
    public void Evaluate_WithNoAceMentioningTheCaller_Denies()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, ListContents, SomebodyElse));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// A grant of a different right says nothing about this one.
    /// </summary>
    [Test]
    public void Evaluate_WithOnlyADifferentRightGranted_Denies()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, ReadProperty, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    [Test]
    public void Evaluate_WithAnExplicitDeny_Denies()
    {
        var sd = SecurityDescriptor(Ace(AccessDeniedAceType, ListContents, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// Entries are walked in the order held and the first that applies decides, so a deny ahead of a grant wins.
    /// </summary>
    [Test]
    public void Evaluate_WithADenyBeforeAGrant_TheDenyWins()
    {
        var sd = SecurityDescriptor(
            Ace(AccessDeniedAceType, ListContents, ServiceAccount),
            Ace(AccessAllowedAceType, ListContents, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// The mirror image: a grant ahead of a deny wins, which is why the walk must not prefer denies.
    /// </summary>
    [Test]
    public void Evaluate_WithAGrantBeforeADeny_TheGrantWins()
    {
        var sd = SecurityDescriptor(
            Ace(AccessAllowedAceType, ListContents, ServiceAccount),
            Ace(AccessDeniedAceType, ListContents, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// A deny aimed at somebody else must not stop the caller's own grant being reached.
    /// </summary>
    [Test]
    public void Evaluate_WithADenyForAnotherPrincipalFirst_StillGrants()
    {
        var sd = SecurityDescriptor(
            Ace(AccessDeniedAceType, ListContents, SomebodyElse),
            Ace(AccessAllowedAceType, ListContents, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// A deny of a different right must not be read as a deny of this one, even when it comes first.
    /// </summary>
    [Test]
    public void Evaluate_WithADenyOfADifferentRightBeforeAGrant_StillGrants()
    {
        var sd = SecurityDescriptor(
            Ace(AccessDeniedAceType, WriteProperty, ServiceAccount),
            Ace(AccessAllowedAceType, ListContents, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// A deny carrying a generic bit denies every right the bit stands for, exactly as a grant carrying it would
    /// grant them, so a generic deny ahead of a specific grant wins. GENERIC_EXECUTE is the one whose expansion
    /// holds List Contents alongside GENERIC_READ and GENERIC_ALL.
    /// </summary>
    [TestCase(GenericRead)]
    [TestCase(GenericAll)]
    [TestCase(GenericExecute)]
    public void Evaluate_WithAGenericDenyBeforeASpecificGrant_TheDenyWins(uint genericBit)
    {
        var sd = SecurityDescriptor(
            Ace(AccessDeniedAceType, genericBit, ServiceAccount),
            Ace(AccessAllowedAceType, ListContents, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    #endregion

    #region object entries

    /// <summary>
    /// Read Property granted on one property is not Read Property in general: an object ACE whose ObjectType is a
    /// property, property set or child class speaks only to that node of the object tree, [MS-ADTS] 5.1.3.3.3
    /// step 3.5, not to the object as a whole.
    /// </summary>
    [Test]
    public void Evaluate_WithReadPropertyScopedToOneProperty_DoesNotGrantReadProperty()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ReadProperty, ServiceAccount, objectType: DescriptionProperty));

        Assert.That(Evaluate(sd, ReadProperty), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// The other direction, and a knowing simplification: [MS-ADTS] 5.1.3.3.3 step 3.8 propagates a scoped deny up
    /// to the root, which the evaluator does not model. Skipping the scoped deny can only over-report a grant,
    /// never fabricate a denial, and the unscoped grant behind it is still reached.
    /// </summary>
    [Test]
    public void Evaluate_WithADenyScopedToOnePropertyBeforeAnUnscopedGrant_StillGrants()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessDeniedObjectAceType, ReadProperty, ServiceAccount, objectType: DescriptionProperty),
            Ace(AccessAllowedAceType, ReadProperty, ServiceAccount));

        Assert.That(Evaluate(sd, ReadProperty), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// An object ACE with no ObjectType is scoped to nothing in particular and speaks to the plain right like a
    /// non-object entry does.
    /// </summary>
    [Test]
    public void Evaluate_WithAnObjectAceCarryingNoObjectType_Grants()
    {
        var sd = SecurityDescriptor(ObjectAce(AccessAllowedObjectAceType, ListContents, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// The root of the object tree carries the object's own class GUID ([MS-ADTS] 5.1.3.3.3 step 1), so an object
    /// ACE whose ObjectType is that class addresses the whole object exactly as an unscoped entry does.
    /// </summary>
    [Test]
    public void Evaluate_WithListContentsScopedToTheObjectsOwnClass_Grants()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ListContents, ServiceAccount, objectType: ContainerClass));

        Assert.That(Evaluate(sd, objectClass: ContainerClass), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// A class the object is not can only be a child class, which scopes the entry below the root.
    /// </summary>
    [Test]
    public void Evaluate_WithListContentsScopedToAnotherClass_DoesNotGrant()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ListContents, ServiceAccount, objectType: UserClass));

        Assert.That(Evaluate(sd, objectClass: ContainerClass), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// A caller that does not say what class the object is cannot have a class-scoped entry read as unscoped.
    /// </summary>
    [Test]
    public void Evaluate_WithListContentsScopedToAClassAndNoObjectClassGiven_DoesNotGrant()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ListContents, ServiceAccount, objectType: ContainerClass));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    #endregion

    #region structural rules

    /// <summary>
    /// An inherit-only entry governs children, not the object carrying it, so it is skipped.
    /// </summary>
    [Test]
    public void Evaluate_WithAnInheritOnlyGrant_SkipsItAndDenies()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, ListContents, ServiceAccount, aceFlags: InheritOnlyAce));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    [Test]
    public void Evaluate_WithAnInheritOnlyDenyBeforeAGrant_SkipsTheDenyAndGrants()
    {
        var sd = SecurityDescriptor(
            Ace(AccessDeniedAceType, ListContents, ServiceAccount, aceFlags: InheritOnlyAce),
            Ace(AccessAllowedAceType, ListContents, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// No access control list at all means unrestricted, [MS-ADTS] 5.1.3.3.2 step 1.
    /// </summary>
    [Test]
    public void Evaluate_WithNoDaclPresent_Grants()
    {
        var sd = SecurityDescriptorWithControl(SelfRelative);

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// An empty list denies everyone everything, step 2.
    /// </summary>
    [Test]
    public void Evaluate_WithAnEmptyDacl_Denies()
    {
        Assert.That(Evaluate(SecurityDescriptor()), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// The evaluator answers one right at a time. Asking about two bits in one call would need the remaining
    /// rights accumulated across entries, which the first-match walk does not do, so the question is refused.
    /// </summary>
    [TestCase(ListContents | ReadProperty)]
    [TestCase(0u)]
    public void Evaluate_WithARightThatIsNotExactlyOneBit_Throws(uint right)
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, GenericAll, ServiceAccount));

        Assert.That(() => Evaluate(sd, right), Throws.ArgumentException.With.Message.Contains("one right at a time"));
    }

    #endregion
}
