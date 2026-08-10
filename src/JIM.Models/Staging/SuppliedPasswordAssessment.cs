// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a password an administrator supplied actually contains, and whether the target would accept it.
/// <para>
/// The counterpart of <see cref="PasswordGenerationAssessment"/> for the one case where JIM is handed a password
/// rather than producing one: the static initial password on a Synchronisation Rule. The question is the same
/// (will every account this is set on accept it?) and the answer matters more, because a static password is set
/// on every account the rule provisions, so a refusal is not one account's problem but every account's.
/// </para>
/// <para>
/// <b>Deliberately reports no entropy figure.</b> Entropy is a property of how a value was chosen, not of the
/// value itself: JIM can count the characters in a password somebody typed but knows nothing about the process
/// that produced it, and the usual character-composition estimate would score a memorable phrase far above what
/// an attacker guessing phrases would need. Reporting a figure JIM cannot stand behind is worse than reporting
/// none, particularly beside an option the portal already recommends against.
/// </para>
/// <para>
/// Holds nothing derived from the password beyond its length and the categories present, so that it can travel
/// into an Activity or a portal alert without carrying the password with it.
/// </para>
/// </summary>
public class SuppliedPasswordAssessment
{
    /// <summary>
    /// How many characters the password contains.
    /// </summary>
    public required int Length { get; init; }

    /// <summary>
    /// The character categories the password contains, read from the password itself rather than promised by a
    /// configuration.
    /// </summary>
    public required PasswordCharacterClasses CharacterClasses { get; init; }

    /// <summary>
    /// Everything that would stop this password working, in language meant for the administrator reading it.
    /// Empty means there is nothing to fix.
    /// <para>
    /// Never quotes the password, because these strings are displayed, logged against a parked account and
    /// carried on Activities.
    /// </para>
    /// </summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>
    /// Whether the target should accept this password.
    /// </summary>
    public bool IsUsable => Problems.Count == 0;

    /// <summary>
    /// How many character categories the password covers, which is the number systems expressing complexity as
    /// "at least N of the categories" are counting.
    /// </summary>
    public int CharacterClassCount => System.Numerics.BitOperations.PopCount((uint)CharacterClasses);
}
