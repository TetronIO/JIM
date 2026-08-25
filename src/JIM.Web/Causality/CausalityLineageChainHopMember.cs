// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One cause inside an expanded plural cohort card (#1495): its snapshotted name and the link to
/// the item that recorded it, where one exists and is not the page being read.
/// </summary>
/// <param name="DisplayName">The cause's name as snapshotted when the edge was written.</param>
/// <param name="ActivityItemHref">Link to the recording item, or null where there is nothing useful to link.</param>
public sealed record CausalityLineageChainHopMember(string DisplayName, string? ActivityItemHref);
