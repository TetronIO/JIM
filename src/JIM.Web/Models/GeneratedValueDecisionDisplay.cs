// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.UniqueValues;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Localisation;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// What the Generated Values surfaces (the Operations tab, the Metaverse Object's banner) say about a held generated value
/// (Unique Value Generation, #242, release 4, Phase 9): the "why it is held" sentence for each reason, its second line,
/// and whether the value offers its two actions. One place, so the tab and the banner cannot explain the same value two
/// ways. Written in the viewer's language (#3030): the words come from <c>GeneratedValueText</c>. A "why" sentence is
/// written as pieces around its Connected System chips, which the component places, so each language's pieces keep the
/// same order of subject, verb and object.
/// </summary>
public static class GeneratedValueDecisionDisplay
{
    /// <summary>
    /// Whether the value is waiting on an administrator, and so offers "Allow the rename…" and "Try again". An allowed
    /// rename is waiting on the next export, not on anyone.
    /// </summary>
    public static bool OffersActions(GeneratedValueDecisionHeader decision) =>
        decision.Status == GeneratedValueDecisionStatus.NeedsDecision;

    /// <summary>
    /// Why the value is held, as text and Connected System pieces. A system that has since been deleted is named in plain
    /// text, since there is nothing to link its chip to.
    /// </summary>
    public static IReadOnlyList<GeneratedValueDecisionWhyPart> Why(GeneratedValueDecisionHeader decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var rejectedBy = System(decision.RejectedByConnectedSystemId, decision.RejectedByConnectedSystemName, GeneratedValueText.WhyRejectedFallback);
        var anchoredBy = System(decision.AnchoredByConnectedSystemId, decision.AnchoredByConnectedSystemName, GeneratedValueText.WhyAnchoredFallback);

        return decision.Reason switch
        {
            GeneratedValueNeedsDecisionReason.AnchoredElsewhere =>
                [rejectedBy, Text(GeneratedValueText.WhyRejectedAnchored), anchoredBy, Text(GeneratedValueText.WhyAnchoredHas)],
            GeneratedValueNeedsDecisionReason.CannotTell =>
                [rejectedBy, Text(GeneratedValueText.WhyCannotTellJoin), anchoredBy, Text(GeneratedValueText.WhyCannotTellHolds)],
            GeneratedValueNeedsDecisionReason.RemediationLimitReached =>
                [Text(string.Format(CultureInfo.CurrentCulture, GeneratedValueText.WhyRemediationLimit, Times(decision.RemediationCount))),
                    System(decision.RejectedByConnectedSystemId, decision.RejectedByConnectedSystemName, GeneratedValueText.WhyRejectedFallbackLower),
                    Text(".")],
            GeneratedValueNeedsDecisionReason.NoValueAvailable =>
                [rejectedBy, Text(GeneratedValueText.WhyNoValueAvailable)],
            _ => [rejectedBy, Text(GeneratedValueText.WhyRejectedOnly)]
        };
    }

    /// <summary>
    /// The second line under <see cref="Why"/>: what the reason means for the administrator. Null when there is nothing
    /// to add (no reason was recorded).
    /// </summary>
    public static string? WhySecondary(GeneratedValueDecisionHeader decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return decision.Reason switch
        {
            GeneratedValueNeedsDecisionReason.AnchoredElsewhere => GeneratedValueText.WhyCorrectingRenames,
            GeneratedValueNeedsDecisionReason.CannotTell =>
                string.Format(CultureInfo.CurrentCulture, GeneratedValueText.WhyCannotTell,
                    decision.AnchoredByConnectedSystemName ?? GeneratedValueText.WhyThatSystem),
            GeneratedValueNeedsDecisionReason.RemediationLimitReached =>
                string.Format(CultureInfo.CurrentCulture, GeneratedValueText.WhyRemediationLimitStop,
                    UniqueValueGenerationServer.MaximumRemediations),
            GeneratedValueNeedsDecisionReason.NoValueAvailable => GeneratedValueText.WhyNoValueRaise,
            _ => null
        };
    }

    private static GeneratedValueDecisionWhyPart Text(string text) => new(text);

    private static GeneratedValueDecisionWhyPart System(int? id, string? name, string whenDeleted) =>
        id.HasValue && !string.IsNullOrWhiteSpace(name) ? new GeneratedValueDecisionWhyPart(name, id) : new GeneratedValueDecisionWhyPart(whenDeleted);

    private static string Times(int count) =>
        PluralRules.Choose(count, GeneratedValueText.WhyTimesOne,
            string.Format(CultureInfo.CurrentCulture, GeneratedValueText.WhyTimesOther, count.ToString("N0", CultureInfo.CurrentCulture)));
}
