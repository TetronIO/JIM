// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// Starts a preview of deleting a Connected System with Synchronised Deprovisioning (#134). The body is optional:
/// there is only one deletion to preview, and the system is named in the route.
/// </summary>
public class StartConnectedSystemDeletionPreviewRequest
{
    /// <summary>
    /// Whether every drill-down row is kept, or only the per-group cap's worth. Capped by default, which is the
    /// right answer for all but the largest previews. Group counts are exact either way; this decides only how much
    /// of the detail behind them can be read back.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;
}
