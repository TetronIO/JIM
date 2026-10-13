// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// What export staging produces when a Metaverse Object's only email, held in a multi-valued Metaverse Attribute,
/// changes from <see cref="OldValue"/> to <see cref="NewValue"/>, or is removed with nothing in its place, and flows to
/// a single-valued attribute of a Connected System (#2182). Shared by the Connector fixtures so each one is handed
/// exactly what the engine stages, rather than a shape a test author assumed it stages.
/// </summary>
internal static class StagedEmailChange
{
    internal const string OldValue = "a@example.com";

    internal const string NewValue = "b@example.com";

    /// <summary>
    /// The attribute changes export staging produces for the change to <see cref="NewValue"/>, written to
    /// <paramref name="target"/>.
    /// </summary>
    internal static List<PendingExportAttributeValueChange> StageFor(ConnectedSystemObjectTypeAttribute target) =>
        Stage(target, replacedWith: NewValue);

    /// <summary>
    /// The attribute changes export staging produces when <see cref="OldValue"/> is removed and nothing replaces it,
    /// written to <paramref name="target"/>.
    /// </summary>
    internal static List<PendingExportAttributeValueChange> StageClearFor(ConnectedSystemObjectTypeAttribute target) =>
        Stage(target, replacedWith: null);

    private static List<PendingExportAttributeValueChange> Stage(ConnectedSystemObjectTypeAttribute target, string? replacedWith)
    {
        var source = new MetaverseAttribute
        {
            Id = 501,
            Name = "Emails",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued
        };

        // Additions first, then removals, as the synchronisation processor passes them; by the time export staging
        // runs, the Metaverse Object already holds the result.
        var removed = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = source, AttributeId = source.Id, StringValue = OldValue };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        List<MetaverseObjectAttributeValue> changed = [];
        if (replacedWith != null)
        {
            var added = new MetaverseObjectAttributeValue { Attribute = source, AttributeId = source.Id, StringValue = replacedWith };
            mvo.AttributeValues.Add(added);
            changed.Add(added);
        }

        changed.Add(removed);

        var exportRule = new SyncRule { Id = 1, Name = "Export", Direction = SyncRuleDirection.Export };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 1,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Id = 1, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });

        return new SyncEngine().ComputeAttributeValueChanges(mvo, exportRule, changed, PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);
    }
}
