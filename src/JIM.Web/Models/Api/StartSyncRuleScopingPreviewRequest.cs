// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Search;

namespace JIM.Web.Models.Api;

/// <summary>
/// The Scoping Criteria to preview (#1436): which objects the Synchronisation Rule would manage under the proposed
/// criteria, and what the objects moving in or out of scope would cost, before anything is saved.
/// </summary>
public class StartSyncRuleScopingPreviewRequest
{
    /// <summary>
    /// The proposed top-level criteria groups, combined with OR exactly as a synchronisation combines them.
    ///
    /// Omitted or null previews the rule's stored criteria, matching the update endpoints' semantics: a caller
    /// proposing nothing is proposing no change, and the preview says so rather than inventing one. An explicitly
    /// EMPTY array is a real proposal and a very different one: it removes every criterion, handing the rule every
    /// object of its type.
    /// </summary>
    public List<SyncRuleScopingCriteriaGroupRequest>? CriteriaGroups { get; set; }

    /// <summary>
    /// Whether drill-down rows are kept in full or capped per summary group. Counts are exact either way; capping
    /// bounds only what is retained for drill-down. Defaults to Capped, the recommended choice for large
    /// populations.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;

    /// <summary>
    /// The proposal these criteria describe, or the rule's stored scope where the caller proposed none.
    /// </summary>
    /// <param name="syncRule">The rule being previewed, read for its stored scope.</param>
    public SyncRuleScopingProposal ToProposal(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);

        return CriteriaGroups == null
            ? SyncRuleScopingProposal.FromCurrentScope(syncRule)
            : new SyncRuleScopingProposal([.. CriteriaGroups.Select(group => group.ToProposal())]);
    }
}

/// <summary>
/// One proposed criteria group: how its members combine, the criteria it evaluates, and any groups nested in it.
/// </summary>
public class SyncRuleScopingCriteriaGroupRequest
{
    /// <summary>
    /// Whether the group's members are combined with All (every member must match) or Any (one is enough).
    /// </summary>
    public SearchGroupType Type { get; set; } = SearchGroupType.All;

    /// <summary>
    /// The criteria this group evaluates directly. A group with no criteria and no child groups constrains
    /// nothing, and so matches every object.
    /// </summary>
    public List<SyncRuleScopingCriterionRequest> Criteria { get; set; } = [];

    /// <summary>
    /// Groups nested inside this one, evaluated as further members of it.
    /// </summary>
    public List<SyncRuleScopingCriteriaGroupRequest> ChildGroups { get; set; } = [];

    internal SyncRuleScopingCriteriaGroupProposal ToProposal() =>
        new(Type,
            [.. Criteria.Select(criterion => criterion.ToProposal())],
            [.. ChildGroups.Select(group => group.ToProposal())]);
}

/// <summary>
/// One proposed criterion: the attribute it reads, how it compares, and what it compares against.
/// </summary>
/// <remarks>
/// Exactly one of the two attribute ids is set, and which one depends on the rule's direction: an import rule
/// evaluates its scope against Connected System attributes, an export rule against Metaverse Attributes. A
/// criterion carrying the wrong side's attribute can never match, so the preview reports it as a blocking finding
/// rather than evaluating around it and answering for a wider scope than the caller described.
/// </remarks>
public class SyncRuleScopingCriterionRequest
{
    /// <summary>The Metaverse Attribute read by an export rule's criterion.</summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>The Connected System attribute read by an import rule's criterion.</summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>The comparison applied between the attribute's value and this criterion's value.</summary>
    public SearchComparisonType ComparisonType { get; set; } = SearchComparisonType.Equals;

    /// <summary>The value compared against, for a Text attribute.</summary>
    public string? StringValue { get; set; }

    /// <summary>The value compared against, for a Number attribute.</summary>
    public int? IntValue { get; set; }

    /// <summary>The value compared against, for a Long Number attribute.</summary>
    public long? LongValue { get; set; }

    /// <summary>The value compared against, for a Decimal attribute.</summary>
    public decimal? DecimalValue { get; set; }

    /// <summary>The fixed date compared against, when <see cref="ValueMode"/> is Absolute.</summary>
    public DateTime? DateTimeValue { get; set; }

    /// <summary>The value compared against, for a Boolean attribute.</summary>
    public bool? BoolValue { get; set; }

    /// <summary>The value compared against, for a Guid attribute.</summary>
    public Guid? GuidValue { get; set; }

    /// <summary>Whether a text comparison respects case. Defaults to true, as the editor does.</summary>
    public bool CaseSensitive { get; set; } = true;

    /// <summary>
    /// Whether a date criterion compares against a fixed date or one resolved relative to the moment of evaluation.
    /// </summary>
    public DateCriteriaValueMode ValueMode { get; set; } = DateCriteriaValueMode.Absolute;

    /// <summary>The size of the offset from now, when <see cref="ValueMode"/> is Relative.</summary>
    public int? RelativeCount { get; set; }

    /// <summary>The unit of the offset from now, when <see cref="ValueMode"/> is Relative.</summary>
    public RelativeDateUnit? RelativeUnit { get; set; }

    /// <summary>The direction of the offset from now, when <see cref="ValueMode"/> is Relative.</summary>
    public RelativeDateDirection? RelativeDirection { get; set; }

    internal SyncRuleScopingCriterionProposal ToProposal() =>
        new(MetaverseAttributeId,
            ConnectedSystemAttributeId,
            ComparisonType,
            StringValue,
            IntValue,
            LongValue,
            DecimalValue,
            DateTimeValue,
            BoolValue,
            GuidValue,
            CaseSensitive,
            ValueMode,
            RelativeCount,
            RelativeUnit,
            RelativeDirection);
}
