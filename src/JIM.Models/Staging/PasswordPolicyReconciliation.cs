// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One set of generator settings that satisfies every selected Connected System at once, and what had to be
/// assumed to get there (issue #1172).
/// <para>
/// Setting one password across several systems means satisfying the strictest length any of them demands and
/// the character categories all of them count, not the rules of whichever system happens to be first. This is
/// the result of working that out.
/// </para>
/// </summary>
public class PasswordPolicyReconciliation
{
    /// <summary>
    /// The generator settings to use. Derived from the combined constraints, so a password produced from it
    /// satisfies every system whose policy JIM has read.
    /// </summary>
    public required PasswordGenerationPolicy Policy { get; init; }

    /// <summary>
    /// What the combined rules amount to, in language meant to be shown to an administrator beside the Generate
    /// button ("15 characters or more", "three character categories"). Empty when no selected system published
    /// any constraint at all.
    /// </summary>
    public required IReadOnlyList<string> Constraints { get; init; }

    /// <summary>
    /// The systems JIM has never read a password policy from. Their rules are not in <see cref="Policy"/> and
    /// cannot be, so a password can only be checked against them by sending it.
    /// </summary>
    public required IReadOnlyList<string> SystemsWithNoDiscoveredPolicy { get; init; }

    /// <summary>
    /// Systems the derived settings would not satisfy, named with the reason.
    /// <para>
    /// A guard rather than an expected outcome. The settings are derived <i>from</i> the combined constraints,
    /// so they satisfy them by construction, and JIM discovers no maximum length that could contradict a
    /// minimum. This exists so that the day a constraint is added which can conflict, the conflict is reported
    /// before a password is generated rather than discovered as a rejection on the second account, after the
    /// first has already been changed.
    /// </para>
    /// </summary>
    public required IReadOnlyList<string> Conflicts { get; init; }

    /// <summary>
    /// Whether one password can satisfy every selected system.
    /// </summary>
    public bool IsUsable => Conflicts.Count == 0;

    /// <summary>
    /// Whether any selected system may apply a stricter policy to some accounts, or JIM could not tell. The
    /// combined constraints are a floor in that case, so a generated password can still be refused.
    /// </summary>
    public required bool MayBeStricterThanDiscovered { get; init; }
}
