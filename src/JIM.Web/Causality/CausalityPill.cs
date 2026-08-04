// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// An outcome category pill for the summary band, colour-coded by tone
/// (e.g. "Identity created", "11 attributes flowed", "Export queued · 11 changes").
/// </summary>
/// <param name="Label">The pill text.</param>
/// <param name="Tone">The visual tone.</param>
public sealed record CausalityPill(string Label, CausalityTone Tone);
