// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The relationship between two adjacent lineage columns (#1495): "imported", "projected", "joined",
/// "provisioned" or "exported". Null where the pair has no stated relationship (either side is the
/// trailing unassigned column, or two records sit adjacent with no Identity between them).
/// </summary>
/// <param name="Label">The relationship label, lower-case for rendering inside the connector.</param>
public sealed record CausalityLineageJoin(string? Label);
