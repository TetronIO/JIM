// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// One row of a preview's summary: a transition, the population it applies to, and how many objects it covers.
/// </summary>
public class ConfigurationChangePreviewGroupResponse
{
    /// <summary>The group's identifier; pass it to the deltas endpoint to drill into this group alone.</summary>
    public Guid Id { get; set; }

    /// <summary>What would happen to the objects in this group.</summary>
    public ActivityRunProfileExecutionItemSyncOutcomeType TransitionType { get; set; }

    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>The object type's name as it was when the preview ran.</summary>
    public string? MetaverseObjectTypeName { get; set; }

    public int? ConnectedSystemId { get; set; }

    public string? ConnectedSystemName { get; set; }

    /// <summary>The attribute the transition concerns, where it concerns one.</summary>
    public string? AttributeName { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    /// <summary>
    /// What kind of edit this group describes, as a stable identifier: EmailDomainChanged, ContainerChanged,
    /// CasingChanged, PrefixAdded, PrefixRemoved, SuffixAdded or SuffixRemoved. Null where nothing recognised the
    /// change, and also where the group's objects did not all make the same kind of edit: a named pattern is a
    /// statement about every object in the group, never a majority of them.
    /// </summary>
    public string? PatternKey { get; set; }

    /// <summary>
    /// The exact number of objects in this group. Never an estimate, and never reduced by capping: what a group
    /// reports is what the change would do, whatever fraction of it can be drilled into.
    /// </summary>
    public int ObjectCount { get; set; }

    /// <summary>
    /// True when this group's drill-down shows a sample rather than every object. Surface this: a sample read as a
    /// complete list is how an administrator concludes a change is safe from the rows that happened to be kept.
    /// </summary>
    public bool DeltasSampled { get; set; }

    public static ConfigurationChangePreviewGroupResponse FromEntity(ConfigurationChangePreviewGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return new ConfigurationChangePreviewGroupResponse
        {
            Id = group.Id,
            TransitionType = group.TransitionType,
            MetaverseObjectTypeId = group.MetaverseObjectTypeId,
            MetaverseObjectTypeName = group.MetaverseObjectTypeName,
            ConnectedSystemId = group.ConnectedSystemId,
            ConnectedSystemName = group.ConnectedSystemName,
            AttributeName = group.AttributeName,
            OldValue = group.OldValue,
            NewValue = group.NewValue,
            PatternKey = group.PatternKey,
            ObjectCount = group.ObjectCount,
            DeltasSampled = group.DeltasSampled
        };
    }
}
