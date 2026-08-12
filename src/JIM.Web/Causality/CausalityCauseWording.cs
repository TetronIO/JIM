// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;

namespace JIM.Web.Causality;

/// <summary>
/// Turns a causal cohort into the plain-language sentence the "Caused by" chain reads back, plus the
/// short phrases beside it: why the cause happened, and why the walk stopped where it did (#1223).
/// </summary>
/// <remarks>
/// Deliberately separate from the component that renders it, so the wording can be asserted as whole
/// sentences rather than through the DOM. Every noun here comes from the edge's own snapshots, never
/// from the live schema: a cascade routinely deletes the very objects it names, and the type or
/// attribute may since have been renamed.
/// </remarks>
public static class CausalityCauseWording
{
    /// <summary>
    /// Composes the sentence for one cohort: what the causes were, what happened to them, and what that
    /// did to <paramref name="effectName"/>.
    /// </summary>
    /// <param name="cohort">The cohort of causes to describe.</param>
    /// <param name="effectName">
    /// The name of the object the cohort acted on, where one is known: the record this page is about at
    /// the first level, and the cause one level up thereafter. Null degrades the sentence rather than
    /// blanking it.
    /// </param>
    public static IReadOnlyList<CausalityCauseSentencePart> Sentence(CausalChainCohort cohort, string? effectName)
    {
        ArgumentNullException.ThrowIfNull(cohort);

        var parts = new List<CausalityCauseSentencePart>();
        var plural = cohort.MemberCount != 1;

        switch (cohort.EdgeType)
        {
            case CausalEdgeType.ExportCausedImportConfirmation:
                parts.Add(new CausalityCauseSentencePart(
                    $"{Subject(cohort)} {(plural ? "were" : "was")} exported, and this import confirms {(plural ? "them" : "it")}"));
                break;

            case CausalEdgeType.MetaverseObjectDeletionCausedDeprovision:
                parts.Add(new CausalityCauseSentencePart(
                    $"{Subject(cohort)} {(plural ? "were" : "was")} deleted, so this deprovisioning was queued"));
                break;

            case CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval:
            default:
                AppendReferenceRemoval(parts, cohort, effectName, plural);
                break;
        }

        return parts;
    }

    /// <summary>
    /// The short phrase explaining why the causes happened, or null where no reason was recorded. Derived
    /// from the code rather than stored as prose, so the wording can be improved without rewriting history.
    /// </summary>
    public static string? Reason(CausalReasonCode reasonCode)
    {
        return reasonCode switch
        {
            CausalReasonCode.LastConnectorDisconnected => "Last connector disconnected",
            CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured =>
                "Last connector disconnected, with no authoritative sources configured",
            CausalReasonCode.AllAuthoritativeSourcesDisconnected => "All authoritative sources disconnected",
            CausalReasonCode.AuthoritativeSourceDisconnected => "An authoritative source disconnected",
            _ => null
        };
    }

    /// <summary>
    /// What to say where the walk stopped, or null where it did not stop (the cause's own causes follow
    /// instead). The three terminal states mean entirely different things and never share a phrase: one is
    /// the whole story, one is history aged out, one is the depth bound.
    /// </summary>
    public static string? Ending(CausalChainResolution resolution)
    {
        return resolution switch
        {
            CausalChainResolution.NoFurtherCauses => "End of the recorded causality chain",
            CausalChainResolution.CauseNotRetained => "What caused this is no longer retained",
            CausalChainResolution.DepthLimitReached => "More causes exist beyond this point",
            _ => null
        };
    }

    /// <summary>
    /// The label on the disclosure that lists a cohort's individual causes.
    /// </summary>
    public static string MembersLabel(CausalChainCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        return $"Show the {cohort.MemberCount} {Noun(cohort)}";
    }

    /// <summary>
    /// The same disclosure's label once it is open. Repeats the count rather than reading "Hide", so the
    /// cohort's size stays on screen while its causes are being read.
    /// </summary>
    public static string HideMembersLabel(CausalChainCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        return $"Hide the {cohort.MemberCount} {Noun(cohort)}";
    }

    /// <summary>
    /// "10 Users", or the single cause's own name where the cohort speaks for one: a cohort of one is the
    /// degenerate case and naming it is far more useful than counting it.
    /// </summary>
    private static string Subject(CausalChainCohort cohort)
    {
        if (cohort.MemberCount == 1)
        {
            var name = cohort.Members[0].DisplayName;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return $"{cohort.MemberCount} {Noun(cohort)}";
    }

    /// <summary>
    /// The cohort's own noun, singular or plural to match its size, falling back to a generic where the
    /// edge recorded no type name (pre-snapshot rows, or a cause with no Metaverse Object Type).
    /// </summary>
    private static string Noun(CausalChainCohort cohort)
    {
        var noun = cohort.ObjectNoun;
        if (!string.IsNullOrWhiteSpace(noun))
            return noun;

        return cohort.MemberCount == 1 ? "object" : "objects";
    }

    /// <summary>
    /// The reference-removal sentence, the one case carrying a highlighted span. Without an attribute name
    /// it still states the removal rather than falling silent: an unnamed relationship is less useful than
    /// a named one, but far more useful than no sentence at all.
    /// </summary>
    private static void AppendReferenceRemoval(
        List<CausalityCauseSentencePart> parts,
        CausalChainCohort cohort,
        string? effectName,
        bool plural)
    {
        var lead = $"{Subject(cohort)} {(plural ? "were" : "was")} deleted, so ";

        if (string.IsNullOrWhiteSpace(cohort.AttributeName))
        {
            var target = string.IsNullOrWhiteSpace(effectName)
                ? "the references to them were removed"
                : $"the references to them were removed from {effectName}";
            parts.Add(new CausalityCauseSentencePart(lead + target));
            return;
        }

        // Singular "they" throughout: a cause is as often a person as a group, and the chain must not
        // guess a pronoun for either.
        var owner = string.IsNullOrWhiteSpace(effectName) ? string.Empty : $"{effectName}'s ";
        parts.Add(new CausalityCauseSentencePart($"{lead}they were removed from {owner}"));
        parts.Add(new CausalityCauseSentencePart(cohort.AttributeName, IsAttributeName: true));
    }
}
