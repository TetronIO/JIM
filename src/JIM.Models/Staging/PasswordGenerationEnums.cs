// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The shape of password JIM generates.
/// <para>
/// An initial password is transcribed by humans far more often than a permanent one: read out by a service desk,
/// typed off an onboarding sheet, entered on a phone keyboard. Style exists because the most secure-looking
/// option is not automatically the best one for a password whose whole job is to survive being copied by hand
/// once.
/// </para>
/// </summary>
public enum PasswordGenerationStyle
{
    /// <summary>
    /// Characters drawn at random from the permitted set, for example <c>t7Rm#qK4vHx2Ndbf</c>. The most entropy
    /// per character, and the hardest to read out.
    /// </summary>
    RandomCharacters = 0,

    /// <summary>
    /// Words drawn at random from a fixed list, for example <c>Brown-Chicken-Ladder-47</c>. Longer, but a person
    /// can hear it, hold it in their head and type it.
    /// </summary>
    Words = 1,

    /// <summary>
    /// Invented but sayable syllables, for example <c>tovanic-hupelo-92</c>. A middle ground: shorter than a
    /// passphrase, and unlike random characters it can be pronounced.
    /// </summary>
    Pronounceable = 2
}

/// <summary>
/// Where a Synchronisation Rule's initial-password generator takes its settings from.
/// </summary>
public enum InitialPasswordSource
{
    /// <summary>
    /// Follow the password policy JIM discovered on the Connected System, and keep following it if the target's
    /// policy is re-read and has changed.
    /// <para>
    /// The default, and the reason the common case needs no configuration. Where nothing was discovered, this
    /// falls back to JIM's own defaults, which satisfy a stock Active Directory domain.
    /// </para>
    /// </summary>
    Discovered = 0,

    /// <summary>
    /// Use the configuration held on this Synchronisation Rule, which an administrator has set deliberately and
    /// which JIM will not change underneath them.
    /// </summary>
    Custom = 1,

    /// <summary>
    /// Set one password an administrator chose on every account the rule provisions, rather than generating a
    /// different one per account.
    /// <para>
    /// <b>Not recommended, and the portal says so.</b> Every account the rule provisions shares this password
    /// until each person changes it, so anybody who learns of this can sign in as any new starter who has not.
    /// It exists because the alternative is worse for the people who need it: JIM stores no generated password,
    /// so without this there is no way to tell a new starter what to sign in with, and every account needs a
    /// password set by hand instead. Delivering a generated password to somebody who should have it (#1252) is
    /// the answer that replaces this one.
    /// </para>
    /// <para>
    /// This is the only password value JIM stores. It is stored encrypted and cannot be shown to anybody again,
    /// by any surface; see <see cref="JIM.Models.Logic.SyncRuleInitialPassword.StaticPasswordEncryptedValue"/>.
    /// </para>
    /// </summary>
    Static = 2
}

/// <summary>
/// What goes between the words of a generated passphrase.
/// <para>
/// Deliberately a separate axis from <see cref="PasswordWordCapitalisation"/>. The two combine to express every
/// common convention with two controls; folding them into one enum would need a dozen entries to cover the same
/// ground, and would make the impossible combinations look like deliberate choices.
/// </para>
/// </summary>
public enum PasswordWordSeparator
{
    /// <summary>Nothing between the words, for example <c>BrownChickenLadder</c>.</summary>
    None = 0,

    Hyphen = 1,

    FullStop = 2,

    Underscore = 3,

    /// <summary>
    /// A different random digit between each pair of words. Adds a digit category without appending anything.
    /// </summary>
    Digit = 4,

    /// <summary>
    /// A different random symbol between each pair of words, drawn from the policy's permitted symbols.
    /// </summary>
    RandomSymbol = 5
}

/// <summary>
/// How the words of a generated passphrase are cased.
/// </summary>
public enum PasswordWordCapitalisation
{
    /// <summary>
    /// Every word lowercase. Note that this yields no uppercase category, which matters where the target counts
    /// character categories.
    /// </summary>
    Lowercase = 0,

    /// <summary>The first letter of every word, for example <c>Brown-Chicken-Ladder</c>.</summary>
    EachWord = 1,

    /// <summary>Every letter of every word.</summary>
    Uppercase = 2,

    /// <summary>The first letter of the first word only, for example <c>Brown-chicken-ladder</c>.</summary>
    FirstWordOnly = 3,

    /// <summary>
    /// The first letter of one word chosen at random. Harder to transcribe than the fixed options, and worth
    /// only <c>log2(word count)</c> extra bits, so it is offered rather than recommended.
    /// </summary>
    RandomWord = 4
}
