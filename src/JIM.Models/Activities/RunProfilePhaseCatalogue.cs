// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// The single declaration of the phases JIM moves through for each Run Profile type (#454). Read
/// once when a Run Profile execution starts, to seed the Activity's phases so an administrator can
/// see the whole journey (including the steps still to come) rather than only the current message.
/// </summary>
/// <remarks>
/// <para>
/// Adding or renaming a step is a change here plus the matching <see cref="RunPhaseKeys"/> constant
/// at the worker call site that enters it. Nothing else needs to know: the portal, the API and
/// PowerShell all read the phases recorded against the Activity.
/// </para>
/// <para>
/// Declared phases are an expectation, not a promise. A run legitimately skips phases (a Delta
/// Import does no deletion detection; a file-based import opens no connection), and the phase a run
/// never enters is recorded as skipped rather than left pending forever. Declare the phases a run
/// of that type <em>can</em> perform, in the order they would occur.
/// </para>
/// </remarks>
public static class RunProfilePhaseCatalogue
{
    private static readonly IReadOnlyList<RunProfilePhase> ImportPhases =
    [
        new(RunPhaseKeys.ImportConnect, "Connecting to Connected System"),
        new(RunPhaseKeys.ImportFetch, "Importing objects", HostsConnectorPhases: true),
        new(RunPhaseKeys.ImportDeletions, "Processing deletions"),
        new(RunPhaseKeys.ImportResolveReferences, "Resolving references"),
        new(RunPhaseKeys.ImportSave, "Saving changes"),
        new(RunPhaseKeys.ImportReconcile, "Reconciling Pending Exports"),
        new(RunPhaseKeys.ImportRecordResults, "Recording results")
    ];

    private static readonly IReadOnlyList<RunProfilePhase> SynchronisationPhases =
    [
        new(RunPhaseKeys.SyncPrepare, "Preparing"),
        new(RunPhaseKeys.SyncProcessObjects, "Processing Connected System Objects"),
        new(RunPhaseKeys.SyncResolveCrossPageReferences, "Resolving cross-page references")
    ];

    private static readonly IReadOnlyList<RunProfilePhase> ExportPhases =
    [
        new(RunPhaseKeys.ExportPrepare, "Preparing export"),
        new(RunPhaseKeys.ExportExecute, "Exporting", HostsConnectorPhases: true),
        new(RunPhaseKeys.ExportResolveReferences, "Resolving change history references")
    ];

    private static readonly IReadOnlyList<RunProfilePhase> NoPhases = [];

    /// <summary>
    /// The phases a Run Profile of the given type moves through, in order.
    /// Returns an empty list for a run type JIM does not execute.
    /// </summary>
    public static IReadOnlyList<RunProfilePhase> GetPhases(ConnectedSystemRunType runType) => runType switch
    {
        ConnectedSystemRunType.FullImport or ConnectedSystemRunType.DeltaImport => ImportPhases,
        ConnectedSystemRunType.FullSynchronisation or ConnectedSystemRunType.DeltaSynchronisation => SynchronisationPhases,
        ConnectedSystemRunType.Export => ExportPhases,
        _ => NoPhases
    };

    /// <summary>
    /// The key of the phase during which the Connector runs for this run type, or null when the run
    /// type does not call a Connector. A Connector's own declared phases nest inside this phase.
    /// </summary>
    public static string? GetConnectorHostPhaseKey(ConnectedSystemRunType runType) =>
        GetPhases(runType).FirstOrDefault(p => p.HostsConnectorPhases)?.Key;
}
