// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// Works out what an administrator is about to change, before they save it, so JIM can ask for consent to the
/// consequential parts and stay out of the way for everything else.
///
/// The baseline is the object's **latest captured configuration snapshot**, not a fresh read of the entity. Two
/// reasons, both load-bearing:
///
/// 1. It is the same baseline <see cref="ConfigurationChangeCaptureService"/> diffs against after the save, so the
///    acknowledgement the administrator consented to and the class written into the change history are computed from
///    one comparison and cannot disagree.
/// 2. It is immune to the change tracker. The edit surfaces mutate the entity they loaded and save it on the same
///    context, so re-reading the entity to get a "before" would return the mutated instance and the diff would come
///    back empty: a silent, total failure of the feature that no unit test using detached objects would catch.
///
/// Where no baseline exists (change tracking switched off, or an object that predates change capture) the answer is
/// "unknown", never "safe". See <see cref="ConfigurationChangePreflight.BaselineUnavailable"/>.
/// </summary>
public class ConfigurationChangePreflightService
{
    private JimApplication Application { get; }

    internal ConfigurationChangePreflightService(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Evaluates an unsaved Synchronisation Rule against its last captured state.
    /// </summary>
    /// <param name="proposed">The rule as edited but not yet saved. A rule with no id is a create, which has no prior
    /// state and therefore puts nothing existing at risk.</param>
    public async Task<ConfigurationChangePreflight> EvaluateSyncRuleAsync(SyncRule proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        if (proposed.Id == 0)
            return ConfigurationChangePreflight.None;

        return await EvaluateAsync(
            () => Application.Activities.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, proposed.Id),
            hashKey => Application.ConfigurationSnapshots.CreateSnapshot(proposed, hashKey));
    }

