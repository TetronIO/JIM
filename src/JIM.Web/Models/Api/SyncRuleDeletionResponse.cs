// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// Returned with 200 OK when a Synchronisation Rule was deleted straight away (keep chosen, or nothing
/// contributed): what the deletion affected, so a caller can report it without a second request.
/// </summary>
public class SyncRuleDeletionResponse
{
    /// <summary>
    /// How many Metaverse attribute values the Synchronisation Rule contributed at decision time.
    /// </summary>
    public int AffectedValueCount { get; set; }

    /// <summary>
    /// How many distinct Metaverse Objects held at least one of those values at decision time.
    /// </summary>
    public int AffectedObjectCount { get; set; }

    /// <summary>
    /// The Metaverse-Derived Attribute Flows on other Synchronisation Rules that the deletion left with a missing
    /// input, because an attribute they read lost its last enabled contributor. The deletion went ahead regardless;
    /// each flow's Missing Input Behaviour now decides what it contributes. Always present.
    /// </summary>
    public List<DependentDerivedFlow> DependentDerivedFlows { get; set; } = new();
}
