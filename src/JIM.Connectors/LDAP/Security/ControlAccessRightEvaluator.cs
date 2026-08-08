// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP.Security;

/// <summary>
/// The outcome of an access check for a control access right.
/// </summary>
internal enum AccessCheckOutcome
{
    /// <summary>The access control list grants the right to the caller.</summary>
    Granted,

    /// <summary>
    /// The access control list does not grant the right, either by denying it outright or by never granting it.
    /// <para>
    /// This is a statement about the list that was evaluated and nothing more. Whether the list could be read at
    /// all, and whether the caller's group memberships were fully known, are questions for the caller: an
    /// incomplete input must never be turned into a denial here.
    /// </para>
    /// </summary>
    Denied
}

/// <summary>
/// Decides whether a security context holds a given control access right on an object, per [MS-ADTS] 5.1.3.3.4.
/// <para>
/// Control access rights (extended rights) are not ordinary permissions. Resetting a password is granted by the
/// User-Force-Change-Password right, not by write permission on the password attribute, and the two are entirely
/// separate: an account can hold either without the other. That distinction is the whole reason this evaluator
/// exists.
/// </para>
/// </summary>
internal static class ControlAccessRightEvaluator
{
    /// <summary>
    /// RIGHT_DS_CONTROL_ACCESS. When present in an entry's mask, the entry concerns control access rights.
    /// </summary>
    private const uint ControlAccess = 0x00000100;

    /// <summary>
    /// GENERIC_ALL. Full control, which subsumes every control access right.
    /// <para>
    /// Not named in the [MS-ADTS] 5.1.3.3.4 rules, which speak only of RIGHT_DS_CONTROL_ACCESS, because generic
    /// rights are normally mapped to specific ones before an access check sees them. Real access control lists
    /// nonetheless carry the generic bit, and the built-in administrative entries are among those that do, so a
    /// check that ignored it would report the most privileged accounts in the domain as unable to reset a
    /// password.
    /// </para>
    /// </summary>
    private const uint GenericAll = 0x10000000;

    /// <summary>
    /// Evaluates the access control list for a control access right.
    /// </summary>
    /// <param name="securityDescriptor">The target object's security descriptor.</param>
    /// <param name="callerSids">
    /// Every security identifier in the caller's context: its own, and every group it is transitively a member
    /// of. An incomplete set produces a wrong answer in the denial direction, so the caller must be sure of it
    /// before treating a denial as meaningful.
    /// </param>
    /// <param name="right">The control access right being asked about.</param>
    internal static AccessCheckOutcome Evaluate(SecurityDescriptor securityDescriptor, IReadOnlySet<string> callerSids, Guid right)
    {
        // Step 1: no access control list at all means the object is unprotected, which grants the right. This is
        // the opposite of an empty list below, and the two are easy to conflate.
        if (!securityDescriptor.DaclPresent)
            return AccessCheckOutcome.Granted;

        // Step 2: a list with no entries grants nobody anything.
        if (securityDescriptor.Aces.Count == 0)
            return AccessCheckOutcome.Denied;

        // Step 3: walk in order. The first entry that matches decides the outcome, which is why entries must be
        // evaluated strictly in the order the directory holds them rather than denies-first: canonical ordering
        // already places denies ahead of grants, and a list that departs from it means what it says.
        // Steps 3.1 to 3.6: the first entry that applies to the caller and speaks to this right decides, and
        // the walk stops there. FirstOrDefault preserves that: it evaluates in order and returns as soon as one
        // matches, so the ordering the specification depends on is intact.
        var decidingAce = securityDescriptor.Aces
            .FirstOrDefault(ace => AppliesToTheCaller(ace, callerSids) && GrantsOrDeniesTheRight(ace, right));

        // No entry matched, so the right was never granted. An entry that matched decides by its own type.
        return decidingAce is { IsAllow: true } ? AccessCheckOutcome.Granted : AccessCheckOutcome.Denied;
    }

    /// <summary>
    /// Whether an entry has anything to say about this caller.
    /// <para>
    /// Step 3.1: an inherit-only entry governs an object's children, not the object itself. Step 3.2: an entry
    /// naming a principal the caller is not falls away.
    /// </para>
    /// </summary>
    private static bool AppliesToTheCaller(AccessControlEntry ace, IReadOnlySet<string> callerSids) =>
        !ace.IsInheritOnly && ace.Sid != null && callerSids.Contains(ace.Sid.Value);

    /// <summary>
    /// Whether an entry speaks to the right in question at all.
    /// <para>
    /// An entry does so when it carries full control, or when it carries the control-access right and is either
    /// unscoped or scoped to exactly this right. An unscoped entry covers every control access right: per
    /// [MS-ADTS] 5.1.3.3.4 steps 3.3 and 3.5, an absent ObjectType matches whatever is being asked about. A plain
    /// entry that is not object-specific has no ObjectType field at all and is the same case.
    /// </para>
    /// </summary>
    private static bool GrantsOrDeniesTheRight(AccessControlEntry ace, Guid right)
    {
        if ((ace.AccessMask & GenericAll) == GenericAll)
            return true;

        if ((ace.AccessMask & ControlAccess) != ControlAccess)
            return false;

        return ace.ObjectType == null || ace.ObjectType == right;
    }
}
