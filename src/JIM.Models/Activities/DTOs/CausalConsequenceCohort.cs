// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;
using JIM.Models.Enums;
using JIM.Models.Transactional;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// A group of consequences sharing everything but the object they happened to (#1811): one card reading "4,218 Groups
/// lost Tina Adams from Static Members" rather than 4,218 cards. Mirrors Phase 1's <see cref="CausalChainCohort"/>, read
/// in the other direction.
/// </summary>
public class CausalConsequenceCohort
{
    /// <summary>
    /// Identifies the cohort for member paging: its path from the item the walk started from, encoded. Opaque, so the
    /// server re-derives the cohort from it rather than holding state between requests.
    /// </summary>
    public required string CohortKey { get; init; }

    /// <summary>
    /// How the consequences were reached from the cohort above.
    /// </summary>
    public CausalConsequenceHopKind HopKind { get; init; }

    /// <summary>
    /// The recorded edge, for an edge or confirmation hop.
    /// </summary>
    public CausalEdgeType? EdgeType { get; init; }

    /// <summary>
    /// Why the edge's cause produced these; NotSet for a derived hop.
    /// </summary>
    public CausalReasonCode ReasonCode { get; init; }

    /// <summary>
    /// The Connected System the hop is attributed to, where one applies.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    /// <summary>
    /// That Connected System's name.
    /// </summary>
    public string? ConnectedSystemName { get; init; }

    /// <summary>
    /// The Synchronisation Rule the edge names, where it names one.
    /// </summary>
    public int? SyncRuleId { get; init; }

    /// <summary>
    /// That Synchronisation Rule's name, as recorded.
    /// </summary>
    public string? SyncRuleName { get; init; }

    /// <summary>
    /// The members' object type, singular.
    /// </summary>
    public string? ObjectTypeName { get; init; }

    /// <summary>
    /// The members' object type, plural, where the type curates one.
    /// </summary>
    public string? ObjectTypePluralName { get; init; }

    /// <summary>
    /// The reference attribute a removal acted through, where the edge names one.
    /// </summary>
    public string? AttributeName { get; init; }

    /// <summary>
    /// What the members' items did to their objects.
    /// </summary>
    public ObjectChangeType? ChangeType { get; init; }

    /// <summary>
    /// The members' shared outcome summary.
    /// </summary>
    public string? OutcomeSummary { get; init; }

    /// <summary>
    /// The live status of outstanding Pending Exports, for that hop.
    /// </summary>
    public PendingExportStatus? PendingExportStatus { get; init; }

    /// <summary>
    /// The exact number of members.
    /// </summary>
    public int MemberCount { get; init; }

    /// <summary>
    /// When the earliest member happened, in UTC.
    /// </summary>
    public DateTime FirstOccurred { get; init; }

    /// <summary>
    /// When the latest member happened, in UTC.
    /// </summary>
    public DateTime LastOccurred { get; init; }

    /// <summary>
    /// The soonest due time of the members' outstanding work, in UTC: a retry, or a scheduled deletion.
    /// </summary>
    public DateTime? DueAt { get; init; }

    /// <summary>
    /// The first page of members; page through the rest with <see cref="CohortKey"/>.
    /// </summary>
    public List<CausalConsequenceMember> Members { get; init; } = [];

    /// <summary>
    /// One of the members' items in full, with its sync outcomes and its Activity: the first member that is an item.
    /// Every member shares the cohort's outcome summary, so what one recorded is what each did to its own object, and
    /// it is what the portal places the cohort's cards from. Null for a cohort of Pending Exports or scheduled
    /// deletions, which are not items, and where the item has since been purged.
    /// </summary>
    /// <remarks>
    /// Not part of the REST contract, which carries the outcome summary instead: an item graph has no place in a wire
    /// shape, and ignoring it here means a serialiser can never walk into its Activity.
    /// </remarks>
    [JsonIgnore]
    public ActivityRunProfileExecutionItem? Representative { get; set; }

    /// <summary>
    /// Where this cohort's branch ends, for a cohort that ends one: a confirmation, or outstanding work. Null where
    /// the members carry on into <see cref="Consequences"/>.
    /// </summary>
    public CausalConsequenceEnding? Ending { get; init; }

    /// <summary>
    /// Members that led nowhere further, counted by reason.
    /// </summary>
    public List<CausalConsequenceEndingCount> Endings { get; init; } = [];

    /// <summary>
    /// What the members went on to cause.
    /// </summary>
    public List<CausalConsequenceCohort> Consequences { get; init; } = [];

    /// <summary>
    /// True when some of what the members went on to cause is not shown, because the walk reached its depth bound or
    /// the number of objects it carries to the next level.
    /// </summary>
    public bool IsFurtherThanShown { get; set; }
}
