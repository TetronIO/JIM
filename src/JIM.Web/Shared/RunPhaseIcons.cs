// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using MudBlazor;

namespace JIM.Web.Shared;

/// <summary>
/// The icon each step of a Run Profile execution is drawn with (#454). A step that looks like what
/// it does is recognisable before its label is read, which is the difference between scanning a
/// stepper and reading it.
/// </summary>
/// <remarks>
/// Icons live here rather than in the phase catalogue because they are a presentation choice, and
/// the catalogue (JIM.Models) has no business knowing about MudBlazor. A Connector's own steps are
/// keyed by the Connector's vocabulary, which JIM cannot map, so they share one neutral icon.
/// </remarks>
public static class RunPhaseIcons
{
    private static readonly Dictionary<string, string> IconsByPhaseKey = new(StringComparer.Ordinal)
    {
        // Import
        [RunPhaseKeys.ImportConnect] = Icons.Material.Filled.Lan,
        [RunPhaseKeys.ImportFetch] = Icons.Material.Filled.CloudDownload,
        [RunPhaseKeys.ImportDeletions] = Icons.Material.Filled.DeleteSweep,
        [RunPhaseKeys.ImportResolveReferences] = Icons.Material.Filled.Link,
        [RunPhaseKeys.ImportSave] = Icons.Material.Filled.Save,
        [RunPhaseKeys.ImportReconcile] = Icons.Material.Filled.FactCheck,
        [RunPhaseKeys.ImportRecordResults] = Icons.Material.Filled.Assignment,

        // Synchronisation
        [RunPhaseKeys.SyncPrepare] = Icons.Material.Filled.Rule,
        [RunPhaseKeys.SyncProcessObjects] = Icons.Material.Filled.Sync,
        [RunPhaseKeys.SyncResolveCrossPageReferences] = Icons.Material.Filled.Link,
        [RunPhaseKeys.SyncReviewExportScope] = Icons.Material.Filled.Schedule,

        // Export
        [RunPhaseKeys.ExportPrepare] = Icons.Material.Filled.Rule,
        [RunPhaseKeys.ExportExecute] = Icons.Material.Filled.CloudUpload,
        [RunPhaseKeys.ExportResolveReferences] = Icons.Material.Filled.Link,
        [RunPhaseKeys.ExportSelectNewContainers] = Icons.Material.Filled.CreateNewFolder,
        [RunPhaseKeys.ExportDeliverInitialPasswords] = Icons.Material.Filled.Key
    };

    /// <summary>
    /// The icon for a step, by its phase key. Connector steps and anything unrecognised fall back
    /// to the icon the portal uses elsewhere for work in flight.
    /// </summary>
    public static string ForPhase(string phaseKey) =>
        IconsByPhaseKey.TryGetValue(phaseKey, out var icon) ? icon : Icons.Material.Filled.Memory;
}
