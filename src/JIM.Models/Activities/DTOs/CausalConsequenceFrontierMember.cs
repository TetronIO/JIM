// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One item the downward causal walk (#1811) carries into its next level: the item, and which cohort of the level
/// before it belongs to, so its own consequences group under the right parent.
/// </summary>
/// <param name="RunProfileExecutionItemId">The item whose consequences the next level reads.</param>
/// <param name="ParentCohortOrdinal">The ordinal of the cohort the item is a member of, as the level that found it
/// numbered its cohorts.</param>
public sealed record CausalConsequenceFrontierMember(Guid RunProfileExecutionItemId, int ParentCohortOrdinal);
