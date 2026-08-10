// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// A proposed partition and container selection for a Connected System, submitted to find out what it would do
/// before making it (#827/#1251).
/// </summary>
/// <remarks>
/// The whole selection is sent at once, unlike the update endpoints, which set one partition's or one container's
/// <c>selected</c> flag. That is not an inconsistency: what a deselection costs depends on the rest of the
/// selection, because an object leaves scope only when nothing else still covers it. Previewing one tick box at a
/// time would answer a question nobody is asking, and would answer it wrongly for anyone changing several.
///
/// The natural way to use it is to read the current selection from
/// <c>GET connected-systems/{id}/partitions</c>, apply the intended changes to those id lists, preview the result,
/// and then make the changes through the update endpoints.
/// </remarks>
public class StartConnectedSystemScopeSelectionPreviewRequest
{
    /// <summary>
    /// The partitions that would be managed. Omitted or null previews the partitions currently selected, so a
    /// request changing only containers need not restate them.
    /// </summary>
    public List<int>? SelectedPartitionIds { get; set; }

    /// <summary>
    /// The containers that would be managed. Omitted or null previews the containers currently selected.
    ///
    /// Selecting a container selects its whole subtree, so a descendant does not need listing to be in scope; send
    /// the containers that would carry a tick, exactly as the Partitions tab records them.
    /// </summary>
    public List<int>? SelectedContainerIds { get; set; }

    /// <summary>
    /// Whether every drill-down row is kept, or only the per-group cap's worth. Capped by default, which is the
    /// right answer for all but the largest previews. Group counts are exact either way; this decides only how much
    /// of the detail behind them can be read back.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;
}
