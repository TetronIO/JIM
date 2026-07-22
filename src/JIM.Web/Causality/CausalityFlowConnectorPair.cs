// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// A logical Flow view connector between two measured elements, identified by their
/// <c>data-flow-id</c> values, before geometry is applied.
/// </summary>
/// <param name="FromId">The flow id of the element the connector leaves (its right edge).</param>
/// <param name="ToId">The flow id of the element the connector enters (its left edge).</param>
public sealed record CausalityFlowConnectorPair(string FromId, string ToId);
