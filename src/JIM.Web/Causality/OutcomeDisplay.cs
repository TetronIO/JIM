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
public sealed record OutcomeDisplay(string PlainLabel, string TechnicalLabel, CausalityTone Tone, string Icon);
