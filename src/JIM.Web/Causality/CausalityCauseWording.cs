// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;

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

        // A derived source-import hop carries no edge type, so it is worded before the switch: the true root
        // of a chain, where data arrived at (or disappeared from) the source system. The system is named in
        // the sentence and never chipped on this hop; see ShowConnectedSystemChip.
        if (cohort.SourceImportChangeType is { } importChangeType)
        {
            var systemName = string.IsNullOrWhiteSpace(cohort.ConnectedSystemName)
                ? "the source system"
                : cohort.ConnectedSystemName;
            parts.Add(new CausalityCauseSentencePart(importChangeType switch
            {
                ObjectChangeType.Updated =>
                    $"{Subject(cohort)} was imported from {systemName} with changed attributes",
                ObjectChangeType.Deleted =>
                    $"{Subject(cohort)}'s record was deleted from {systemName}",
                _ => $"{Subject(cohort)} was imported into {systemName} as a new record"
            }));
            return parts;
        }

        switch (cohort.EdgeType)
        {
            case CausalEdgeType.ExportCausedImportConfirmation:
                parts.Add(new CausalityCauseSentencePart(
                    $"{Subject(cohort)} {(plural ? "were" : "was")} exported, and this import confirms {(plural ? "them" : "it")}"));
                break;

            case CausalEdgeType.PendingExportQueueingCausedExportExecution:
                // Keyed on what the synchronisation decided, so the verb answers create-versus-update at a
                // glance. The system exported to is named here rather than as a chip: the chip would restate
                // the page's own system with no role beside it (see ShowConnectedSystemChip). NotSet covers
                // edges written before the reason codes existed and keeps their original sentence.
                parts.Add(new CausalityCauseSentencePart(cohort.ReasonCode switch
                {
                    CausalReasonCode.ExportCreateStaged when !string.IsNullOrWhiteSpace(cohort.ConnectedSystemName) =>
                        $"{Subject(cohort)} was provisioned to {cohort.ConnectedSystemName}, so this run created the record",
                    CausalReasonCode.ExportCreateStaged =>
                        $"{Subject(cohort)} was provisioned, so this run created the record",
                    CausalReasonCode.ExportUpdateStaged =>
                        $"{Subject(cohort)}'s Identity changed, so this run applied the changes to the record",
                    CausalReasonCode.ExportDeleteStaged =>
                        $"The Identity {Subject(cohort)} was deleted, so this run deleted the record",
                    _ => $"A synchronisation of {Subject(cohort)} staged this change, and this run exported it"
                }));
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
    /// Why the causes happened, or null where no reason was recorded. Derived from the code rather than stored
    /// as prose, so the wording can be improved without rewriting history.
    /// </summary>
    /// <remarks>
    /// Two forms of every phrase, because the row is a sentence and the Connected System chip is its subject.
    /// Where the cohort names one, the chip renders immediately before this phrase and the phrase opens with
    /// its verb ("Yellowstone APAC <i>was the last authoritative source to disconnect</i>"); where it does not,
    /// the phrase has to name its own subject instead. The alternative shipped first and was the defect: an
    /// unattributed fragment ("All authoritative sources disconnected") sat beside a chip with no stated role,
    /// saying neither what had disconnected from what nor that any of it explained the deletion.
    ///
    /// Every phrase names the Deletion Rule for that last reason. The row explains why the <b>cause</b>
    /// happened, one level above the sentence it sits under, and without naming the deletion a reader has no
    /// way to tell which of the two it is about.
    /// </remarks>
    /// <param name="cohort">The cohort whose reason is being read, consulted for whether a Connected System
    /// was recorded alongside it.</param>
    public static string? Reason(CausalChainCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);

        // The queueing seam's reasons are already told by its sentence; the one thing left to attribute is a
        // create's provisioning decision, which reads on from the Synchronisation Rule chip as its subject.
        if (cohort.ReasonCode is CausalReasonCode.ExportCreateStaged)
            return cohort.SyncRuleId.HasValue ? "made the provisioning decision" : null;
        if (cohort.ReasonCode is CausalReasonCode.ExportUpdateStaged or CausalReasonCode.ExportDeleteStaged)
            return null;

        // Kept as one expression per code rather than a shared suffix: an administrator reads these, and a
        // phrase assembled from fragments is a phrase nobody proof-reads as a whole sentence.
        return cohort.HasConnectedSystem
            ? cohort.ReasonCode switch
            {
                CausalReasonCode.LastConnectorDisconnected =>
                    "held the last remaining connection, so the Deletion Rule deleted them",
                CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured =>
                    "held the last remaining connection, so the Deletion Rule deleted them " +
                    "(no authoritative sources were configured)",
                CausalReasonCode.AllAuthoritativeSourcesDisconnected =>
                    "was the last authoritative source to disconnect, so the Deletion Rule deleted them",
                CausalReasonCode.AuthoritativeSourceDisconnected =>
                    "was an authoritative source and disconnected, so the Deletion Rule deleted them",
                _ => null
            }
            : cohort.ReasonCode switch
            {
                CausalReasonCode.LastConnectorDisconnected =>
                    "The last remaining connection was removed, so the Deletion Rule deleted them",
                CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured =>
                    "The last remaining connection was removed, so the Deletion Rule deleted them " +
                    "(no authoritative sources were configured)",
                CausalReasonCode.AllAuthoritativeSourcesDisconnected =>
                    "The last authoritative source disconnected, so the Deletion Rule deleted them",
                CausalReasonCode.AuthoritativeSourceDisconnected =>
                    "An authoritative source disconnected, so the Deletion Rule deleted them",
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
    /// Whether the hop should render the cohort's Connected System as a chip. False where the sentence
    /// already names the system (the queueing seam): a chip there would restate the very system the page is
    /// about with no role stated beside it, which is the unattributed-token shape the attribution row was
    /// redesigned to remove. The rule is that the system appears exactly once per hop, either as the subject
    /// of the reason phrase (via its chip) or inside the sentence, never both.
    /// </summary>
    public static bool ShowConnectedSystemChip(CausalChainCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        return cohort.SourceImportChangeType is null
            && cohort.EdgeType != CausalEdgeType.PendingExportQueueingCausedExportExecution;
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
