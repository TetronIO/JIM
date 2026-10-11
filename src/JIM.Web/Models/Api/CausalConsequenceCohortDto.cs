// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Transactional;

namespace JIM.Web.Models.Api;

/// <summary>
/// Consequences sharing everything but the object they happened to, counted exactly; the first members are included
/// and the rest are paged with <see cref="CohortKey"/>.
/// </summary>
public class CausalConsequenceCohortDto
{
    /// <summary>
    /// Identifies the cohort for paging its members
    /// (<c>GET /activities/items/{id}/consequences/cohorts/{cohortKey}/members</c>). Opaque: pass it back as given.
    /// </summary>
    public string CohortKey { get; set; } = string.Empty;

    /// <summary>
    /// How the consequences were reached: Edge (a recorded cause), Confirmation (the import confirming an export),
    /// NextSynchronisation (the synchronisation after an import), OutstandingPendingExport or ScheduledDeletion
    /// (work not finished yet, read live).
    /// </summary>
    public CausalConsequenceHopKind HopKind { get; set; }

    /// <summary>
    /// The recorded edge, for an Edge or Confirmation hop.
    /// </summary>
    public CausalEdgeType? EdgeType { get; set; }

    /// <summary>
    /// Why the edge's cause produced these; NotSet for a derived hop.
    /// </summary>
    public CausalReasonCode ReasonCode { get; set; }

    /// <summary>
    /// The Connected System the hop is attributed to, where one applies.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// That Connected System's name.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// The Synchronisation Rule the edge names, where it names one.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// That Synchronisation Rule's name, as recorded.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The members' object type, singular.
    /// </summary>
    public string? ObjectTypeName { get; set; }

    /// <summary>
    /// The members' object type, plural, where the type curates one.
    /// </summary>
    public string? ObjectTypePluralName { get; set; }

    /// <summary>
    /// The reference attribute a removal acted through, where the edge names one.
    /// </summary>
    public string? AttributeName { get; set; }

    /// <summary>
    /// What the members' execution items did to their objects.
    /// </summary>
    public ObjectChangeType? ChangeType { get; set; }

    /// <summary>
    /// The members' shared outcome summary.
    /// </summary>
    public string? OutcomeSummary { get; set; }

    /// <summary>
    /// The live status of outstanding Pending Exports, for that hop.
    /// </summary>
    public PendingExportStatus? PendingExportStatus { get; set; }

    /// <summary>
    /// The exact number of members.
    /// </summary>
    public int MemberCount { get; set; }

    /// <summary>
    /// When the earliest member happened, in UTC.
    /// </summary>
    public DateTime FirstOccurred { get; set; }

    /// <summary>
    /// When the latest member happened, in UTC.
    /// </summary>
    public DateTime LastOccurred { get; set; }

    /// <summary>
    /// The soonest due time of the members' outstanding work, in UTC.
    /// </summary>
    public DateTime? DueAt { get; set; }

    /// <summary>
    /// The first members, earliest first and then by name.
    /// </summary>
    public List<CausalConsequenceMemberDto> Members { get; set; } = [];

    /// <summary>
    /// Where this cohort's branch ends, for a cohort that ends one (Confirmed, WaitingForExport, WaitingForRetry,
    /// NeedsAttention, WaitingForConfirmingImport or DeletionScheduled). Null where the members carry on into
    /// <see cref="Consequences"/>.
    /// </summary>
    public CausalConsequenceEnding? Ending { get; set; }

    /// <summary>
    /// Members that led nowhere further, counted by reason.
    /// </summary>
    public List<CausalConsequenceEndingCountDto> Endings { get; set; } = [];

    /// <summary>
    /// What the members went on to cause.
    /// </summary>
    public List<CausalConsequenceCohortDto> Consequences { get; set; } = [];

    /// <summary>
    /// True when some of what the members went on to cause is not shown, because the walk reached its depth bound or the
    /// number of objects it carries to the next level.
    /// </summary>
    public bool IsFurtherThanShown { get; set; }

    /// <summary>
    /// Maps one cohort of the downward walk.
    /// </summary>
    public static CausalConsequenceCohortDto FromModel(CausalConsequenceCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        return new CausalConsequenceCohortDto
        {
            CohortKey = cohort.CohortKey,
            HopKind = cohort.HopKind,
            EdgeType = cohort.EdgeType,
            ReasonCode = cohort.ReasonCode,
            ConnectedSystemId = cohort.ConnectedSystemId,
            ConnectedSystemName = cohort.ConnectedSystemName,
            SyncRuleId = cohort.SyncRuleId,
            SyncRuleName = cohort.SyncRuleName,
            ObjectTypeName = cohort.ObjectTypeName,
            ObjectTypePluralName = cohort.ObjectTypePluralName,
            AttributeName = cohort.AttributeName,
            ChangeType = cohort.ChangeType,
            OutcomeSummary = cohort.OutcomeSummary,
            PendingExportStatus = cohort.PendingExportStatus,
            MemberCount = cohort.MemberCount,
            FirstOccurred = cohort.FirstOccurred,
            LastOccurred = cohort.LastOccurred,
            DueAt = cohort.DueAt,
            Members = cohort.Members.Select(CausalConsequenceMemberDto.FromModel).ToList(),
            Ending = cohort.Ending,
            Endings = cohort.Endings.Select(CausalConsequenceEndingCountDto.FromModel).ToList(),
            Consequences = cohort.Consequences.Select(FromModel).ToList(),
            IsFurtherThanShown = cohort.IsFurtherThanShown
        };
    }
}
