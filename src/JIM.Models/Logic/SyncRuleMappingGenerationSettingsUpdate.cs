// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A change to an existing generated mapping's <see cref="SyncRuleMappingGeneration"/> settings (Unique Value
/// Generation, #242, Phase 3). Every property is optional; a null one leaves the mapping's current value alone.
/// Carried inside <see cref="SyncRuleMappingSettingsUpdate.Generation"/>; applying it to a mapping that is not
/// currently a generated mapping (<see cref="SyncRuleMapping.Generation"/> is null) is refused, the same as every
/// other direction-scoped setting on the parent type.
/// </summary>
/// <remarks>
/// Deliberately excludes <see cref="SyncRuleMappingGeneration.Exclusions"/> and
/// <see cref="SyncRuleMappingGeneration.CollisionRemediation"/>: exclusions are release 4 surfaces and Collision
/// Remediation is a release 4 feature, so neither is settable from any surface yet (plan releases 1 and 4).
/// </remarks>
public class SyncRuleMappingGenerationSettingsUpdate
{
    /// <summary>Replaces which uniqueness token the mapping appends.</summary>
    public GeneratedValueTokenKind? TokenKind { get; set; }

    /// <summary>Only-if-taken: whether the collision suffix is a number or a letter.</summary>
    public GeneratedValueSuffixStyle? SuffixStyle { get; set; }

    /// <summary>Only-if-taken: the first suffix value tried once the bare base value is taken.</summary>
    public int? SuffixStart { get; set; }

    /// <summary>
    /// Sequence: the lowest number this flow will ever issue. Raising this above the attribute's counter moves
    /// the counter forward at save time (plan decision 3); the save response reports the move.
    /// </summary>
    public long? SequenceStart { get; set; }

    /// <summary>Sequence: how much the counter advances per issued number.</summary>
    public int? SequenceIncrement { get; set; }

    /// <summary>
    /// Sequence: zero-pads the number to this many digits. <c>0</c> clears the fixed width back to none; a
    /// positive value sets it; omitted (null) leaves it unchanged. There is no valid fixed width of 0, so this
    /// sentinel introduces no ambiguity with a genuine value.
    /// </summary>
    public int? FixedWidth { get; set; }

    /// <summary>Sequence: what happens when a number would no longer fit <see cref="FixedWidth"/>.</summary>
    public GeneratedValueWidthOverflowBehaviour? OnWidthExceeded { get; set; }

    /// <summary>Random: the shape of the random token.</summary>
    public GeneratedValueRandomFormat? RandomFormat { get; set; }

    /// <summary>Random: the length of the generated string, in characters.</summary>
    public int? RandomLength { get; set; }

    /// <summary>
    /// The characters placed between the base value and the token. An empty or whitespace-only string clears
    /// the stored separator to null (matching the REST convention for clearable optional strings); omitted
    /// (null) leaves it unchanged.
    /// </summary>
    public string? Separator { get; set; }

    /// <summary>The maximum number of candidates tried per object per synchronisation run before generation fails.</summary>
    public int? AttemptLimit { get; set; }

    /// <summary>
    /// Whether a value whose assignment is deleted is retired and never issued again. Sequence tokens force this
    /// on regardless of what is supplied here (<see cref="SyncRuleMappingGenerationValidator"/>).
    /// </summary>
    public bool? NeverReuse { get; set; }

    /// <summary>
    /// True when the update names at least one generation setting.
    /// </summary>
    public bool HasChanges =>
        TokenKind.HasValue ||
        SuffixStyle.HasValue ||
        SuffixStart.HasValue ||
        SequenceStart.HasValue ||
        SequenceIncrement.HasValue ||
        FixedWidth.HasValue ||
        OnWidthExceeded.HasValue ||
        RandomFormat.HasValue ||
        RandomLength.HasValue ||
        Separator != null ||
        AttemptLimit.HasValue ||
        NeverReuse.HasValue;
}
