// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to say, in terms an administrator can read, where one of its import watermarks stands: the
/// point a Delta Import reads changes after. JIM records each import's watermark on its Activity exactly as the
/// Connector persisted it, and the Connector's persisted data commonly holds far more than the watermark (an LDAP
/// directory's detected capabilities, for example), so the raw value alone tells an administrator very little.
/// </summary>
/// <remarks>
/// Like <see cref="IConnectorDetectedCapabilities"/>, JIM never parses a Connector's persisted data itself; it only
/// hands the stored value back to the Connector that wrote it.
/// </remarks>
public interface IConnectorImportWatermarkDescription
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
}
