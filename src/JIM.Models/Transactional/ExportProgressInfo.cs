// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// Progress information for export execution.
/// </summary>
public class ExportProgressInfo
{
    /// <summary>
    /// Current phase of export execution.
    /// </summary>
    public ExportPhase Phase { get; set; }

    /// <summary>
    /// Total number of exports to process.
    /// </summary>
    public int TotalExports { get; set; }

    /// <summary>
    /// Number of exports processed so far.
    /// </summary>
    public int ProcessedExports { get; set; }

    /// <summary>
    /// Size of the current batch being processed.
    /// </summary>
    public int CurrentBatchSize { get; set; }

    /// <summary>
    /// Number of successful exports (only populated in Completed phase).
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed exports (only populated in Completed phase).
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Number of deferred exports (only populated in Completed phase).
    /// </summary>
    public int DeferredCount { get; set; }

    /// <summary>
    /// Human-readable progress message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The Connector's own phase key (#454), set when this report came from the Connector entering
    /// one of the phases it declared, so the worker can advance the step the administrator sees.
    /// Null for JIM's own progress reports. Where the Connector supplied no message with it,
    /// <see cref="Message"/> is empty and the phase's declared name stands in.
    /// </summary>
    public string? ConnectorPhaseKey { get; set; }

    /// <summary>
    /// How much work the pass currently running has of its own, where that differs from the export
    /// as a whole. Null on reports describing the whole export, where <see cref="TotalExports"/>
    /// already says it.
    /// </summary>
    /// <remarks>
    /// Set by the deferred second pass, which covers only what the first pass could not write. Left
    /// on the export's own totals, that pass reported itself finished from the moment it started.
    /// </remarks>
    public int? PassTotal { get; set; }

    /// <summary>
    /// How much of <see cref="PassTotal"/> the current pass has finished with. Null whenever
    /// <see cref="PassTotal"/> is.
    /// </summary>
    public int? PassProcessed { get; set; }

    /// <summary>
    /// The counting window this report describes: the current pass's own work where it has its own,
    /// and the export as a whole otherwise. This is what the Activity's object counters carry, and
    /// so what the portal, the API and PowerShell render progress from.
    /// </summary>
    public (int Total, int Processed) CountingWindow =>
        (PassTotal ?? TotalExports, PassProcessed ?? ProcessedExports);

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public int ProgressPercentage => TotalExports > 0
        ? (int)((double)ProcessedExports / TotalExports * 100)
        : 0;
}
