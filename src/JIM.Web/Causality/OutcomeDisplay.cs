// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The complete display mapping for a sync outcome type: the single label shown for it, the visual
/// tone, and the Material icon.
/// </summary>
/// <param name="Label">
/// The outcome's one name, in the same vocabulary the rest of the portal uses ("Attributes flowed",
/// "Projected to the Metaverse"). JIM does not rename its product nouns, and it does not offer a second,
/// more technical name beside them either: "Metaverse Object" and "Connected System Object" are used in
/// full where a noun is needed, and internal shorthands ("MVO", "CSO") never reach this label.
/// </param>
/// <param name="Tone">Visual tone for colour coding.</param>
/// <param name="Icon">Material icon string.</param>
/// <param name="SentenceForm">
/// The label as a bare-infinitive clause ("become eligible for deletion"), for surfaces that state an outcome as a
/// sentence rather than as a column heading: "2 objects would " + this. Bare infinitive so the sentence is
/// grammatical for one object and for a million, without the surface conjugating anything. Null where the
/// outcome has no such surface: only the Configuration Change Preview transitions (#827) need one, and inventing
/// the rest would be a vocabulary nobody reads and nobody keeps true. It lives here rather than in a second map
/// beside the preview because an outcome's words belong in one place; splitting them is how two of them drift.
/// </param>
/// <param name="SpeculativeLabel">
/// The full conditional-mood sentence fragment ("A Metaverse Object would be projected") a Sync Preview's
/// speculative causality tree (#1519, D-S1) substitutes for <see cref="Label"/>. Distinct from
/// <see cref="SentenceForm"/>'s bare infinitive: this reads as a complete card title on its own, the way
/// <see cref="Label"/> does for the recorded tree, rather than completing an external "would " prefix.
/// Populated only for the bounded set of outcome types <c>SyncPreviewServer</c> can emit (see
/// <see cref="OutcomeDisplayMap.GetSpeculativeLabel"/> and its completeness test); null for every outcome
/// type the preview engine never produces, since inventing conditional wording nothing ever exercises is
/// exactly the vocabulary drift <see cref="SentenceForm"/>'s own doc comment warns against.
/// </param>
/// <param name="StatedInVerdict">
/// Whether a Configuration Change Preview's leading sentence states this transition (#134). False for a transition
/// with no effect anyone would notice, such as a contributor changing while the value stays identical: the preview
/// still counts and groups it, sorted after everything that matters, but stating its (typically largest) number in
/// the verdict would bury the consequences that do. True for everything else.
/// </param>
public sealed record OutcomeDisplay(
    string Label,
    CausalityTone Tone,
    string Icon,
    string? SentenceForm = null,
    string? SpeculativeLabel = null,
    bool StatedInVerdict = true);
