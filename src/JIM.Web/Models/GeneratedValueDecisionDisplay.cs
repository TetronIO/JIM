// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// What the Generated Values surfaces (the Operations tab, the Metaverse Object's banner) say about a held generated value
/// (Unique Value Generation, #242, release 4, Phase 9): the "why it is held" sentence for each reason, its second line,
/// and whether the value offers its two actions. One place, so the tab and the banner cannot explain the same value two
/// ways.
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

        var rejectedBy = System(decision.RejectedByConnectedSystemId, decision.RejectedByConnectedSystemName, "A Connected System");
        var anchoredBy = System(decision.AnchoredByConnectedSystemId, decision.AnchoredByConnectedSystemName, "another Connected System");

        return decision.Reason switch
        {
            GeneratedValueNeedsDecisionReason.AnchoredElsewhere =>
                [rejectedBy, Text(" rejected it as already in use; "), anchoredBy, Text(" has provisioned an account with it.")],
            GeneratedValueNeedsDecisionReason.CannotTell =>
                [rejectedBy, Text(" rejected it as already in use; JIM could not tell whether "), anchoredBy, Text(" holds it.")],
            GeneratedValueNeedsDecisionReason.RemediationLimitReached =>
                [Text($"Corrected {Times(decision.RemediationCount)} and still rejected by "), System(decision.RejectedByConnectedSystemId, decision.RejectedByConnectedSystemName, "a Connected System"), Text(".")],
            GeneratedValueNeedsDecisionReason.NoValueAvailable =>
                [rejectedBy, Text(" rejected it as already in use, and JIM found no other value it could issue.")],
            _ => [rejectedBy, Text(" rejected it as already in use.")]
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
            GeneratedValueNeedsDecisionReason.AnchoredElsewhere => "Correcting it would rename that account.",
            GeneratedValueNeedsDecisionReason.CannotTell =>
                $"{decision.AnchoredByConnectedSystemName ?? "That Connected System"}'s connector space was cleared and has not been fully imported since.",
            GeneratedValueNeedsDecisionReason.RemediationLimitReached =>
                $"JIM stops after {UniqueValueGenerationServer.MaximumRemediations} corrections so a misconfigured target cannot use up values.",
            GeneratedValueNeedsDecisionReason.NoValueAvailable =>
                "Raise the Attribute Flow's attempt limit or widen its fixed width, then try again.",
            _ => null
        };
    }

    private static GeneratedValueDecisionWhyPart Text(string text) => new(text);

    private static GeneratedValueDecisionWhyPart System(int? id, string? name, string whenDeleted) =>
        id.HasValue && !string.IsNullOrWhiteSpace(name) ? new GeneratedValueDecisionWhyPart(name, id) : new GeneratedValueDecisionWhyPart(whenDeleted);

    private static string Times(int count) => count == 1 ? "once" : $"{count:N0} times";
}
