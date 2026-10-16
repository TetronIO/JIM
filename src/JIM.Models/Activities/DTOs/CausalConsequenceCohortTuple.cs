// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
using JIM.Models.Transactional;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// What a cohort of consequences (#1811) has in common: the values the downward walk groups on. Members sharing every
/// value read as one card with a count; any difference keeps them apart, so "Exported to 4,216" and "Export failed on
/// 2" are two cohorts rather than one. Names are deliberately absent: they are for display, and a rename between two
/// members must not split them.
/// </summary>
/// <param name="HopKind">How the consequence was reached.</param>
/// <param name="EdgeType">The recorded edge, for an <see cref="CausalConsequenceHopKind.Edge"/> or
/// <see cref="CausalConsequenceHopKind.Confirmation"/> hop.</param>
/// <param name="ReasonCode">Why the edge's cause produced it; NotSet for a derived hop.</param>
/// <param name="ConnectedSystemId">The Connected System the hop is attributed to: the edge's own attribution, the
/// synchronised system for a next synchronisation, the target system for an outstanding Pending Export.</param>
/// <param name="SyncRuleId">The Synchronisation Rule the edge names, where it names one.</param>
/// <param name="ObjectTypeName">The consequence's object type, as its item recorded it.</param>
/// <param name="AttributeName">The reference attribute a removal acted through, where the edge names one.</param>
/// <param name="ChangeType">What the consequence item did to its object.</param>
/// <param name="OutcomeSummary">The consequence item's outcome summary, so differing results split.</param>
/// <param name="PendingExportStatus">The live status of an outstanding Pending Export.</param>
public sealed record CausalConsequenceCohortTuple(
    CausalConsequenceHopKind HopKind,
    CausalEdgeType? EdgeType,
    CausalReasonCode ReasonCode,
    int? ConnectedSystemId,
    int? SyncRuleId,
    string? ObjectTypeName,
    string? AttributeName,
    ObjectChangeType? ChangeType,
    string? OutcomeSummary,
    PendingExportStatus? PendingExportStatus);
