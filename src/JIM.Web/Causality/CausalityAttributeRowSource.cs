// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The change record an event's attribute rows are read from, a window at a time, when there are too many to carry
/// with the event (#2498).
/// </summary>
public sealed record CausalityAttributeRowSource(CausalityChangeSide Side, Guid ChangeId);
