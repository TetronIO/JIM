// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// A proposed change to a Metaverse Object Type's deletion settings, submitted to find out what it would do before
/// making it (#827/#1114).
///
/// The field semantics are deliberately identical to <see cref="UpdateMetaverseObjectTypeRequest"/>: an omitted
/// field keeps the stored value. A preview whose omitted fields meant something different from the update's would
/// answer a question about a change nobody was proposing, and would answer it with the same confidence as a
/// correct one. Send the same body to this endpoint and then to the update, and the preview describes exactly what
/// the update will do.
/// </summary>
public class StartMetaverseObjectTypeDeletionSettingsPreviewRequest
{
    /// <summary>
    /// The proposed deletion rule. Omitted or null previews the stored rule.
    /// </summary>
    public MetaverseObjectDeletionRule? DeletionRule { get; set; }

    /// <summary>
    /// The proposed grace period, as a duration string (for example "7.00:00:00" for seven days). Omitted or null
    /// previews the stored grace period; "00:00:00" previews no grace period, matching how the update endpoint
    /// stores it.
    /// </summary>
    public TimeSpan? DeletionGracePeriod { get; set; }

    /// <summary>
    /// The proposed authoritative sources. Omitted or null previews the stored list.
    ///
    /// Worth stating plainly: this list is read at the moment a Connected System Object disconnects, not by the
    /// housekeeping sweep that acts on objects already marked, so changing it alone moves no object's deletion date
    /// and the preview will honestly report no impact from it. What it can do is make the proposal invalid, which
    /// comes back as a blocking validation finding.
    /// </summary>
    public List<int>? DeletionTriggerConnectedSystemIds { get; set; }

    /// <summary>
    /// The proposed trigger mode. Omitted or null previews the stored mode. Supply the enum member name as a string.
    /// </summary>
    public AuthoritativeSourceTriggerMode? DeletionTriggerMode { get; set; }

    /// <summary>
    /// Whether every drill-down row is kept, or only the per-group cap's worth. Capped by default, which is the
    /// right answer for all but the largest previews. Group counts are exact either way; this decides only how much
    /// of the detail behind them can be read back.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;
}
