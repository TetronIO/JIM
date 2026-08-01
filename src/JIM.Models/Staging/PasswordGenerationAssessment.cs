// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a <see cref="PasswordGenerationPolicy"/> would actually produce, and whether the target would accept it.
/// <para>
/// This exists because the interesting failure is not a configuration that is obviously wrong. It is one that
/// looks entirely sensible, generates a perfectly good password, and is rejected by the target on every single
/// account: <c>brown-chicken-ladder</c> offers two character categories where a stock Active Directory domain
/// wants three. Nothing about that is visible without composing the style and counting what comes out, which is
/// what this reports.
/// </para>
/// <para>
/// Everything here is a floor rather than a typical value. <see cref="GuaranteedMinimumLength"/> is the shortest
/// a generated password could be, and <see cref="GuaranteedCharacterClasses"/> the categories every generated
/// password will contain, not the categories a given one happens to. A promise about "usually" is worthless
/// when the question is whether a target will reject one account in twenty.
/// </para>
/// </summary>
public class PasswordGenerationAssessment
{
    /// <summary>
    /// The fewest characters a generated password could contain. For the styles built to a set length this is
    /// that length; for words it follows from the shortest words the list can supply.
    /// </summary>
    public required int GuaranteedMinimumLength { get; init; }

    /// <summary>
    /// The character categories every generated password is certain to contain.
    /// </summary>
    public required PasswordCharacterClasses GuaranteedCharacterClasses { get; init; }

    /// <summary>
    /// How many bits of entropy the generation process contributes, as a deliberate underestimate.
    /// <para>
    /// Counted from the draws that go into a password rather than measured off the output, and ignoring what the
    /// final shuffle or ordering adds. An estimate that errs high would be the harmful direction: it would
    /// present a weak configuration as a strong one.
    /// </para>
    /// </summary>
    public required double EntropyBits { get; init; }

    /// <summary>
    /// Everything that would stop this configuration working, in language meant for the administrator reading
    /// it. Empty means there is nothing to fix.
    /// </summary>
    public required IReadOnlyList<string> Problems { get; init; }

    /// <summary>
    /// Whether JIM can generate passwords from this configuration that the target should accept.
    /// </summary>
    public bool IsUsable => Problems.Count == 0;

    /// <summary>
    /// How many character categories <see cref="GuaranteedCharacterClasses"/> covers, which is the number
    /// systems expressing complexity as "at least N of the categories" are counting.
    /// </summary>
    public int GuaranteedCharacterClassCount => System.Numerics.BitOperations.PopCount((uint)GuaranteedCharacterClasses);
}
