// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP.Security;
using NUnit.Framework;
using static JIM.Worker.Tests.Connectors.Security.SecurityDescriptorTestData;

namespace JIM.Worker.Tests.Connectors.Security;

/// <summary>
/// Covers the access check for a control access right, per [MS-ADTS] 5.1.3.3.4.
/// <para>
/// The stakes here are asymmetric and worth stating. A wrong "granted" makes an administrator confident in a
/// channel that will fail at the first password set, which they will find out about quickly. A wrong "denied"
/// tells a correctly configured deployment that its least-privileged service account lacks a permission it
/// plainly holds, which is the failure mode that made the previous implementation of this check worse than
/// useless. Several tests below exist only to hold that second line.
/// </para>
/// </summary>
[TestFixture]
public class ControlAccessRightEvaluatorTests
{
    private const string ServiceAccount = "S-1-5-21-1111111111-2222222222-3333333333-1104";
    private const string HelpDeskGroup = "S-1-5-21-1111111111-2222222222-3333333333-1105";
    private const string SomebodyElse = "S-1-5-21-1111111111-2222222222-3333333333-9999";
    private const string DomainAdmins = "S-1-5-21-1111111111-2222222222-3333333333-512";

    private static readonly HashSet<string> CallerIsTheServiceAccount =
        new([ServiceAccount, "S-1-5-11"], StringComparer.Ordinal);

    private static AccessCheckOutcome Evaluate(byte[] descriptor, IReadOnlySet<string>? callerSids = null)
    {
        var sd = SecurityDescriptorParser.TryParse(descriptor);
        Assert.That(sd, Is.Not.Null, "The test's own descriptor should parse.");
        return ControlAccessRightEvaluator.Evaluate(sd!, callerSids ?? CallerIsTheServiceAccount, ResetPassword);
    }

    #region the delegation that matters

    /// <summary>
    /// The normal, least-privileged delegation: an object ACE granting exactly the Reset Password extended right.
    /// This is the case the old allowedAttributesEffective check got wrong, so if only one test here survives,
    /// it should be this one.
    /// </summary>
    [Test]
    public void Evaluate_WithTheResetPasswordRightDelegatedDirectly_Grants()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// The same delegation made to a group the account belongs to, which is how it is actually done in practice.
    /// </summary>
    [Test]
    public void Evaluate_WithTheRightDelegatedToAGroupTheCallerIsIn_Grants()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, HelpDeskGroup, objectType: ResetPassword));

        var caller = new HashSet<string>([ServiceAccount, HelpDeskGroup], StringComparer.Ordinal);

        Assert.That(Evaluate(sd, caller), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// An ACE with the control-access right but no ObjectType grants every control access right, including this
    /// one. Per [MS-ADTS] 5.1.3.3.4 step 3.3, and the same rule stated the other way round in Microsoft's access
    /// control documentation: an ACE with no object GUID applies to all of the object's properties and rights.
    /// </summary>
    [Test]
    public void Evaluate_WithAllExtendedRightsGranted_Grants()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// A plain, non-object ACE carrying the control-access right. This is how a great many real grants are
    /// stored, including the built-in administrative ones, and it has no ObjectType field at all rather than an
    /// empty one. Treating only object ACEs as capable of granting would report Domain Admins as unable to reset
    /// a password.
    /// </summary>
    [Test]
    public void Evaluate_WithANonObjectAceCarryingTheControlAccessRight_Grants()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, ControlAccess, ServiceAccount));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// Full control. Stored in real access control lists both as mapped specific rights and, in places, as the
    /// generic bit; a check that only understood the former would deny the most privileged accounts there are.
    /// </summary>
    [Test]
    public void Evaluate_WithFullControl_Grants()
    {
        var sd = SecurityDescriptor(Ace(AccessAllowedAceType, GenericAll, DomainAdmins));

        var caller = new HashSet<string>([ServiceAccount, DomainAdmins], StringComparer.Ordinal);

        Assert.That(Evaluate(sd, caller), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    #endregion

    #region denials

    [Test]
    public void Evaluate_WithNoAceMentioningTheCaller_Denies()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, SomebodyElse, objectType: ResetPassword));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// The whole point of the exercise: write permission on the password attribute is a different right from the
    /// extended right that actually permits a reset, and must not be read as one.
    /// </summary>
    [Test]
    public void Evaluate_WithOnlyWritePermissionOnTheAttribute_Denies()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, WriteProperty, ServiceAccount, objectType: ResetPassword));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// A control-access grant scoped to a different extended right says nothing about this one, so the ObjectType
    /// has to actually be compared rather than merely noted as present.
    /// </summary>
    [Test]
    public void Evaluate_WithADifferentExtendedRightGranted_Denies()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ChangePassword));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    [Test]
    public void Evaluate_WithAnExplicitDeny_Denies()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessDeniedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// Entries are evaluated in the order the list holds them and the first match decides, so a deny placed
    /// before a grant wins. Canonical ordering puts denies first precisely so this is what happens.
    /// </summary>
    [Test]
    public void Evaluate_WithADenyBeforeAGrant_TheDenyWins()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessDeniedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword),
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// The mirror image, and the reason the check must not simply scan for any deny anywhere: a grant that comes
    /// first wins, even though a later entry denies. Evaluating denies preferentially would be wrong here.
    /// </summary>
    [Test]
    public void Evaluate_WithAGrantBeforeADeny_TheGrantWins()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword),
            ObjectAce(AccessDeniedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// A deny aimed at somebody else must not stop the caller's own grant from being reached.
    /// </summary>
    [Test]
    public void Evaluate_WithADenyForAnotherPrincipalFirst_StillGrants()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessDeniedObjectAceType, ControlAccess, SomebodyElse, objectType: ResetPassword),
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    #endregion

    #region structural rules

    /// <summary>
    /// An inherit-only entry governs children, not the object carrying it, so it must be skipped. Honouring one
    /// would report a grant on an object that does not actually have it.
    /// </summary>
    [Test]
    public void Evaluate_WithAnInheritOnlyGrant_SkipsItAndDenies()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount,
                objectType: ResetPassword, aceFlags: InheritOnlyAce));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    /// <summary>
    /// An inherit-only deny is skipped for the same reason, and must not suppress a real grant behind it.
    /// </summary>
    [Test]
    public void Evaluate_WithAnInheritOnlyDenyBeforeAGrant_SkipsTheDenyAndGrants()
    {
        var sd = SecurityDescriptor(
            ObjectAce(AccessDeniedObjectAceType, ControlAccess, ServiceAccount,
                objectType: ResetPassword, aceFlags: InheritOnlyAce),
            ObjectAce(AccessAllowedObjectAceType, ControlAccess, ServiceAccount, objectType: ResetPassword));

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// No access control list at all means unrestricted, per [MS-ADTS] 5.1.3.3.4 step 1. Rare in a real directory,
    /// but the opposite of an empty list, and getting the two the same way round matters.
    /// </summary>
    [Test]
    public void Evaluate_WithNoDaclPresent_Grants()
    {
        var sd = SecurityDescriptorWithControl(SelfRelative);

        Assert.That(Evaluate(sd), Is.EqualTo(AccessCheckOutcome.Granted));
    }

    /// <summary>
    /// An empty list denies everyone everything, per step 2.
    /// </summary>
    [Test]
    public void Evaluate_WithAnEmptyDacl_Denies()
    {
        Assert.That(Evaluate(SecurityDescriptor()), Is.EqualTo(AccessCheckOutcome.Denied));
    }

    #endregion
}
