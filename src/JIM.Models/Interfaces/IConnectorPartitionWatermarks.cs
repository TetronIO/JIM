// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;

namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector with Partitions to keep a Delta Import watermark for each Partition, held by JIM, so a Run
/// Profile that reads one Partition moves only that Partition's starting point (#2715).
/// </summary>
/// <remarks>
/// <para>
/// Only a Connector that imports using calls (<see cref="IConnectorImportUsingCalls"/>) is handed watermarks. JIM hands
/// it the watermark it holds for every Partition the run targets, once, before it opens the import connection, and
/// hands nothing per page: the values stay the same for the whole run, whatever the pages return, so anything built
/// from them on the first page (an LDAP paging cookie, for example) stays valid on the next. The Connector returns what
/// it reached in <see cref="ConnectedSystemImportResult.PartitionWatermarks"/>; JIM takes the first page that carries a
/// map.
/// </para>
/// <para>
/// JIM records those values, in one write with the Connected System's persisted connector data, only once the run has
/// staged everything it read. It records nothing when the run fails or is cancelled, when a Delta Import withheld the
/// deletes its Connector reported, when a Delta Import that read every object did not finish deletion detection, or
/// when <see cref="IConnectorImportUsingCalls.CloseImportConnection"/> returned data. A page whose map names a Partition
/// the run does not target fails the run before anything is staged, as a fault in the Connector. A completed run also
/// removes the watermarks of Partitions that are no longer selected.
/// </para>
/// <para>
/// Like <see cref="IConnectorImportWatermarkDescription"/>, JIM never parses a value itself; it only hands it back to
/// the Connector that wrote it. A Connector that implements this keeps its Connected System-level persisted connector
/// data for anything else it needs to remember between runs.
/// </para>
/// </remarks>
public interface IConnectorPartitionWatermarks
{
    /// <summary>
    /// Hands the Connector the watermark JIM holds for every Partition the run targets. Called once per run, before the
    /// import connection is opened.
    /// </summary>
    /// <param name="watermarks">
    /// Every Partition the run targets, by Partition id, with the watermark JIM recorded for it, or null where none is
    /// recorded: a Partition never imported, or one whose watermark was cleared (a Connector Space clear, or the
    /// Partition being deselected). A Partition with no watermark must be read in full. Empty when the run targets no
    /// Partition. The map is read-only and does not change during the run.
    /// </param>
    void SetPartitionWatermarks(IReadOnlyDictionary<int, string?> watermarks);

    /// <summary>
    /// Describes one recorded Partition watermark, in terms an administrator can read, for the Activity page, the REST
    /// API and PowerShell.
    /// </summary>
    /// <param name="value">A Partition watermark as the Connector returned it, or null when there was none.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <returns>
    /// The description, or null when the value holds no watermark: null or empty, or a value that cannot be parsed.
    /// Must never throw, since it is called from the UI and API rendering path.
    /// </returns>
    ImportWatermarkDescription? DescribePartitionWatermark(string? value, ILogger logger);
}
