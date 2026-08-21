// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// A group of causes that share an attribution tuple, and therefore say the same thing about why an effect
/// happened: "10 objects were removed because their authoritative source disconnected" (#1223).
/// </summary>
/// <remarks>
/// Grouping is per <b>effect outcome</b>, not merely per item. An item carrying several outcomes has several
/// independent stories, and merging their causes into one cohort would attribute a removal on one outcome to a
/// cause belonging to another. This is the resolution of the PRD's edge-granularity question, and it is why
/// <see cref="EffectSyncOutcomeId"/> is part of the key rather than incidental detail.
///
/// The reason is a <see cref="CausalReasonCode"/> rather than the sentence shown on screen. Grouping on prose
/// would be redundant with the Connected System name the tuple already carries, and would change silently
/// whenever the wording changed; the sentence is derived at render time from the code plus these snapshots.
/// </remarks>
public class CausalChainCohort
{
    /// <summary>
    /// The outcome on the effect that this cohort explains, where the causes named one. Null where the causes
    /// attach to the item as a whole.
    /// </summary>
    public Guid? EffectSyncOutcomeId { get; init; }

    /// <summary>
    /// Which cascade seam these causes sit on.
    /// </summary>
    public CausalEdgeType EdgeType { get; init; }

    /// <summary>
    /// Why the causes produced the effect, as a code. The displayed sentence is derived from it.
    /// </summary>
    public CausalReasonCode ReasonCode { get; init; }

    /// <summary>
    /// The Connected System the causes occurred on, where one applies.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    /// <summary>
    /// The Connected System's name as it was at the time, which is what the chain must display: the system may
    /// since have been renamed or deleted.
    /// </summary>
    public string? ConnectedSystemName { get; init; }

    /// <summary>
    /// Whether this cohort names a Connected System that can be both linked to and read aloud.
    /// </summary>
    /// <remarks>
    /// Exposed rather than left to each caller because two of them have to agree exactly: the chip renders on
    /// this condition, and the reason phrase beside it is worded as the predicate of a sentence the chip is the
    /// subject of. Were they to disagree, the row would read as a verb with nothing in front of it.
    /// </remarks>
    public bool HasConnectedSystem => ConnectedSystemId.HasValue && !string.IsNullOrWhiteSpace(ConnectedSystemName);

    /// <summary>
    /// Set where this cohort is a derived source-import hop rather than a recorded edge: what the import did
    /// to the record (added, changed or deleted it). The walk synthesises these from the record's own timeline
    /// (the PRD's free per-object join), so they carry no <see cref="EdgeType"/> of their own; wording and
    /// rendering key on this before consulting the edge type.
    /// </summary>
    public Enums.ObjectChangeType? SourceImportChangeType { get; init; }

    /// <summary>
    /// The Synchronisation Rule responsible, where one applies.
    /// </summary>
    public int? SyncRuleId { get; init; }

    /// <summary>
    /// The Synchronisation Rule's name as it was at the time.
    /// </summary>
    public string? SyncRuleName { get; init; }

    /// <summary>
    /// The Metaverse Object Type of this cohort's causes, singular and plural, as curated on the type.
    /// </summary>
    /// <remarks>
    /// The type is part of the grouping key, so a cohort's causes are all of one type and this noun is always
    /// correct for them. Without that a cohort could mix a User and a Contractor, and no single noun would be
    /// right; in practice a deletion cascade is type-homogeneous, so it rarely forks on this element.
    ///
    /// Both forms are carried because the caller picks by <see cref="MemberCount"/>: "1 User" against
    /// "10 Users". Never derived from one another by rule; see <see cref="CausalEdge.CauseObjectTypeName"/>.
    /// </remarks>
    public string? ObjectTypeName { get; init; }

    /// <inheritdoc cref="ObjectTypeName"/>
    public string? ObjectTypePluralName { get; init; }

    /// <summary>
    /// The reference attribute the causes acted through, where there was one: the relationship noun the
    /// chain reads back ("removed from Project Diamond's Static Members"). Null where the effect was not a
    /// reference removal.
    /// </summary>
    public string? AttributeName { get; init; }

    /// <summary>
    /// The noun to use for this cohort, singular or plural according to how many causes it speaks for.
    /// </summary>
    /// <remarks>
    /// Falls back to the singular where no plural was curated, and to null where neither was recorded, so a
    /// caller renders slightly stiff English rather than a guessed plural or an empty noun.
    /// </remarks>
    public string? ObjectNoun => MemberCount == 1
        ? ObjectTypeName
        : ObjectTypePluralName ?? ObjectTypeName;

    /// <summary>
    /// The individual causes in this cohort. Always populated, so an expanded cohort can name its members
    /// rather than only counting them.
    /// </summary>
    public List<CausalChainMember> Members { get; init; } = [];

    /// <summary>
    /// How many causes this cohort speaks for. The number an administrator reads first ("4 Groups"), so it is
    /// exposed rather than left for the caller to compute and risk computing differently.
    /// </summary>
    public int MemberCount => Members.Count;
}
