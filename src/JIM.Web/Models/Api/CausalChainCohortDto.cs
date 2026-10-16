// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;

namespace JIM.Web.Models.Api;

/// <summary>
/// Causes sharing everything but the object they happened to: one recorded edge type, reason, Connected System,
/// Synchronisation Rule, object type and attribute, or one derived hop (the import that fed a synchronisation, or the
/// Metaverse Object's creation).
/// </summary>
public class CausalChainCohortDto
{
    /// <summary>
    /// The recorded edge, or null for a derived hop (see <see cref="SourceImportChangeType"/> and
    /// <see cref="MetaverseChangeType"/>).
    /// </summary>
    public CausalEdgeType? EdgeType { get; set; }

    /// <summary>
    /// Why the cause produced its effect; NotSet where no reason was recorded.
    /// </summary>
    public CausalReasonCode ReasonCode { get; set; }

    /// <summary>
    /// For the derived hop to the import that fed a synchronisation: what that import did (Added, Updated or Deleted).
    /// </summary>
    public ObjectChangeType? SourceImportChangeType { get; set; }

    /// <summary>
    /// For the derived hop to the Metaverse Object's creation: Projected, Joined or Created.
    /// </summary>
    public ObjectChangeType? MetaverseChangeType { get; set; }

    /// <summary>
    /// The Connected System the cause is attributed to, where one applies.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// That Connected System's name, as recorded.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// The Synchronisation Rule responsible, where one applies.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// That Synchronisation Rule's name, as recorded.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// The causes' object type, singular.
    /// </summary>
    public string? ObjectTypeName { get; set; }

    /// <summary>
    /// The causes' object type, plural.
    /// </summary>
    public string? ObjectTypePluralName { get; set; }

    /// <summary>
    /// The reference attribute on the effect the causes acted through, where the seam names one.
    /// </summary>
    public string? AttributeName { get; set; }

    /// <summary>
    /// The outcome on the effect item that these causes explain, where one is recorded.
    /// </summary>
    public Guid? EffectSyncOutcomeId { get; set; }

    /// <summary>
    /// How many causes the cohort holds.
    /// </summary>
    public int MemberCount { get; set; }

    /// <summary>
    /// The causes, each with its own causes.
    /// </summary>
    public List<CausalChainMemberDto> Members { get; set; } = [];

    /// <summary>
    /// Maps one cohort of the upward walk.
    /// </summary>
    public static CausalChainCohortDto FromModel(CausalChainCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        var derived = cohort.SourceImportChangeType.HasValue || cohort.MetaverseChangeType.HasValue;
        return new CausalChainCohortDto
        {
            EdgeType = derived ? null : cohort.EdgeType,
            ReasonCode = cohort.ReasonCode,
            SourceImportChangeType = cohort.SourceImportChangeType,
            MetaverseChangeType = cohort.MetaverseChangeType,
            ConnectedSystemId = cohort.ConnectedSystemId,
            ConnectedSystemName = cohort.ConnectedSystemName,
            SyncRuleId = cohort.SyncRuleId,
            SyncRuleName = cohort.SyncRuleName,
            ObjectTypeName = cohort.ObjectTypeName,
            ObjectTypePluralName = cohort.ObjectTypePluralName,
            AttributeName = cohort.AttributeName,
            EffectSyncOutcomeId = cohort.EffectSyncOutcomeId,
            MemberCount = cohort.MemberCount,
            Members = cohort.Members.Select(CausalChainMemberDto.FromModel).ToList()
        };
    }
}