    /// <summary>
    /// Evaluates an unsaved Connected System against its last captured state. One entry point serves the details,
    /// settings, schema and partitions tabs, because all four edit and save the same entity; the snapshot covers every
    /// property any of them can reach, so each save path asks the same question and gets a consistent answer.
    /// </summary>
    /// <param name="proposed">The Connected System as edited but not yet saved.</param>
    public async Task<ConfigurationChangePreflight> EvaluateConnectedSystemAsync(ConnectedSystem proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        if (proposed.Id == 0)
            return ConfigurationChangePreflight.None;

        return await EvaluateAsync(
            () => Application.Activities.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ConnectedSystem, proposed.Id),
            hashKey => Application.ConfigurationSnapshots.CreateSnapshot(proposed, hashKey));
    }

    /// <summary>
    /// Evaluates an unsaved Metaverse Object Type against its last captured state. This is the one surface whose
    /// destructive properties (the deletion rule, its grace period and its trigger systems) take effect without a
    /// synchronisation run in between, which is why the consequence copy for them says so explicitly.
    /// </summary>
    /// <param name="proposed">The object type as edited but not yet saved.</param>
    public async Task<ConfigurationChangePreflight> EvaluateMetaverseObjectTypeAsync(MetaverseObjectType proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        if (proposed.Id == 0)
            return ConfigurationChangePreflight.None;

        return await EvaluateAsync(
            () => Application.Activities.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.MetaverseObjectType, proposed.Id),
            hashKey => Application.ConfigurationSnapshots.CreateSnapshot(proposed, hashKey));
    }

    /// <summary>
    /// Evaluates an unsaved Metaverse Attribute against its last captured state.
    /// </summary>
    /// <param name="proposed">The attribute as edited but not yet saved. The editor applies its changes through
    /// several separate calls (schema, rename, rendering hint, Standard Mappings), each of which captures its own
    /// change version; the administrator performed one save, so they are asked to acknowledge it once, over the union
    /// of what they changed.</param>
    public async Task<ConfigurationChangePreflight> EvaluateMetaverseAttributeAsync(MetaverseAttribute proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        if (proposed.Id == 0)
            return ConfigurationChangePreflight.None;

        return await EvaluateAsync(
            () => Application.Activities.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.MetaverseAttribute, proposed.Id),
            hashKey => Application.ConfigurationSnapshots.CreateSnapshot(proposed, hashKey));
    }

    /// <summary>
    /// Evaluates an unsaved Service Setting against its last captured state. Service Settings are string-keyed rather
    /// than id-keyed, so the baseline is looked up by <see cref="ServiceSetting.Key"/>.
    /// </summary>
    /// <param name="proposed">The setting carrying the value about to be stored: for an edit, the new value; for a
    /// revert, a null value (the setting's own definition of "use the default"). Encrypted settings are captured as a
    /// keyed hash and are classified Cosmetic to a one, so passing a plaintext value here cannot change the
    /// answer.</param>
    public async Task<ConfigurationChangePreflight> EvaluateServiceSettingAsync(ServiceSetting proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentException.ThrowIfNullOrEmpty(proposed.Key);

        return await EvaluateAsync(
            () => Application.Activities.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ServiceSetting, proposed.Key),
            hashKey => Application.ConfigurationSnapshots.CreateSnapshot(proposed, hashKey));
    }

    /// <summary>
    /// The shared body of every entry point: fetch the baseline, build the proposed snapshot, compare. Kept in one
    /// place so a surface cannot acquire its own subtly different notion of what "changed" means.
    /// </summary>
    private async Task<ConfigurationChangePreflight> EvaluateAsync(
        Func<Task<string?>> getBaselineSnapshotAsync,
        Func<byte[], ConfigurationSnapshot> buildProposedSnapshot)
    {
        // Only consults the store when change tracking is on. With tracking off no new baselines are written, so
        // whatever is stored has gone stale: diffing against it would present changes made days ago as though they
        // were part of this save. "Unknown" is the honest answer, and it is the same answer the changed-since
        // indicator gives.
        if (!await Application.ServiceSettings.GetConfigurationChangeTrackingEnabledAsync())
            return ConfigurationChangePreflight.Unknown;

        var baselineJson = await getBaselineSnapshotAsync();
        if (baselineJson == null)
            return ConfigurationChangePreflight.Unknown;

        var hashKey = await Application.ServiceSettings.GetOrCreateConfigurationChangeHashKeyAsync();
        return Evaluate(ConfigurationSnapshotService.Deserialise(baselineJson), buildProposedSnapshot(hashKey));
    }

    /// <summary>
    /// The shared comparison, kept separate from the per-surface entry points so every surface produces an identically
    /// shaped answer from an identically computed diff.
    /// </summary>
    private ConfigurationChangePreflight Evaluate(ConfigurationSnapshot? baseline, ConfigurationSnapshot proposed)
    {
        if (baseline == null)
            return ConfigurationChangePreflight.Unknown;

        var diff = Application.ConfigurationDiffs.Diff(baseline, proposed);
        if (!diff.HasChanges)
            return ConfigurationChangePreflight.None;

        // Started from the root's children rather than the root itself: the root's label names the object, not a
        // section within it, so it belongs to no property's path.
        var items = new List<ConfigurationChangePreflightItem>();
        foreach (var child in diff.Root.Children ?? [])
            CollectItems(child, [], proposed.ObjectType, proposed.ObjectKey, items);

        return new ConfigurationChangePreflight
        {
            // The classifier's verdict over the whole diff, never the maximum over the items below. The two are not
            // the same reduction: the items are the changed *scalars*, whereas the classifier also weighs the
            // collection and object nodes above them. Where a collection's own key outranks every scalar inside its
            // items (`partitions` is Class B; a container's name, external id and hidden flag are all Class C),
            // taking the maximum over the items answers Cosmetic to a change the capture then records as
            // synchronisation-affecting: the administrator saves in silence and only learns of it from the change
            // history. Deriving both from ConfigurationChangeClassifier.Classify makes the promise this class already
            // documents (the acknowledgement and the recorded class cannot disagree) true by construction rather than
            // by coincidence.
            HighestClass = ConfigurationChangeClassifier.Classify(diff, proposed.ObjectKey),
            // Most consequential first, then alphabetically so the list is stable between renders of the same change.
            Items = items
                .OrderByDescending(i => i.Class)
                .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    /// <summary>
    /// Walks the diff tree collecting every changed scalar, carrying the ancestor labels down so a nested property
    /// can be named in terms an administrator recognises ("Attribute Flow &gt; Source &gt; Order", not "Order").
    /// </summary>
    private static void CollectItems(ConfigurationDiffNode node, IReadOnlyList<string> ancestorLabels,
        string objectType, string? objectKey, List<ConfigurationChangePreflightItem> items)
    {
        if (node.NodeType == ConfigurationSnapshotNodeType.Scalar)
        {
            if (node.ChangeType != ConfigurationDiffChangeType.Unchanged)
                items.Add(BuildItem(node, ancestorLabels, objectType, objectKey));
            return;
        }

        // An object or collection node contributes its own label to the path of everything beneath it. Descent is
        // unconditional: a container is only as changed as its leaves, and it is the leaves that get classified.
        var childAncestors = new List<string>(ancestorLabels) { DescribeNode(node) };
        foreach (var child in node.Children ?? [])
            CollectItems(child, childAncestors, objectType, objectKey, items);
    }

    /// <summary>
    /// The label to use for an object or collection node within a property's path. Collection items are labelled by
    /// their kind ("Object Type", "Partition", "Run Profile"), which identifies nothing once the tree is flattened
    /// into a list: "Object Types &gt; Object Type &gt; Selected" is the same sentence for all twelve of them. Where the
    /// node carries a name, the name replaces the kind, which the parent collection's own label already supplies.
    /// </summary>
    private static string DescribeNode(ConfigurationDiffNode node)
    {
        var label = node.Label ?? node.Key;
        if (node.NodeType != ConfigurationSnapshotNodeType.Object)
            return label;

        // The new name where there is one, else the old: a rename in the same save should still identify the item by
        // what the administrator is looking at, and a removed item only has an old name.
        var nameNode = node.Children?.FirstOrDefault(c => c.Key == "name" && c.NodeType == ConfigurationSnapshotNodeType.Scalar);
        var name = nameNode?.NewValue ?? nameNode?.OldValue;
        return string.IsNullOrEmpty(name) ? label : name;
    }

    private static ConfigurationChangePreflightItem BuildItem(ConfigurationDiffNode node,
        IReadOnlyList<string> ancestorLabels, string objectType, string? objectKey)
    {
        var name = node.Label ?? node.Key;
        var label = ancestorLabels.Count == 0 ? name : string.Join(" > ", ancestorLabels.Append(name));

        return new ConfigurationChangePreflightItem
        {
            Key = node.Key,
            Label = label,
            Class = ConfigurationChangeClassifier.ClassifyKey(objectType, node.Key, objectKey),
            OldDisplayValue = ForDisplay(node.OldDisplayValue, node.OldValue),
            NewDisplayValue = ForDisplay(node.NewDisplayValue, node.NewValue),
            Consequence = ConfigurationChangeConsequences.For(objectType, node.Key, node.OldValue, node.NewValue)
        };
    }

    // Booleans are snapshotted as raw "true"/"false" with no display form, which reads like a debug dump next to
    // every other value's friendly rendering. Secrets carry neither, and are reported as changed without a value.
    private static string? ForDisplay(string? displayValue, string? rawValue) => displayValue ?? rawValue switch
    {
        "true" => "Yes",
        "false" => "No",
        _ => rawValue
    };
}
