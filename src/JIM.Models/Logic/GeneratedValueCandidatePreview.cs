// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A pure, no-I/O preview of what a generated mapping's uniqueness token would produce against a sample base
/// value (Unique Value Generation, #242, Phase 3: <c>DescribeGeneratedCandidates</c>). Built on
/// <c>UniqueValueCandidates</c>, the same placement, padding and random-drawing code the real engine uses, so a
/// form's live preview matches what generation would actually do. Never touches a repository: a Sequence
/// preview starts from the mapping's configured <see cref="SyncRuleMappingGeneration.SequenceStart"/>, not the
/// attribute's live counter (see <c>GeneratedValueSequenceState</c> for that).
/// </summary>
public class GeneratedValueCandidatePreview
{
    /// <summary>
    /// The first value JIM would try. Set for <see cref="GeneratedValueTokenKind.OnlyIfTaken"/> (the bare base
    /// value) and <see cref="GeneratedValueTokenKind.Sequence"/> (the base value plus the flow's configured
    /// start number); null for <see cref="GeneratedValueTokenKind.Random"/>, which has no predictable first
    /// value.
    /// </summary>
    public string? FirstValue { get; set; }

    /// <summary>
    /// The next candidates after <see cref="FirstValue"/>: the collision-suffixed values an only-if-taken token
    /// would try in order, or the next sequence numbers a Sequence token would issue in order. Empty for
    /// <see cref="GeneratedValueTokenKind.Random"/>.
    /// </summary>
    public List<string> Candidates { get; set; } = [];

    /// <summary>
    /// A single example value, drawn from the real cryptographic random source. Set only for
    /// <see cref="GeneratedValueTokenKind.Random"/>, whose candidates are not otherwise predictable.
    /// </summary>
    public string? Example { get; set; }

    /// <summary>
    /// A short, human-readable sentence describing where JIM places the token relative to the base value (before
    /// the first <c>@</c>, or appended to the end), for the form's "When JIM generates this value" statement.
    /// </summary>
    public string PlacementText { get; set; } = string.Empty;
}
