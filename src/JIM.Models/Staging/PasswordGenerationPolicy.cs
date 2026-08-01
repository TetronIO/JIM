// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// How JIM should generate an initial password.
/// <para>
/// This is what an administrator configures; <see cref="ConnectedSystemPasswordPolicy"/> is what the target
/// demands. The two are checked against each other before anything is generated, because the interesting failure
/// is a configuration that looks reasonable and produces passwords the target rejects on every account.
/// </para>
/// <para>
/// Every property carries a usable default, so a policy that has never been edited still generates a password
/// that satisfies a stock Active Directory domain.
/// </para>
/// </summary>
public class PasswordGenerationPolicy
{
    public PasswordGenerationStyle Style { get; set; } = PasswordGenerationStyle.RandomCharacters;

    /// <summary>
    /// How many characters to produce, for the styles built character by character
    /// (<see cref="PasswordGenerationStyle.RandomCharacters"/> and
    /// <see cref="PasswordGenerationStyle.Pronounceable"/>).
    /// <para>
    /// Ignored by <see cref="PasswordGenerationStyle.Words"/>, whose length follows from the words drawn.
    /// </para>
    /// </summary>
    public int Length { get; set; } = 16;

    #region random characters
    /// <summary>
    /// The fewest uppercase letters a generated password must contain. Satisfied by construction rather than by
    /// generating and re-rolling.
    /// </summary>
    public int MinimumUppercase { get; set; } = 1;

    public int MinimumLowercase { get; set; } = 1;

    public int MinimumDigits { get; set; } = 1;

    public int MinimumSymbols { get; set; } = 1;

    /// <summary>
    /// The symbols JIM may use.
    /// <para>
    /// Configurable because the constraint is rarely the password policy: it is some downstream system that
    /// chokes on a particular character, and which characters those are is a property of the deployment rather
    /// than something JIM can know. The default set avoids quoting and escaping trouble; see
    /// <see cref="DefaultSymbols"/>.
    /// </para>
    /// </summary>
    public string PermittedSymbols { get; set; } = DefaultSymbols;
    #endregion

    #region words
    public int WordCount { get; set; } = 4;

    public PasswordWordSeparator WordSeparator { get; set; } = PasswordWordSeparator.Hyphen;

    public PasswordWordCapitalisation WordCapitalisation { get; set; } = PasswordWordCapitalisation.EachWord;
    #endregion

    #region words and pronounceable
    /// <summary>
    /// How many digits to append, for the styles that would otherwise produce none. Zero appends nothing.
    /// <para>
    /// This is the usual way a passphrase reaches the three character categories a stock Active Directory domain
    /// requires, since words plus a hyphen only reach two.
    /// </para>
    /// </summary>
    public int AppendedDigitCount { get; set; } = 2;

    /// <summary>
    /// Whether to append one symbol, drawn from <see cref="PermittedSymbols"/>.
    /// </summary>
    public bool AppendSymbol { get; set; }
    #endregion

    /// <summary>
    /// Whether to leave out characters that are easily confused with one another when a password is read out or
    /// copied by hand. Applies to every style. See <see cref="AmbiguousCharacters"/> for the set.
    /// <para>
    /// On by default. It costs a little under half a bit per character, which is a good trade for a credential
    /// that exists to be transcribed once and then replaced.
    /// </para>
    /// </summary>
    public bool ExcludeAmbiguousCharacters { get; set; } = true;

    /// <summary>
    /// The characters excluded by <see cref="ExcludeAmbiguousCharacters"/>: the digit-letter confusions that
    /// survive most fonts and every telephone.
    /// <para>
    /// Deliberately short. Every character removed costs entropy, and stripping every pair anyone has ever
    /// misread (5/S, 2/Z, 8/B, 6/G) would empty the digit category almost entirely for no proportionate gain.
    /// </para>
    /// </summary>
    public const string AmbiguousCharacters = "Il1|O0";

    /// <summary>
    /// The symbols JIM uses unless an administrator narrows them.
    /// <para>
    /// Chosen to survive being passed around: no quotes, backslash or backtick (shell and escaping trouble), no
    /// angle brackets or ampersand (markup), no comma or semicolon (delimited files), no space, and nothing that
    /// needs escaping in an LDAP filter. What is left is still twelve symbols, which is ample for one category.
    /// </para>
    /// </summary>
    public const string DefaultSymbols = "!#$%*+-=?@^_";
}
