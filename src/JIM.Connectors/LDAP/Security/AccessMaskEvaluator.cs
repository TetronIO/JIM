// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP.Security;

/// <summary>
/// Decides whether a security context holds one ordinary access-mask right on an object, per [MS-ADTS] 5.1.3.3.2
/// for simple entries and 5.1.3.3.3 for object-specific ones.
/// <para>
/// The companion to <see cref="ControlAccessRightEvaluator"/>, for the rights that are bits in an entry's mask
/// rather than GUIDs: whether the caller may list a container's children, or read an object's properties. The
/// first question JIM asks of it is whether the bound account holds List Contents on a partition's Deleted
/// Objects container, because a Delta Import that lacks it is not refused; it simply finds no deletions.
/// </para>
/// </summary>
internal static class AccessMaskEvaluator
{
    /// <summary>RIGHT_DS_LIST_CONTENTS. Permits enumerating the children of a container.</summary>
    internal const uint ListContents = 0x00000004;

    /// <summary>RIGHT_DS_WRITE_PROPERTY_EXTENDED. Permits the validated writes ([MS-ADTS] 5.1.3.2.2).</summary>
    private const uint ValidatedWrite = 0x00000008;

    /// <summary>RIGHT_DS_READ_PROP. Permits reading an object's properties.</summary>
    internal const uint ReadProperty = 0x00000010;

    /// <summary>RIGHT_DS_WRITE_PROP. Permits writing an object's properties.</summary>
    private const uint WriteProperty = 0x00000020;

    /// <summary>RIGHT_DS_LIST_OBJECT. Permits seeing that an object exists when List Contents on its parent is withheld.</summary>
    private const uint ListObject = 0x00000080;

    /// <summary>READ_CONTROL. Permits reading an object's security descriptor.</summary>
    private const uint ReadControl = 0x00020000;

    /// <summary>
    /// GENERIC_ALL. Full control, which subsumes every right.
    /// <para>
    /// Active Directory maps the generic bits to the specific rights they stand for when a descriptor is stored
    /// ([MS-ADTS] 5.1.3.2), so an effective entry read back from it should not carry one. The evaluator expands
    /// them anyway, so that a directory which stores what it was sent, or an entry written by tooling that
    /// bypasses the mapping, is read as the administrator meant rather than as a denial.
    /// </para>
    /// </summary>
    private const uint GenericAll = 0x10000000;

    /// <summary>GENERIC_EXECUTE. Maps to Read Control and List Contents ([MS-ADTS] 5.1.3.2).</summary>
    private const uint GenericExecute = 0x20000000;

    /// <summary>GENERIC_WRITE. Maps to Read Control, Write Property and Validated Write ([MS-ADTS] 5.1.3.2).</summary>
    private const uint GenericWrite = 0x40000000;

    /// <summary>GENERIC_READ. Maps to Read Control, List Contents, Read Property and List Object ([MS-ADTS] 5.1.3.2).</summary>
    private const uint GenericRead = 0x80000000;

    /// <summary>The rights GENERIC_READ stands for ([MS-ADTS] 5.1.3.2).</summary>
    private const uint ReadFamily = ReadControl | ListContents | ReadProperty | ListObject;

    /// <summary>The rights GENERIC_WRITE stands for ([MS-ADTS] 5.1.3.2).</summary>
    private const uint WriteFamily = ReadControl | WriteProperty | ValidatedWrite;

    /// <summary>The rights GENERIC_EXECUTE stands for ([MS-ADTS] 5.1.3.2).</summary>
    private const uint ExecuteFamily = ReadControl | ListContents;

