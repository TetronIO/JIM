// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;
using System.Diagnostics.CodeAnalysis;
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
        if (!MayState(preview, isStale))
            return [];

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
                    // The plain label, not the technical one: this is read by an administrator deciding whether to
                    // save, not by an operator reading a completed run.
                    Label = Helpers.GetOutcomeTypePlainName(c.TransitionType),
                    Count = c.ObjectCount,
                    Note = "from the preview"
                })
        ];
    }

    /// <summary>
    /// The same answer as a sentence, for a confirmation that leads with what the change would do rather than
    /// tabulating it (#1275). Null where <see cref="ForConfirmation"/> would state nothing, and null where the
    /// preview found nothing: a confirmation that already lists the properties changing does not need a line saying
    /// no objects move, and the reassurance is better placed on the preview panel the administrator just read.
    /// </summary>
    public static PreviewVerdict? ForConfirmationVerdict(ConfigurationChangePreview? preview, bool isStale) =>
        MayState(preview, isStale)
            ? ConfigurationChangePreviewVerdict.Describe(preview.ReadImpactCounts())
            : null;

    /// <summary>
    /// Whether a preview is entitled to say anything at all on a save confirmation. A preview that failed has
    /// evaluated an arbitrary subset of the population, one still counting has no answer yet, and one run against
    /// settings the administrator has since edited describes a different change; none of those is something to
    /// hedge on screen, so all three say nothing.
    /// </summary>
    private static bool MayState([NotNullWhen(true)] ConfigurationChangePreview? preview, bool isStale) =>
        !isStale &&
        preview is { HasFailed: false, ImpactCountsStatus: ConfigurationChangePreviewStageStatus.Complete };
}
