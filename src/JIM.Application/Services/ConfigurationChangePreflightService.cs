// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Logic;

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

        var baselineJson = await GetBaselineAsync(() =>
            Application.Activities.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, proposed.Id));
        if (baselineJson == null)
            return ConfigurationChangePreflight.Unknown;

        var hashKey = await Application.ServiceSettings.GetOrCreateConfigurationChangeHashKeyAsync();
        return Evaluate(ConfigurationSnapshotService.Deserialise(baselineJson),
            Application.ConfigurationSnapshots.CreateSnapshot(proposed, hashKey));
    }

    // Only consults the store when change tracking is on. With tracking off no new baselines are written, so whatever
    // is stored has gone stale: diffing against it would present changes made days ago as though they were part of
    // this save. "Unknown" is the honest answer, and it is the same answer the changed-since indicator gives.
    private async Task<string?> GetBaselineAsync(Func<Task<string?>> getSnapshotAsync) =>
        await Application.ServiceSettings.GetConfigurationChangeTrackingEnabledAsync()
            ? await getSnapshotAsync()
            : null;

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

        if (items.Count == 0)
            return ConfigurationChangePreflight.None;

        return new ConfigurationChangePreflight
        {
            HighestClass = items.Max(i => i.Class),
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
        var childAncestors = new List<string>(ancestorLabels) { node.Label ?? node.Key };
        foreach (var child in node.Children ?? [])
            CollectItems(child, childAncestors, objectType, objectKey, items);
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
