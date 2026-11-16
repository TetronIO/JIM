// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Staging;

/// <summary>
/// A Connected System's import-incomplete marker (<see cref="ConnectedSystem.ImportIncompleteSince"/>, #2521): the one
/// wording of why a synchronisation is refused while an import has stopped part-way, and of what clears it, shared by the
/// Worker's refusal and the portal so that both say the same thing.
/// </summary>
/// <remarks>
/// User-facing text writes "partition" in lower case, as the run refusals for a deselected partition already do.
/// </remarks>
public static class ImportCompleteness
{
    /// <summary>
    /// Whether a run of this type refuses while the Connected System is marked import incomplete: Full and Delta
    /// Synchronisation read the Connector Space and act on it. An import is the remedy, and an export sends what earlier
    /// synchronisations decided.
    /// </summary>
    public static bool RefusesWhileImportIncomplete(ConnectedSystemRunType runType) =>
        runType is ConnectedSystemRunType.FullSynchronisation or ConnectedSystemRunType.DeltaSynchronisation;

    /// <summary>
    /// Why a synchronisation of a Connected System marked import incomplete is refused, naming the Run Profile, the
    /// Connected System, when the stopped import began writing, the risk and the remedy.
    /// </summary>
    public static string DescribeSynchronisationRefusal(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(runProfile);

        var since = connectedSystem.ImportIncompleteSince is { } startedWritingAt
            ? $" (it began writing on {FormatUtc(startedWritingAt)})"
            : string.Empty;

        return $"Run Profile '{runProfile.Name}' cannot run: an import of Connected System '{connectedSystem.Name}' stopped part-way through " +
               $"saving what it read{since}, so the Connector Space holds only part of that import. Synchronising it now could remove " +
               "Metaverse references to objects the import had not yet saved, and export those removals. " +
               $"{DescribeRemedy(connectedSystem)}";
    }

    /// <summary>
    /// What clears the marker, as an instruction: a Full Import that reads the whole Connected System, run to completion.
    /// </summary>
    public static string DescribeRemedy(ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        var scope = connectedSystem.Partitions?.Any(p => p.Selected) == true
            ? ", reading all selected partitions (a Run Profile that targets one partition is not enough)"
            : string.Empty;

        return $"Run a Full Import of this Connected System to completion{scope}; synchronisation can run once one has.";
    }

    /// <summary>
    /// What an import that completed says on its Activity when it cannot take down the marker an earlier stopped import
    /// left: it did not read every object of the whole Connected System, so synchronisation still waits.
    /// </summary>
    /// <param name="connectedSystem">The Connected System the import read.</param>
    /// <param name="readOnlyChanges">The run was a Delta Import whose Connector read only what changed.</param>
    /// <param name="scopedToOnePartition">The run's Run Profile targets one partition.</param>
    public static string DescribeStillIncompleteAfterImport(ConnectedSystem connectedSystem, bool readOnlyChanges, bool scopedToOnePartition)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        var since = connectedSystem.ImportIncompleteSince is { } startedWritingAt
            ? $" (it began writing on {FormatUtc(startedWritingAt)})"
            : string.Empty;
        var thisRun = readOnlyChanges ? "This Delta Import read only what changed"
            : scopedToOnePartition ? "This import read only one partition"
            : "This import read no objects";

        return $"An earlier import stopped part-way through saving what it read{since}, so synchronisation of this Connected System is refused. " +
               $"{thisRun}, so it cannot repair that. {DescribeRemedy(connectedSystem)}";
    }

    /// <summary>A UTC time as the portal's friendly dates read ("16 Nov 2026 10:03:12"), marked UTC.</summary>
    private static string FormatUtc(DateTime utc) =>
        utc.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}
