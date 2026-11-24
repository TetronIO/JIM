// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using System.Globalization;
using JIM.Models.Enums;
using JIM.Web.Localisation;
using JIM.Web.Resources;

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

        // A derived Identity-creation hop states a fact about the Identity's own origin (#1495 follow-up),
        // and is worded before the source-import branch and the edge switch alike: it carries no EdgeType
        // of its own, and testing MetaverseChangeType first is what the hazard on CausalChainCohort
        // requires. Direct creation names no cause to be the subject of a sentence, so it states the fact
        // plainly rather than reusing Subject.
        if (cohort.MetaverseChangeType is { } metaverseChangeType)
        {
            parts.Add(new CausalityCauseSentencePart(metaverseChangeType switch
            {
                ObjectChangeType.Projected => Format(CausalityText.CauseProjected, Subject(cohort)),
                ObjectChangeType.Joined => Format(CausalityText.CauseJoined, Subject(cohort)),
                _ => CausalityText.CauseCreatedDirectly
            }));
            return parts;
        }

        // A derived source-import hop carries no edge type, so it is worded before the switch: the true root
        // of a chain, where data arrived at (or disappeared from) the source system. The system is named in
        // the sentence and never chipped on this hop; see ShowConnectedSystemChip.
        if (cohort.SourceImportChangeType is { } importChangeType)
        {
            var systemName = string.IsNullOrWhiteSpace(cohort.ConnectedSystemName)
                ? CausalityText.CauseSourceSystemFallback
                : cohort.ConnectedSystemName;
            parts.Add(new CausalityCauseSentencePart(importChangeType switch
            {
                ObjectChangeType.Updated =>
                    Format(CausalityText.CauseImportedChanged, Subject(cohort), systemName),
                ObjectChangeType.Deleted =>
                    Format(CausalityText.CauseImportDeleted, Subject(cohort), systemName),
                _ => Format(CausalityText.CauseImportAdded, Subject(cohort), systemName)
            }));
            return parts;
        }

        switch (cohort.EdgeType)
        {
            case CausalEdgeType.ExportCausedImportConfirmation:
                parts.Add(new CausalityCauseSentencePart(
                    Format(plural ? CausalityText.CauseExportConfirmedOther : CausalityText.CauseExportConfirmedOne, Subject(cohort))));
                break;

            case CausalEdgeType.PendingExportQueueingCausedExportExecution:
            {
                // Keyed on what the synchronisation decided, so the verb answers create-versus-update at a
                // glance. The system exported to is named here rather than as a chip: the chip would restate
                // the page's own system with no role beside it (see ShowConnectedSystemChip). NotSet covers
                // edges written before the reason codes existed and keeps their original sentence. Several
                // members here are several runs staging one object's export (#2100), so the object is named once
                // and the runs are counted.
                parts.Add(new CausalityCauseSentencePart(cohort.ReasonCode switch
                {
                    CausalReasonCode.ExportCreateStaged when !string.IsNullOrWhiteSpace(cohort.ConnectedSystemName) =>
                        Format(CausalityText.CauseProvisionedTo, Subject(cohort), cohort.ConnectedSystemName),
                    CausalReasonCode.ExportCreateStaged =>
                        Format(CausalityText.CauseProvisioned, Subject(cohort)),
                    CausalReasonCode.ExportUpdateStaged =>
                        IsStagedBySeveralRuns(cohort)
                            ? Format(CausalityText.CauseMetaverseChangedSeveralRuns, Subject(cohort), cohort.MemberCount)
                            : Format(CausalityText.CauseMetaverseChanged, Subject(cohort)),
                    CausalReasonCode.ExportDeleteStaged =>
                        Format(CausalityText.CauseMetaverseDeleted, Subject(cohort)),
                    CausalReasonCode.ExportDisableStaged =>
                        Format(CausalityText.CauseDeprovisionedDisabled, Subject(cohort)),
                    _ when IsStagedBySeveralRuns(cohort) =>
                        Format(CausalityText.CauseStagedBySeveralRuns, cohort.MemberCount, Subject(cohort)),
                    _ => Format(CausalityText.CauseStagedByOneRun, Subject(cohort))
                }));
                break;
            }

            case CausalEdgeType.MetaverseObjectDeletionCausedDeprovision:
                parts.Add(new CausalityCauseSentencePart(
                    Format(plural ? CausalityText.CauseDeprovisionQueuedOther : CausalityText.CauseDeprovisionQueuedOne, Subject(cohort))));
                break;

            case CausalEdgeType.MetaverseObjectDeletionScheduleCausedDeletion:
                // A grace-period deletion (#1811): the cause is the run that scheduled it, often days earlier, and
                // the Deletion Rule that decided it is attributed by the reason phrase, as on the cascade edges.
                parts.Add(new CausalityCauseSentencePart(
                    Format(plural ? CausalityText.CauseGraceDeletionOther : CausalityText.CauseGraceDeletionOne, Subject(cohort))));
                break;

            case CausalEdgeType.ExportRejectionCausedGeneratedValueRevision:
                // Unique Value Generation (#242): the system that rejected the value is the sentence's own
                // subject, exactly as the queueing seam above names its system inline rather than as a chip
                // (see ShowConnectedSystemChip). The reason code alone decides the verb: what remediation did
                // is fixed by the code, not by anything else the cohort carries.
                var rejectingSystem = string.IsNullOrWhiteSpace(cohort.ConnectedSystemName)
                    ? CausalityText.CauseRejectingSystemFallback
                    : cohort.ConnectedSystemName;
                parts.Add(new CausalityCauseSentencePart(cohort.ReasonCode switch
                {
                    CausalReasonCode.GeneratedValueAlreadyInUse =>
                        Format(CausalityText.CauseRejectedAlreadyInUse, rejectingSystem),
                    CausalReasonCode.GeneratedValueAnchoredElsewhere =>
                        Format(CausalityText.CauseRejectedAnchoredElsewhere, rejectingSystem),
                    CausalReasonCode.GeneratedValueRenameAuthorised =>
                        Format(CausalityText.CauseRejectedRenameAuthorised, rejectingSystem),
                    _ => Format(CausalityText.CauseRejectedRevised, rejectingSystem)
                }));
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
            return cohort.SyncRuleId.HasValue ? CausalityText.ReasonProvisioningDecision : null;
        if (cohort.ReasonCode is CausalReasonCode.ExportUpdateStaged or CausalReasonCode.ExportDeleteStaged or CausalReasonCode.ExportDisableStaged)
            return null;

        // Kept as one expression per code rather than a shared suffix: an administrator reads these, and a
        // phrase assembled from fragments is a phrase nobody proof-reads as a whole sentence.
        return cohort.HasConnectedSystem
            ? cohort.ReasonCode switch
            {
                CausalReasonCode.LastConnectorDisconnected =>
                    CausalityText.ReasonLastConnectionHeld,
                CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured =>
                    CausalityText.ReasonLastConnectionHeldNoSources,
                CausalReasonCode.AllAuthoritativeSourcesDisconnected =>
                    CausalityText.ReasonLastAuthoritativeDisconnected,
                CausalReasonCode.AuthoritativeSourceDisconnected =>
                    CausalityText.ReasonAuthoritativeDisconnected,
                _ => null
            }
            : cohort.ReasonCode switch
            {
                CausalReasonCode.LastConnectorDisconnected =>
                    CausalityText.ReasonLastConnectionRemoved,
                CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured =>
                    CausalityText.ReasonLastConnectionRemovedNoSources,
                CausalReasonCode.AllAuthoritativeSourcesDisconnected =>
                    CausalityText.ReasonLastAuthoritativeSourceDisconnected,
                CausalReasonCode.AuthoritativeSourceDisconnected =>
                    CausalityText.ReasonAuthoritativeSourceDisconnected,
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
            // "No earlier", not "end of the chain": the footer sits under the story's furthest-back column,
            // which in the reader's left-to-right time order is where the story STARTS, so a phrase about
            // ending read backwards there.
            CausalChainResolution.NoFurtherCauses => CausalityText.EndingNoFurtherCauses,
            CausalChainResolution.CauseNotRetained => CausalityText.EndingCauseNotRetained,
            CausalChainResolution.DepthLimitReached => CausalityText.EndingDepthLimitReached,
            _ => null
        };
    }

    /// <summary>
    /// An item's nearest recorded cause as one line of plain text (#2100): the first cohort's sentence and how many more
    /// causes there are, or how the item's story ends where it has none. The REST API's timeline rows carry it, so a
    /// script reads the same line the portal shows.
    /// </summary>
    public static string NearestCauseText(CausalNearestCauses nearest, string? effectName)
    {
        ArgumentNullException.ThrowIfNull(nearest);

        if (nearest.Cohorts.Count == 0)
            return Ending(nearest.Resolution) ?? Ending(CausalChainResolution.NoFurtherCauses)!;

        return string.Concat(Sentence(nearest.Cohorts[0], effectName).Select(p => p.Text)) + MoreCausesSuffix(nearest);
    }

    /// <summary>
    /// What follows the first cause's sentence where an item has more than one: ", and 2 more causes". Empty where it
    /// has one.
    /// </summary>
    public static string MoreCausesSuffix(CausalNearestCauses nearest)
    {
        ArgumentNullException.ThrowIfNull(nearest);

        var more = nearest.TotalCohortCount - 1;
        return more < 1 ? string.Empty : Format(PluralRules.Choose(more, CausalityText.MoreCausesSuffixOne, CausalityText.MoreCausesSuffixOther), more);
    }

    /// <summary>
    /// The label on the disclosure that lists a cohort's individual causes.
    /// </summary>
    public static string MembersLabel(CausalChainCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        return Format(CausalityText.ShowMembersLabel, cohort.MemberCount, MembersNoun(cohort));
    }

    /// <summary>
    /// The same disclosure's label once it is open. Repeats the count rather than reading "Hide", so the
    /// cohort's size stays on screen while its causes are being read.
    /// </summary>
    public static string HideMembersLabel(CausalChainCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        return Format(CausalityText.HideMembersLabel, cohort.MemberCount, MembersNoun(cohort));
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
        return cohort.MetaverseChangeType is null
            && cohort.SourceImportChangeType is null
            && cohort.EdgeType != CausalEdgeType.PendingExportQueueingCausedExportExecution
            // Unique Value Generation (#242): the rejecting system is named inline in the sentence, exactly
            // as the queueing seam above names its own system, so a chip here would restate it with no role.
            && cohort.EdgeType != CausalEdgeType.ExportRejectionCausedGeneratedValueRevision;
    }

    private static string Format(string template, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, template, arguments);

    /// <summary>
    /// "10 Users", or the single cause's own name where the cohort speaks for one: a cohort of one is the
    /// degenerate case and naming it is far more useful than counting it.
    /// </summary>
    private static string Subject(CausalChainCohort cohort)
    {
        if (cohort.MemberCount == 1 || IsStagedBySeveralRuns(cohort))
        {
            var name = cohort.Members[0].DisplayName;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return $"{cohort.MemberCount} {Noun(cohort)}";
    }

    /// <summary>
    /// Whether a cohort is one export staged by several runs (#2100): queueing members all about one object. Its
    /// members are runs, not objects, so it names the object once and counts the runs.
    /// </summary>
    private static bool IsStagedBySeveralRuns(CausalChainCohort cohort) =>
        cohort.EdgeType == CausalEdgeType.PendingExportQueueingCausedExportExecution
        && cohort.MemberCount > 1
        && cohort.Members.Select(m => (m.MetaverseObjectId, m.DisplayName)).Distinct().Count() == 1;

    /// <summary>
    /// What a cohort's members are, for the disclosure that lists them: the runs, where the cohort is one export
    /// staged by several, otherwise the objects.
    /// </summary>
    private static string MembersNoun(CausalChainCohort cohort) =>
        IsStagedBySeveralRuns(cohort) ? CausalityText.MembersNounSynchronisations : Noun(cohort);

    /// <summary>
    /// The cohort's own noun, singular or plural to match its size, falling back to a generic where the
    /// edge recorded no type name (pre-snapshot rows, or a cause with no Metaverse Object Type).
    /// </summary>
    private static string Noun(CausalChainCohort cohort)
    {
        var noun = cohort.ObjectNoun;
        if (!string.IsNullOrWhiteSpace(noun))
            return noun;

        return cohort.MemberCount == 1 ? CausalityText.MembersNounObjectOne : CausalityText.MembersNounObjectOther;
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
        var subject = Subject(cohort);

        if (string.IsNullOrWhiteSpace(cohort.AttributeName))
        {
            var sentence = string.IsNullOrWhiteSpace(effectName)
                ? Format(plural ? CausalityText.CauseReferencesRemovedOther : CausalityText.CauseReferencesRemovedOne, subject)
                : Format(plural ? CausalityText.CauseReferencesRemovedFromOther : CausalityText.CauseReferencesRemovedFromOne, subject, effectName);
            parts.Add(new CausalityCauseSentencePart(sentence));
            return;
        }

        // Singular "they" throughout: a cause is as often a person as a group, and the chain must not
        // guess a pronoun for either.
        // The sentence stops where the highlighted attribute name begins; the one space between them is layout.
        var removedFrom = string.IsNullOrWhiteSpace(effectName)
            ? Format(plural ? CausalityText.CauseAttributeRemovedOther : CausalityText.CauseAttributeRemovedOne, subject)
            : Format(plural ? CausalityText.CauseAttributeRemovedOwnerOther : CausalityText.CauseAttributeRemovedOwnerOne, subject, effectName);
        parts.Add(new CausalityCauseSentencePart(removedFrom + " "));
        parts.Add(new CausalityCauseSentencePart(cohort.AttributeName, IsAttributeName: true));
    }
}
