// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Implemented by a Connector that keeps its Delta Import watermark, the point a Delta Import reads changes after, in the
/// Connected System's persisted connector data. Through it JIM describes the watermark to an administrator, and removes it
/// when the Connector Space is cleared. JIM records each import's watermark on its Activity exactly as the Connector
/// persisted it, and a Connector's persisted data can hold more than the watermark, so only the Connector can say what the
/// value means or which part of it is the watermark.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="IConnectorDetectedCapabilities"/>, JIM never parses a Connector's persisted data itself; it only hands
/// the stored value back to the Connector that wrote it.
/// </para>
/// <para>
/// A Connector that keeps a watermark for each Partition instead implements <see cref="IConnectorPartitionWatermarks"/>,
/// and JIM holds and clears those watermarks itself. A Connector that keeps a Delta Import watermark in its persisted
/// connector data must implement this interface, or a Connector Space clear would leave the watermark behind and the next
/// Delta Import would never read back the objects the clear removed (#2724).
/// </para>
/// </remarks>
public interface IConnectorImportWatermark
{
    /// <summary>
    /// Describes the watermark held in a Connected System's persisted connector data, as it stood at one point in time.
    /// </summary>
    /// <param name="persistedConnectorData">The persisted connector data an import read from or recorded, or null when there was none.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <returns>
    /// The description, or null when the data holds no watermark: null or empty data, data that cannot be parsed, or
    /// data recorded before the Connector kept a watermark. Must never throw, since it is called from the UI and API
    /// rendering path.
    /// </returns>
    public ImportWatermarkDescription? DescribeImportWatermark(string? persistedConnectorData, ILogger logger);

    /// <summary>
    /// Removes the Delta Import watermark from a Connected System's persisted connector data, keeping anything else the
    /// Connector remembers between runs. Called when the Connector Space is cleared: with no Connected System Objects left
    /// to read changes against, the next Delta Import must read every object, as it does for a Connected System that has
    /// never been imported.
    /// </summary>
    /// <param name="persistedConnectorData">The Connected System's persisted connector data. Never null.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <returns>
    /// The persisted connector data without its watermark, or null when the data held nothing else. Data that cannot be
    /// parsed holds no watermark the Connector could use, so returning null for it is right whenever the Connector keeps
    /// nothing but its watermark there.
    /// </returns>
    public string? RemoveImportWatermark(string persistedConnectorData, ILogger logger);
}
