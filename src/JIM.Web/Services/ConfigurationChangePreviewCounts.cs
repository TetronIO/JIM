// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;
using JIM.Web.Models;

namespace JIM.Web.Services;

/// <summary>
/// Turns a Configuration Change Preview into the counts a save confirmation may state (#827).
///
/// The value here is the withholding, not the mapping. A number on a confirmation dialog is read as an answer
/// whatever caveats sit beside it, so a preview that failed, has not finished counting, or was run against settings
/// the administrator has since edited must contribute nothing at all rather than something hedged. Shared across
/// editors so each adapter's surface does not re-derive those rules, and get one of them slightly wrong.
/// </summary>
public static class ConfigurationChangePreviewCounts
{
    /// <summary>
    /// What the preview may state on a save confirmation, largest impact first.
    /// </summary>
    /// <param name="preview">The preview currently on screen, or null where none was run.</param>
    /// <param name="isStale">
    /// Whether the editor has moved on from the configuration the preview was run against. The editor owns this
    /// judgement because only it knows what is on the form; what this type owns is that a stale preview says
    /// nothing.
    /// </param>
    public static IReadOnlyList<ImpactCount> ForConfirmation(ConfigurationChangePreview? preview, bool isStale)
    {
        if (isStale ||
            preview is not { HasFailed: false, ImpactCountsStatus: ConfigurationChangePreviewStageStatus.Complete })
        {
            return [];
        }

        var counts = preview.ReadImpactCounts();
        if (counts.Count == 0)
        {
            // The preview ran and found nothing, which is worth stating: an absent section reads as "no preview was
            // run", and those are different statements about very different situations.
            return [new ImpactCount { Label = "Objects affected by this change", Count = 0, Note = "from the preview" }];
        }

        return
        [
            .. counts
                .OrderByDescending(c => c.ObjectCount)
                .ThenBy(c => c.TransitionType)
                .Select(c => new ImpactCount
                {
                    Label = Helpers.GetOutcomeTypeDisplayName(c.TransitionType),
                    Count = c.ObjectCount,
                    Note = "from the preview"
                })
        ];
    }
}
