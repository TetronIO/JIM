// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Web.Models;

/// <summary>
/// An Attribute Flow as it stood when the editor's dialog opened it, held as a detached copy. The dialog edits the
/// mapping in place, so without this the mapping's earlier state is unrecoverable by the time the administrator
/// presses Update; with it, the editor can ask the Application layer which Metaverse-Derived Attribute Flows the edit
/// would leave with a missing input (#1750, FR 3), comparing the flow as opened with the flow as edited.
/// </summary>
public sealed class AttributeFlowEditBaseline
{
    private AttributeFlowEditBaseline(SyncRuleMapping asOpened)
    {
        AsOpened = asOpened;
    }

    /// <summary>
    /// A copy of the mapping as it stood when the dialog opened: its identity, whether it was enabled, its target, and
    /// a copy of each source. Attribute navigations are shared with the original, not copied; the editor replaces them
    /// rather than mutating them, so the copy keeps naming what the mapping named.
    /// </summary>
    public SyncRuleMapping AsOpened { get; }

    /// <summary>
    /// Captures <paramref name="mapping"/> as it stands now.
    /// </summary>
    public static AttributeFlowEditBaseline Capture(SyncRuleMapping mapping) => new(Snapshot(mapping));

    /// <summary>
    /// A detached copy of <paramref name="mapping"/> as it stands now, holding what the derived flow checks read. The
    /// dialog's live analysis sends one of these rather than the mapping itself: the analysis reads the mapping after
    /// awaiting the database, by which time the administrator may have typed again on the same instance.
    /// </summary>
    public static SyncRuleMapping Snapshot(SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        var copy = new SyncRuleMapping
        {
            Id = mapping.Id,
            SyncRuleId = mapping.SyncRuleId,
            Enabled = mapping.Enabled,
            Priority = mapping.Priority,
            NullIsValue = mapping.NullIsValue,
            Generation = mapping.Generation,
            TargetMetaverseAttribute = mapping.TargetMetaverseAttribute,
            TargetMetaverseAttributeId = mapping.TargetMetaverseAttributeId,
            TargetConnectedSystemAttribute = mapping.TargetConnectedSystemAttribute,
            TargetConnectedSystemAttributeId = mapping.TargetConnectedSystemAttributeId
        };
        copy.Sources.AddRange(mapping.Sources.Select(source => new SyncRuleMappingSource
        {
            Order = source.Order,
            Expression = source.Expression,
            ConnectedSystemAttribute = source.ConnectedSystemAttribute,
            MetaverseAttribute = source.MetaverseAttribute,
            MissingInputBehaviour = source.MissingInputBehaviour
        }));

        return copy;
    }

    /// <summary>
    /// Whether the edit could take a contribution away from an attribute a derived flow reads: the flow disabled, its
    /// target changed, or what it reads changed (an Expression rewritten, or the source swapped). Anything else (a
    /// value-processing option, "Null is a value") leaves what it contributes where it was, so there is nothing to ask.
    /// </summary>
    /// <param name="edited">The mapping as the dialog now holds it.</param>
    public bool MayTakeAContributionAway(SyncRuleMapping edited)
    {
        ArgumentNullException.ThrowIfNull(edited);

        if (AsOpened.Enabled && !edited.Enabled)
            return true;

        if (AsOpened.ResolveTargetMetaverseAttributeId() != edited.ResolveTargetMetaverseAttributeId())
            return true;

        return !DescribeSources(AsOpened).SequenceEqual(DescribeSources(edited), StringComparer.Ordinal);
    }

    private static IEnumerable<string> DescribeSources(SyncRuleMapping mapping) => mapping.Sources
        .OrderBy(source => source.Order)
        .Select(source => $"{source.Expression}|{source.ConnectedSystemAttribute?.Id}|{source.MetaverseAttribute?.Id}");
}
