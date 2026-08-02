// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// One row of a preview's summary: a transition, the population it applies to, and how many objects it covers.
/// Groups are the landing view, because a preview affecting 40,000 objects is unreadable as a list and perfectly
/// readable as "38,900 would have their email domain changed; 1,100 would fall out of scope".
///
/// **Counts here are always exact**, whether or not the delta rows beneath them were capped. Capping decides what
/// can be drilled into, never what is counted; a summary that under-reported because storage was capped would be
/// worse than no summary, because it would look authoritative.
/// </summary>
public class ConfigurationChangePreviewGroup
{
    public Guid Id { get; set; }

    /// <summary>The preview this group belongs to; also the Activity that owns both.</summary>
    public Guid ActivityId { get; set; }

    public ConfigurationChangePreview Preview { get; set; } = null!;

    /// <summary>What would happen to the objects in this group.</summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType TransitionType { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Grouping dimensions. All optional: a group is as specific as the data supports, and a dimension the adapter
    // did not populate is simply absent from the description rather than rendered as an empty column.
    // -----------------------------------------------------------------------------------------------------------------

    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The object type's name as it was when the preview ran. Snapshotted rather than joined so the summary still
    /// reads correctly after the type is renamed, and still renders at all after it is deleted.
    /// </summary>
    public string? MetaverseObjectTypeName { get; set; }

    public int? ConnectedSystemId { get; set; }

    public string? ConnectedSystemName { get; set; }

    /// <summary>The attribute the transition concerns, where it concerns one.</summary>
    public string? AttributeName { get; set; }

    /// <summary>
    /// The old and new values, populated only where the group is a distinct value pair with low enough cardinality
    /// to be worth naming. High-cardinality pairs (every object getting a different value) collapse into the
    /// attribute-level group instead, because listing 40,000 one-object groups is the wall of text grouping exists
    /// to prevent.
    /// </summary>
    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    /// <summary>
    /// The pattern a detector recognised across this group's deltas ("email domain changed", "moved to a different
    /// OU"), or null when none did. The column exists from the start so the detector registry needs no migration;
    /// nothing populates it yet.
    /// </summary>
    public string? PatternKey { get; set; }

    /// <summary>The exact number of objects in this group. Never an estimate, never reduced by capping.</summary>
    public int ObjectCount { get; set; }

    /// <summary>
    /// True when this group's delta rows were capped, so its drill-down shows a sample rather than the whole group.
    /// The panel labels these; an unlabelled sample read as a complete list is how an administrator concludes a
    /// change is safe from the 1,000 rows that happened to be kept.
    /// </summary>
    public bool DeltasSampled { get; set; }

    public ICollection<ConfigurationChangePreviewDelta> Deltas { get; set; } = [];
}
