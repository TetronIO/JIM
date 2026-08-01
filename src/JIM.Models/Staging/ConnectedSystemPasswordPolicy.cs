// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The password policy JIM discovered on a Connected System, expressed in terms no particular system owns.
/// <para>
/// This exists so that an administrator configuring an initial password does not have to know, or retype, the
/// rules the target already enforces. JIM reads them once during schema discovery and pre-fills the generator
/// from them.
/// </para>
/// <para>
/// <b>A discovered policy is a floor, not a guarantee.</b> Systems routinely allow a stricter policy to be
/// applied to some accounts, and some run custom password filters that are not exposed over any protocol and
/// cannot be discovered at all. A password satisfying everything recorded here can still be rejected, which is
/// why rejection handling is a required part of the feature rather than an edge case.
/// </para>
/// </summary>
public class ConnectedSystemPasswordPolicy
{
    public int Id { get; set; }

    /// <summary>
    /// The Connected System this policy was discovered on.
    /// </summary>
    public ConnectedSystem ConnectedSystem { get; set; } = null!;

    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// When the policy was last read from the Connected System. Shown to administrators so they can judge how
    /// current it is, and refreshed whenever the schema is.
    /// </summary>
    public DateTime Discovered { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The fewest characters the Connected System will accept in a password. Null when the system did not report
    /// one.
    /// </summary>
    public int? MinimumLength { get; set; }

    /// <summary>
    /// Whether the Connected System requires passwords to draw on several categories of character. Null when the
    /// system did not report it.
    /// <para>
    /// When true, <see cref="RequiredCharacterClassCount"/> and <see cref="RecognisedCharacterClasses"/> say what
    /// that means in practice.
    /// </para>
    /// </summary>
    public bool? ComplexityRequired { get; set; }

    /// <summary>
    /// How many distinct character categories a password must contain, where the system expresses its complexity
    /// rule that way. Active Directory requires three. Null when the system has no such rule, or expresses
    /// complexity in a way that does not reduce to counting categories.
    /// </summary>
    public int? RequiredCharacterClassCount { get; set; }

    /// <summary>
    /// The character categories that count towards <see cref="RequiredCharacterClassCount"/>. Active Directory
    /// recognises all five.
    /// </summary>
    public PasswordCharacterClasses RecognisedCharacterClasses { get; set; } = PasswordCharacterClasses.None;

    /// <summary>
    /// How many previous passwords the Connected System remembers and refuses to let an account reuse. Null when
    /// the system did not report one.
    /// <para>
    /// JIM does not use this when generating a password (a generated password is effectively never a repeat), but
    /// it is shown to administrators because it explains rejections they may see on accounts being re-provisioned.
    /// </para>
    /// </summary>
    public int? PasswordHistoryLength { get; set; }

    /// <summary>
    /// How long a password remains valid before the Connected System requires it to be changed. Null when
    /// passwords on this system do not expire.
    /// </summary>
    public TimeSpan? MaximumPasswordAge { get; set; }

    /// <summary>
    /// How long a password must be held before it can be changed again. Null when the system imposes no wait.
    /// <para>
    /// Worth surfacing because a non-zero value is a common and confusing cause of a rejected password change
    /// immediately after provisioning.
    /// </para>
    /// </summary>
    public TimeSpan? MinimumPasswordAge { get; set; }

    /// <summary>
    /// Whether accounts on this Connected System may be governed by a policy other than the one recorded here.
    /// See <see cref="FineGrainedPolicySignal"/> for why this is three states rather than a boolean.
    /// </summary>
    public FineGrainedPolicySignal FineGrainedPolicySignal { get; set; } = FineGrainedPolicySignal.CouldNotDetermine;

    /// <summary>
    /// Whether anything at all was discovered. A policy row where the Connected System reported nothing useful is
    /// worth distinguishing from one that genuinely has no constraints, because the two lead an administrator to
    /// very different conclusions.
    /// </summary>
    public bool HasAnyDiscoveredConstraint =>
        MinimumLength.HasValue ||
        ComplexityRequired.HasValue ||
        PasswordHistoryLength.HasValue ||
        MaximumPasswordAge.HasValue ||
        MinimumPasswordAge.HasValue;
}
