// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// How many objects a proposed change would move through one transition, from set-based SQL alone. Stage 2 is the
/// minimum any destructive surface must implement: "this will disconnect 4,812 accounts" is the number an
/// administrator needs before consenting, and it is answerable without evaluating a single object individually.
/// </summary>
/// <param name="TransitionType">What would happen to the objects counted.</param>
/// <param name="ObjectCount">How many objects. Exact, not sampled: this is a count query, not an evaluation.</param>
/// <param name="ConnectedSystemId">
/// The Connected System the count applies to, where the transition is per-system. Null for counts that are not.
/// </param>
/// <param name="MetaverseObjectTypeId">
/// The Metaverse Object Type the count applies to, where the transition is per-type. Null otherwise.
/// </param>
public record PreviewImpactCount(
    ActivityRunProfileExecutionItemSyncOutcomeType TransitionType,
    int ObjectCount,
    int? ConnectedSystemId = null,
    int? MetaverseObjectTypeId = null);
