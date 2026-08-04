// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Categories of character a password policy can require a password to draw from.
/// <para>
/// Modelled as flags so a Connected System can declare which categories count towards its requirement, rather
/// than JIM assuming a fixed set. Active Directory counts all five; other systems may recognise fewer.
/// </para>
/// </summary>
[Flags]
public enum PasswordCharacterClasses
{
    None = 0,

    /// <summary>Uppercase letters.</summary>
    Uppercase = 1,

    /// <summary>Lowercase letters.</summary>
    Lowercase = 2,

    /// <summary>Base 10 digits, 0 to 9.</summary>
    Digit = 4,

    /// <summary>Non-alphanumeric characters, such as punctuation and symbols.</summary>
    Symbol = 8,

    /// <summary>
    /// Alphabetic characters that are neither uppercase nor lowercase, which is how scripts without letter case
    /// are counted. Active Directory recognises this as a category in its own right.
    /// </summary>
    OtherUnicodeLetter = 16
}

/// <summary>
/// Whether a Connected System has password policies that apply to some accounts in place of the system-wide one.
/// <para>
/// This is deliberately a three-state signal rather than a boolean. Reading the policies themselves usually
/// requires privileges JIM's service account should not need, so "JIM was not allowed to look" is a genuinely
/// different answer from "there are none", and conflating them would turn an unknown into a false reassurance.
/// JIM detects their presence and does not enumerate them.
/// </para>
/// </summary>
public enum FineGrainedPolicySignal
{
    /// <summary>
    /// JIM established that none can exist, so the discovered policy applies to every account.
    /// <para>
    /// Note this is a stronger claim than "the search came back empty", and is reported only when the target
    /// proves it. Directories commonly apply access control to searches as a silent filter, returning a
    /// successful but empty result to a caller with no rights over where these policies live, so an empty result
    /// means <see cref="CouldNotDetermine"/> rather than this.
    /// </para>
    /// </summary>
    Absent = 0,

    /// <summary>
    /// JIM found policies that override the system-wide one for some accounts. The discovered policy is a floor,
    /// not a guarantee, and an account in an affected population may be held to something stricter.
    /// </summary>
    Present = 1,

    /// <summary>
    /// JIM could not tell, most often because the service account was refused access to where these policies are
    /// held. Treat the discovered policy as a floor and expect rejections to be possible.
    /// </summary>
    CouldNotDetermine = 2
}

/// <summary>
/// Whether trying a password set again is worth anything, which is the most useful single thing to tell an
/// administrator looking at a failure (issue #1172).
/// </summary>
public enum PasswordRetryVerdict
{
    /// <summary>
    /// The Connected System read this password and refused it, so sending the same one again fails identically.
    /// A different password may well be accepted.
    /// </summary>
    RetryWithADifferentPassword = 0,

    /// <summary>
    /// Nothing was established about the password. The same request is worth repeating once whatever went wrong
    /// has stopped going wrong.
    /// </summary>
    RetryUnchanged = 1,

    /// <summary>
    /// Nothing changes until a person alters something outside JIM, most often granting a right. Retrying in
    /// the meantime achieves nothing.
    /// </summary>
    NeedsSomebodyElse = 2,

    /// <summary>
    /// The Connected System will answer identically for ever, so no retry is offered at all.
    /// </summary>
    NeverHelps = 3
}
