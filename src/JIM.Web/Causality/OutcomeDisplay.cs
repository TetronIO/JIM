// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The complete display mapping for a sync outcome type: the plain-language label shown first, the
/// technical label demoted alongside it, the visual tone, and the Material icon.
/// </summary>
/// <param name="PlainLabel">Plain-language label (e.g. "Identity created").</param>
/// <param name="TechnicalLabel">Technical label (e.g. "MVO Projected").</param>
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
public sealed record OutcomeDisplay(
    string PlainLabel,
    string TechnicalLabel,
    CausalityTone Tone,
    string Icon,
    string? SentenceForm = null);