    /// <summary>
    /// Evaluates the access control list for one right.
    /// </summary>
    /// <param name="securityDescriptor">The target object's security descriptor.</param>
    /// <param name="callerSids">
    /// Every security identifier in the caller's context: its own, and every group it is transitively a member
    /// of. An incomplete set produces a wrong answer in the denial direction, so the caller must be sure of it
    /// before treating a denial as meaningful.
    /// </param>
    /// <param name="right">
    /// The right being asked about, as exactly one bit of the access mask. The evaluator answers one right at a
    /// time: [MS-ADTS] 5.1.3.3.2 accumulates the remaining requested rights across entries, and a first-match
    /// walk is only equivalent to that when there is a single right left to find.
    /// </param>
    /// <param name="objectClass">
    /// The schemaIDGUID of the object's own class, when the caller knows it. The root of the object tree carries
    /// that GUID ([MS-ADTS] 5.1.3.3.3 step 1), so an object-specific entry whose ObjectType equals it addresses the
    /// object as a whole and is read like an unscoped entry. Without it, every entry carrying an ObjectType is
    /// taken to be scoped below the root, which can under-report a grant but never fabricates a denial.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="right"/> is not exactly one bit.</exception>
    internal static AccessCheckOutcome Evaluate(SecurityDescriptor securityDescriptor, IReadOnlySet<string> callerSids, uint right, Guid? objectClass = null)
    {
        if (right == 0 || (right & (right - 1)) != 0)
            throw new ArgumentException(
                "The evaluator answers one right at a time, so that the first entry to apply decides the outcome; [MS-ADTS] 5.1.3.3.2 accumulates remaining rights across entries, which a single-bit question does not need.",
                nameof(right));

        // Step 1: no access control list at all means the object is unprotected, which grants the right. The
        // opposite of an empty list below, and the two are easy to conflate.
        if (!securityDescriptor.DaclPresent)
            return AccessCheckOutcome.Granted;

        // Step 2: a list with no entries grants nobody anything.
        if (securityDescriptor.Aces.Count == 0)
            return AccessCheckOutcome.Denied;

        // Step 3, the owner rule (the object's owner holds READ_CONTROL and WRITE_DAC regardless of the list), is
        // deliberately not modelled: the rights JIM asks about are List Contents and Read Property, which the
        // owner rule never grants.

        // Step 4: walk in the order the directory holds the entries. The first one that applies to the caller and
        // speaks to this right decides, which is why the walk must not prefer denies: canonical ordering already
        // places them first, and a list that departs from it means what it says. FirstOrDefault evaluates in order
        // and returns at the first match, so that ordering is intact.
        var decidingAce = securityDescriptor.Aces
            .FirstOrDefault(ace => AppliesToTheCaller(ace, callerSids) && GrantsOrDeniesTheRight(ace, right, objectClass));

        // Step 5: no entry matched, so the right was never granted. An entry that matched decides by its own type.
        return decidingAce is { IsAllow: true } ? AccessCheckOutcome.Granted : AccessCheckOutcome.Denied;
    }

    /// <summary>
    /// Whether an entry has anything to say about this caller.
    /// <para>
    /// An inherit-only entry governs an object's children, not the object itself. An entry naming a principal the
    /// caller is not falls away.
    /// </para>
    /// </summary>
    private static bool AppliesToTheCaller(AccessControlEntry ace, IReadOnlySet<string> callerSids) =>
        !ace.IsInheritOnly && ace.Sid != null && callerSids.Contains(ace.Sid.Value);

    /// <summary>
    /// Whether an entry speaks to the right in question at all.
    /// <para>
    /// The plain right is a question about the object as a whole, which is the root of the object tree in
    /// [MS-ADTS] 5.1.3.3.3. An entry with no ObjectType, or whose ObjectType is the object's own class (the GUID
    /// the root carries), addresses that root (steps 3.4 and 3.5, with v the root, and 3.7 and 3.8 for a deny).
    /// Any other ObjectType names a property, property set or child class, which sits below the root, and the
    /// entry is skipped.
    /// </para>
    /// <para>
    /// Two knowing simplifications. A grant scoped below the root is not treated as a grant on the object, which
    /// is what step 3.5 says, except that the rule completing a grant upwards once every sibling is granted
    /// (step 3.5.2) is not modelled. A deny scoped below the root is not propagated up to the root as step 3.8
    /// requires. Each can make the evaluator over-report a grant; neither can make it fabricate a denial, which is
    /// the direction its callers must never get wrong.
    /// </para>
    /// <para>
    /// An entry addressing the root speaks to the right when its mask carries the bit, once any generic bits are
    /// expanded to the rights they stand for.
    /// </para>
    /// </summary>
    private static bool GrantsOrDeniesTheRight(AccessControlEntry ace, uint right, Guid? objectClass) =>
        (ace.ObjectType == null || ace.ObjectType == objectClass) && (ExpandGenericBits(ace.AccessMask) & right) == right;

    /// <summary>
    /// The mask with each generic bit replaced by the specific rights it stands for ([MS-ADTS] 5.1.3.2), so an
    /// allow and a deny carrying a generic bit are read the same way. GENERIC_ALL is every right.
    /// </summary>
    private static uint ExpandGenericBits(uint mask)
    {
        if ((mask & GenericAll) != 0)
            return uint.MaxValue;

        var effective = mask;
        if ((mask & GenericRead) != 0) effective |= ReadFamily;
        if ((mask & GenericWrite) != 0) effective |= WriteFamily;
        if ((mask & GenericExecute) != 0) effective |= ExecuteFamily;
        return effective;
    }
}
