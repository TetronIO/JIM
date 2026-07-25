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
    /// JIM looked and found none, so the discovered policy is expected to apply to every account.
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
