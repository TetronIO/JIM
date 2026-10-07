// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// Starts a preview of what a Full Synchronisation of a Connected System would do (#1530). The body is optional:
/// the system is named in the route, and by default every one of its objects is evaluated.
/// </summary>
public class StartConnectedSystemFullSynchronisationPreviewRequest
{
    /// <summary>
    /// Optional: evaluate only this many objects, in the order the synchronisation would meet them, rather than all
    /// of them. Omit it to evaluate every object, which is the only way the counts describe the whole system; a capped
    /// preview carries a warning saying so. Must be at least 1, or the proposal comes back blocked.
    /// </summary>
    public int? MaxObjects { get; set; }

    /// <summary>
    /// Whether every drill-down row is kept, or only the per-group cap's worth. Capped by default, which is the
    /// right answer for all but the largest previews. Group counts are exact either way; this decides only how much
    /// of the detail behind them can be read back.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;
}
