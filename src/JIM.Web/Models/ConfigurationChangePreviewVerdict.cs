// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;
using JIM.Web.Causality;
using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// The one sentence a Configuration Change Preview leads with: what saving this would do, worst consequence first.
/// </summary>
/// <param name="Severity">The tone of the most serious transition the preview found, for the surrounding alert.</param>
/// <param name="Lead">The worst consequence, stated as a sentence.</param>
/// <param name="Detail">The remaining transitions as one sentence, or null where there are none.</param>
public sealed record PreviewVerdict(Severity Severity, string Lead, string? Detail);

/// <summary>
/// Turns a preview's impact counts into that sentence (#1275).
///
/// The panel used to open with a list of transition chips and their counts, and then repeat both in the summary
/// table underneath. Two renderings of the same facts is one too many, and neither of them answered the question
/// the administrator opened the panel to ask, which is what the worst thing that happens is. This orders the
/// transitions by the weight of their consequence rather than by count, so a change that disconnects forty
/// thousand objects and deletes two still leads with the two deletions.
/// </summary>
public static class ConfigurationChangePreviewVerdict
{
    /// <summary>
    /// Describes what the counted transitions would do, or null where there is nothing to say: no counts at all, or
    /// counts that are all zero. Both of those are the summary's "nothing would change" case, and a verdict of
    /// "0 objects" beside it would be a second, weaker statement of the same thing.
    /// </summary>
    public static PreviewVerdict? Describe(IReadOnlyList<PreviewImpactCount> counts)
    {
        var stated = counts
            .Where(c => c.ObjectCount > 0)
            .OrderBy(c => ConsequenceWeight(OutcomeDisplayMap.Get(c.TransitionType).Tone))
            .ThenByDescending(c => c.ObjectCount)
            .ThenBy(c => c.TransitionType)
            .ToList();

        if (stated.Count == 0)
            return null;

        var severity = ToSeverity(OutcomeDisplayMap.Get(stated[0].TransitionType).Tone);
        var lead = Sentence(stated[0]);
        var detail = stated.Count > 1
            ? string.Join(" ", stated.Skip(1).Select(Sentence))
            : null;

        return new PreviewVerdict(severity, lead, detail);
    }

    /// <summary>
    /// How much an outcome's tone should weigh on the verdict, lowest first. Deliberately not the enum's own order:
    /// <see cref="CausalityTone"/> is declared for the causality palette, and reordering it to suit this would move
    /// colours around the Activity views.
    /// </summary>
    private static int ConsequenceWeight(CausalityTone tone) => tone switch
    {
        CausalityTone.Error => 0,
        CausalityTone.Warning => 1,
        CausalityTone.Primary => 2,
        CausalityTone.Info => 3,
        CausalityTone.Secondary => 4,
        CausalityTone.Success => 5,
        _ => 6
    };

    private static Severity ToSeverity(CausalityTone tone) => tone switch
    {
        CausalityTone.Error => Severity.Error,
        CausalityTone.Warning => Severity.Warning,
        CausalityTone.Success => Severity.Success,
        _ => Severity.Info
    };

    private static string Sentence(PreviewImpactCount count)
    {
        var display = OutcomeDisplayMap.Get(count.TransitionType);

        // "objects" rather than the population's name: an impact count is aggregated across whatever the adapter
        // counted, and only the summary groups below carry a reliable object type. Naming one here would be a guess,
        // and a guess about what is being deleted is not a thing to put in the leading sentence.
        var subject = count.ObjectCount == 1 ? "1 object" : $"{count.ObjectCount:N0} objects";

        // "would" is redundant on a row label, where the panel's heading has already established that nothing has
        // happened yet, but this IS that heading-level statement, and carrying the modal is what lets one bare
        // infinitive serve both "1 object would leave" and "40,000 objects would leave".
        //
        // The sentence form where the outcome has one, otherwise the plain label, which reads as a fragment rather
        // than as a clause but says the right thing. Every transition a preview can produce carries one.
        return display.SentenceForm is { Length: > 0 } clause
            ? $"{subject} would {clause}."
            : $"{subject}: {display.PlainLabel}.";
    }
}
