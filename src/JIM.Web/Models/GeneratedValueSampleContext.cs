// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The Metaverse and Connected System sample attribute dictionaries built for the "JIM generates it" live
/// preview's base expression evaluation (Unique Value Generation, #242, Phase 3), plus whether they came from
/// the administrator's own "Test this Expression" values or from
/// <see cref="GeneratedValuePreviewHelpers.NeutralSampleValue"/> placeholders. See
/// <see cref="GeneratedValuePreviewHelpers.BuildSampleContext"/>.
/// </summary>
public sealed record GeneratedValueSampleContext(
    Dictionary<string, object?> Metaverse, Dictionary<string, object?> ConnectedSystem, bool UsedNeutralSample);
