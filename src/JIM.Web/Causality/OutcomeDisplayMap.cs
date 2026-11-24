// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Transactional;
using JIM.Web.Localisation;
using JIM.Web.Resources;
using MudBlazor;

namespace JIM.Web.Causality;

/// <summary>
/// The single source of truth for how every <see cref="ActivityRunProfileExecutionItemSyncOutcomeType"/>
/// value is displayed: label, tone and icon. The Helpers outcome-type methods delegate here so existing
/// callers keep identical behaviour, and the causality visualisation builds on the same mapping.
///
/// Every label speaks the portal's own vocabulary: "Metaverse Object" and "Connected System Object" in
/// full where a noun is needed, never "Identity", "record", "account", "MVO" or "CSO". JIM does not
/// rename its product nouns, and it does not carry a second, more technical name beside them either;
/// one label per outcome is the whole of it. <c>OutcomeDisplayMapVocabularyTests</c> (JIM.Web.Tests)
/// asserts every label, sentence form and speculative preview label in this map against that rule.
/// </summary>
public static class OutcomeDisplayMap
{
    // Each entry is a factory so the text is read from the resource files on every call, in the viewer's language.
    private static Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, Func<OutcomeDisplay>> Map { get; } = new()
    {
        // Import outcomes
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded] =
            () => new OutcomeDisplay(CausalityText.OutcomeCsoAddedLabel, CausalityTone.Success, Icons.Material.Filled.Add),
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated] =
            () => new OutcomeDisplay(CausalityText.OutcomeCsoUpdatedLabel, CausalityTone.Info, Icons.Material.Filled.Edit),
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted] =
            () => new OutcomeDisplay(CausalityText.OutcomeCsoDeletedLabel, CausalityTone.Error, Icons.Material.Filled.Delete,
                CausalityText.OutcomeCsoDeletedClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected] =
            () => new OutcomeDisplay(CausalityText.OutcomeDeletionDetectedLabel, CausalityTone.Warning, Icons.Material.Filled.RemoveCircle),

        // Import outcomes; confirming import (export confirmation)
        [ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed] =
            () => new OutcomeDisplay(CausalityText.OutcomeExportConfirmedLabel, CausalityTone.Success, Icons.Material.Filled.CheckCircle),
        [ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed] =
            () => new OutcomeDisplay(CausalityText.OutcomeExportFailedLabel, CausalityTone.Error, Icons.Material.Filled.Cancel),

        // Sync outcomes; inbound
        [ActivityRunProfileExecutionItemSyncOutcomeType.Projected] =
            () => new OutcomeDisplay(CausalityText.OutcomeProjectedLabel, CausalityTone.Primary, Icons.Material.Filled.AirlineStops,
                CausalityText.OutcomeProjectedClause,
                SpeculativeLabel: CausalityText.OutcomeProjectedSpeculative),
        [ActivityRunProfileExecutionItemSyncOutcomeType.Joined] =
            () => new OutcomeDisplay(CausalityText.OutcomeJoinedLabel, CausalityTone.Secondary, Icons.Material.Filled.Link,
                CausalityText.OutcomeJoinedClause,
                SpeculativeLabel: CausalityText.OutcomeJoinedSpeculative),
        [ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow] =
            () => new OutcomeDisplay(CausalityText.OutcomeAttributeFlowLabel, CausalityTone.Secondary, Icons.Material.Filled.SyncAlt,
                CausalityText.OutcomeAttributeFlowClause,
                SpeculativeLabel: CausalityText.OutcomeAttributeFlowSpeculative),
        [ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected] =
            () => new OutcomeDisplay(CausalityText.OutcomeDisconnectedLabel, CausalityTone.Warning, Icons.Material.Filled.LinkOff),
        [ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope] =
            () => new OutcomeDisplay(CausalityText.OutcomeDisconnectedOutOfScopeLabel, CausalityTone.Warning, Icons.Material.Filled.FilterAltOff,
                SpeculativeLabel: CausalityText.OutcomeDisconnectedOutOfScopeSpeculative),
        // The retaining sibling of DisconnectedOutOfScope (#1649): same scope exit, join kept. Info tone and the
        // FilterAlt icon match what the portal already shows for this change type on the Activity list chip, so
        // the two surfaces agree; nothing is destroyed or recalled, so it is not a Warning.
        [ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin] =
            () => new OutcomeDisplay(CausalityText.OutcomeOutOfScopeRetainJoinLabel, CausalityTone.Info, Icons.Material.Filled.FilterAlt,
                CausalityText.OutcomeOutOfScopeRetainJoinClause,
                SpeculativeLabel: CausalityText.OutcomeOutOfScopeRetainJoinSpeculative),
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted] =
            () => new OutcomeDisplay(CausalityText.OutcomeMvoDeletedLabel, CausalityTone.Error, Icons.Material.Filled.PersonRemove,
                SpeculativeLabel: CausalityText.OutcomeMvoDeletedSpeculative),
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled] =
            () => new OutcomeDisplay(CausalityText.OutcomeMvoDeletionScheduledLabel, CausalityTone.Warning, Icons.Material.Filled.HourglassBottom,
                SpeculativeLabel: CausalityText.OutcomeMvoDeletionScheduledSpeculative),
        // The survival counterpart of MvoDeletionScheduled above (#1620): a rejoin undid the disconnection
        // that scheduled it, so the object lives on. Success-toned (the object survived) and the
        // "hourglass disabled" icon reads as the same hourglass, stopped, rather than a new symbol.
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled] =
            () => new OutcomeDisplay(CausalityText.OutcomeMvoDeletionCancelledLabel, CausalityTone.Success, Icons.Material.Filled.HourglassDisabled),
        [ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection] =
            () => new OutcomeDisplay(CausalityText.OutcomeDriftCorrectionLabel, CausalityTone.Warning, Icons.Material.Filled.CompareArrows,
                CausalityText.OutcomeDriftCorrectionClause,
                SpeculativeLabel: CausalityText.OutcomeDriftCorrectionSpeculative),

        // Sync outcomes; outbound (Pending Export creation during sync)
        [ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned] =
            () => new OutcomeDisplay(CausalityText.OutcomeProvisionedLabel, CausalityTone.Primary, Icons.Material.Filled.SwitchAccessShortcut,
                CausalityText.OutcomeProvisionedClause,
                SpeculativeLabel: CausalityText.OutcomeProvisionedSpeculative),
        [ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated] =
            () => new OutcomeDisplay(CausalityText.OutcomePendingExportCreatedLabel, CausalityTone.Info, Icons.Material.Filled.Schedule,
                SpeculativeLabel: CausalityText.OutcomePendingExportCreatedSpeculative),
        // The delete-flavoured staging outcome. Error-toned and named for what it will do, because "Export
        // queued" over a single distinguishedName row read as an attribute update rather than an account
        // being removed. AutoDelete (a clock inside a bin) is the queued form of Deprovisioned's CloudOff.
        [ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued] =
            () => new OutcomeDisplay(CausalityText.OutcomeDeprovisionQueuedLabel, CausalityTone.Error, Icons.Material.Filled.AutoDelete,
                SpeculativeLabel: CausalityText.OutcomeDeprovisionQueuedSpeculative),
        // Provisioning withdrawn before it was ever exported: nothing exists in the target
        // system, so nothing was deleted there, which is why this is Warning rather than DeprovisionQueued's
        // Error: a queued deprovision destroys a real (or possibly real) account, this destroys nothing.
        // CancelScheduleSend (a send action struck through) reads as "this was going out, and now it is not",
        // distinct from AutoDelete's "this will be removed".
        [ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled] =
            () => new OutcomeDisplay(CausalityText.OutcomeProvisioningCancelledLabel, CausalityTone.Warning, Icons.Material.Filled.CancelScheduleSend,
                CausalityText.OutcomeProvisioningCancelledClause,
                SpeculativeLabel: CausalityText.OutcomeProvisioningCancelledSpeculative),
        // The Disconnect sibling of DeprovisionQueued (#1966): the object in the target system is left in place but
        // no longer managed, so Warning rather than DeprovisionQueued's Error, and the same link-off icon as the
        // item's own Disconnected, which it is deliberately not (that names the item's own object).
        [ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected] =
            () => new OutcomeDisplay(CausalityText.OutcomeTargetDisconnectedLabel, CausalityTone.Warning, Icons.Material.Filled.LinkOff,
                SpeculativeLabel: CausalityText.OutcomeTargetDisconnectedSpeculative),
        // Changes queued for a target system that it already holds, withdrawn rather than exported (#2001). Info: the
        // target keeps the value it has and nothing is sent, so this explains why an expected update never went out
        // rather than warning of anything lost. PlaylistRemove reads as items taken off a queue.
        // Another export rule took the account over when the object left the scope of the rule that managed it (#2822).
        // Info: the account stays as it was, so nothing is lost; it explains why a scope exit deprovisioned nothing.
        [ActivityRunProfileExecutionItemSyncOutcomeType.HandedToAnotherExportRule] =
            () => new OutcomeDisplay(CausalityText.OutcomeHandedToAnotherExportRuleLabel, CausalityTone.Info, Icons.Material.Filled.SwapHoriz,
                CausalityText.OutcomeHandedToAnotherExportRuleClause,
                SpeculativeLabel: CausalityText.OutcomeHandedToAnotherExportRuleSpeculative),
        [ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportChangesWithdrawn] =
            () => new OutcomeDisplay(CausalityText.OutcomePendingExportChangesWithdrawnLabel, CausalityTone.Info, Icons.Material.Filled.PlaylistRemove,
                CausalityText.OutcomePendingExportChangesWithdrawnClause,
                SpeculativeLabel: CausalityText.OutcomePendingExportChangesWithdrawnSpeculative),

        // Export execution outcomes
        [ActivityRunProfileExecutionItemSyncOutcomeType.Exported] =
            () => new OutcomeDisplay(CausalityText.OutcomeExportedLabel, CausalityTone.Info, Icons.Material.Filled.Output),
        [ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned] =
            () => new OutcomeDisplay(CausalityText.OutcomeDeprovisionedLabel, CausalityTone.Error, Icons.Material.Filled.CloudOff),

        // Attribute priority (#91): a deliberate blank assertion, and a value cleared with no
        // surviving contributor; both worth drawing the eye to.
        [ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull] =
            () => new OutcomeDisplay(CausalityText.OutcomeAssertedNullLabel, CausalityTone.Warning, Icons.Material.Filled.DoNotDisturbOn),
        [ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor] =
            () => new OutcomeDisplay(CausalityText.OutcomeNoContributorLabel, CausalityTone.Warning, Icons.Material.Filled.HighlightOff,
                CausalityText.OutcomeNoContributorClause,
                SpeculativeLabel: CausalityText.OutcomeNoContributorSpeculative),

        // #1570: values kept as last known state because no import source remains to assert the object;
        // the preserving counterpart of NoContributor above, and just as worth drawing the eye to.
        [ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved] =
            () => new OutcomeDisplay(CausalityText.OutcomeValuesPreservedLabel, CausalityTone.Warning, Icons.Material.Filled.AcUnit,
                SpeculativeLabel: CausalityText.OutcomeValuesPreservedSpeculative),

        // Configuration change preview (#827): transitions a proposed configuration would cause. Nothing writes
        // these during a run, so they never reach an Activity's causality views; they are mapped because this is
        // the one place an outcome type's vocabulary lives, and a preview surface rendering through it should
        // inherit the same label rather than grow a second, drifting one. Conditional tone throughout: a preview
        // states what would happen, so the tone marks the consequence's weight, not a failure that has occurred.
        //
        // These labels are held to a stricter standard than the run outcomes above, because these are the ones an
        // administrator reads while deciding whether to save: present tense, no "Would" prefix. The panel heading
        // already establishes that nothing has happened yet, so repeating it per row spent a column's width saying
        // nothing.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldFallInScopeLabel, CausalityTone.Info, Icons.Material.Filled.FilterAlt,
                CausalityText.OutcomeWouldFallInScopeClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldFallOutOfScopeLabel, CausalityTone.Warning, Icons.Material.Filled.FilterAltOff,
                CausalityText.OutcomeWouldFallOutOfScopeClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldBecomeDeletionEligibleLabel, CausalityTone.Error, Icons.Material.Filled.DeleteOutline,
                CausalityText.OutcomeWouldBecomeDeletionEligibleClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldCeaseToBeDeletionEligibleLabel, CausalityTone.Success, Icons.Material.Filled.RestoreFromTrash,
                CausalityText.OutcomeWouldCeaseToBeDeletionEligibleClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldChangeDeletionEligibleDateLabel, CausalityTone.Warning, Icons.Material.Filled.EditCalendar,
                CausalityText.OutcomeWouldChangeDeletionEligibleDateClause),
        // Named for the disconnection rather than for the scope change, which is the fact that distinguishes it
        // from WouldFallOutOfScope above; that the object also leaves scope is carried by the delta's old and new
        // values, so the label does not have to spend itself restating it.
        //
        // Warning rather than Error, matching its own run-time outcomes (Disconnected, DisconnectedOutOfScope). A
        // disconnection recalls the attribute values the object contributed, which is serious, but re-selecting the
        // container and importing puts it back; a deletion past its grace period does not come back. Toning both
        // the same left the preview's severity column encoding nothing on the change that most needs it.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldDisconnectFromMetaverseObjectLabel, CausalityTone.Warning, Icons.Material.Filled.LinkOff,
                CausalityText.OutcomeWouldDisconnectFromMetaverseObjectClause),
        // The destructive-toggle preview's fates (#1115). Error tone on the delete because a deletion in the
        // target system past its recycle window does not come back; Success on the join kept because nothing is
        // destroyed; Warning on the exposure change because nothing happens on save, but the standing consequence
        // of every future scope exit changes.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldStageDeleteExportLabel, CausalityTone.Error, Icons.Material.Filled.AutoDelete,
                CausalityText.OutcomeWouldStageDeleteExportClause),
        // Warning rather than Error: the account stays, disabled, and a rehire can be re-enabled (#2085).
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDisableExport] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldStageDisableExportLabel, CausalityTone.Warning, Icons.Material.Filled.Block,
                CausalityText.OutcomeWouldStageDisableExportClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldRemainJoinedLabel, CausalityTone.Success, Icons.Material.Filled.Link,
                CausalityText.OutcomeWouldRemainJoinedClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldChangeDeprovisionActionLabel, CausalityTone.Warning, Icons.Material.Filled.SwapHoriz,
                CausalityText.OutcomeWouldChangeDeprovisionActionClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldFailAttributeFlowLabel, CausalityTone.Error, Icons.Material.Filled.RuleFolder,
                CausalityText.OutcomeWouldFailAttributeFlowClause),
        // The Object Matching preview's fates (#1457). Error tone on joining a different Metaverse Object and on
        // projecting instead of joining, because both are identity corruption that nothing reports at run time:
        // one merges an account into the wrong identity, the other splits one identity into two. Ambiguity is a
        // Warning because the next synchronisation refuses the object loudly rather than joining it wrongly.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldJoinDifferentMetaverseObjectLabel, CausalityTone.Error, Icons.Material.Filled.SwapHoriz,
                CausalityText.OutcomeWouldJoinDifferentMetaverseObjectClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldJoinInsteadOfProjectLabel, CausalityTone.Success, Icons.Material.Filled.Link,
                CausalityText.OutcomeWouldJoinInsteadOfProjectClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldProjectInsteadOfJoinLabel, CausalityTone.Error, Icons.Material.Filled.CallSplit,
                CausalityText.OutcomeWouldProjectInsteadOfJoinClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldMatchAmbiguouslyLabel, CausalityTone.Warning, Icons.Material.Filled.QuestionMark,
                CausalityText.OutcomeWouldMatchAmbiguouslyClause),
        // The behaviour-toggle preview's fates (#1462). Warning rather than Error on all three: nothing existing
        // is destroyed by any of them, and what they cost is a Metaverse Object, a Connected System Object or a
        // correction that never arrives, which is a different kind of harm from a deletion.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldStopProjectingLabel, CausalityTone.Warning, Icons.Material.Filled.PersonOff,
                CausalityText.OutcomeWouldStopProjectingClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldStopProvisioningLabel, CausalityTone.Warning, Icons.Material.Filled.NoAccounts,
                CausalityText.OutcomeWouldStopProvisioningClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldStopCorrectingDriftLabel, CausalityTone.Warning, Icons.Material.Filled.SyncDisabled,
                CausalityText.OutcomeWouldStopCorrectingDriftClause),
        // The schema selection preview's fates (#1475). Warning on the freeze rather than Error, because nothing is
        // destroyed and nothing is disconnected; what happens is that values stop tracking their source while still
        // being contributed, which is a slower harm and an easier one to miss.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldStopBeingImportedLabel, CausalityTone.Warning, Icons.Material.Filled.CloudOff,
                CausalityText.OutcomeWouldStopBeingImportedClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldResumeBeingImportedLabel, CausalityTone.Success, Icons.Material.Filled.CloudSync,
                CausalityText.OutcomeWouldResumeBeingImportedClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldWithdrawContributedValuesLabel, CausalityTone.Warning, Icons.Material.Filled.Undo,
                CausalityText.OutcomeWouldWithdrawContributedValuesClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldRetainContributedValuesLabel, CausalityTone.Info, Icons.Material.Filled.Inventory2,
                CausalityText.OutcomeWouldRetainContributedValuesClause),
        // The export-side scope pair. These exist because the import-side pair above were the only scope
        // transitions an export rule's scope preview could emit, and "Leaves import scope" against a Metaverse
        // Object leaving an export rule names a direction the rule does not have. Info on both: an identity the
        // rule stops standing over with nothing in the target to remove, and one it starts flowing to, are the
        // benign ends of a scope change. The costly ends have transitions of their own (WouldStageDeleteExport,
        // WouldDisconnectFromMetaverseObject, WouldStopProvisioning and Provisioned), which is what lets these
        // two be read as "nothing is created or removed" without qualification.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldLeaveExportScopeLabel, CausalityTone.Info, Icons.Material.Filled.FilterAltOff,
                CausalityText.OutcomeWouldLeaveExportScopeClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldEnterExportScopeLabel, CausalityTone.Info, Icons.Material.Filled.FilterAlt,
                CausalityText.OutcomeWouldEnterExportScopeClause),

        // The Connected System deletion impact preview (#134). Warning on a takeover that changes the value, matching
        // NoContributor beside it: the attribute stays populated but now says something different, and that is what an
        // administrator migrating between sources needs to read. Secondary, the lowest weight, on a takeover with an
        // identical value: only the source changes, nothing is exported, and in a migration these outnumber every other
        // row, so they must sort below what matters. Info on the corrective update: the consequence downstream of the
        // two rows above, already accounted for by them.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldTakeOverContributedValueLabel, CausalityTone.Warning, Icons.Material.Filled.ChangeCircle,
                CausalityText.OutcomeWouldTakeOverContributedValueClause),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldTakeOverSameValueLabel, CausalityTone.Secondary, Icons.Material.Filled.CompareArrows,
                CausalityText.OutcomeWouldTakeOverSameValueClause, StatedInVerdict: false),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldStageUpdateExportLabel, CausalityTone.Info, Icons.Material.Filled.CloudUpload,
                CausalityText.OutcomeWouldStageUpdateExportClause),

        // The Full Synchronisation preview (#1530). An object left as it is is the reassurance, not a change: the verdict
        // does not state it (the panel says how many in a line of its own) and it sorts after everything that matters.
        // A failure no more specific transition names is an Error, as WouldFailAttributeFlow is.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldNotChange] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldNotChangeLabel, CausalityTone.Success, Icons.Material.Filled.CheckCircleOutline,
                CausalityText.OutcomeWouldNotChangeClause, StatedInVerdict: false),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFail] =
            () => new OutcomeDisplay(CausalityText.OutcomeWouldFailLabel, CausalityTone.Error, Icons.Material.Filled.ErrorOutline,
                CausalityText.OutcomeWouldFailClause),

        // Unique Value Generation (#242). Primary on Assigned: a positive act JIM took, matching the tone
        // Projected and Provisioned use for their own "JIM did this" outcomes. Fingerprint reads as
        // "identifier issued" without borrowing the Add glyph every creation outcome already uses.
        [ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned] =
            () => new OutcomeDisplay(CausalityText.OutcomeGeneratedValueAssignedLabel, CausalityTone.Primary, Icons.Material.Filled.Fingerprint),
        // Retired (#242, product-owner decision 2026-10-01): nothing records this outcome any more. Kept so an
        // item recorded before the decision (development builds only; the feature was never released) still
        // renders, and because the enum member itself must stay to preserve the persisted ordinals after it.
        [ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAdopted] =
            () => new OutcomeDisplay(CausalityText.OutcomeGeneratedValueAdoptedLabel, CausalityTone.Info, Icons.Material.Filled.MoveToInbox),
        // Warning, matching NoContributor and ValuesPreserved: a value leaving live use is worth noticing,
        // even though nothing failed. Archive reads as "put away", not "deleted".
        [ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRetired] =
            () => new OutcomeDisplay(CausalityText.OutcomeGeneratedValueRetiredLabel, CausalityTone.Warning, Icons.Material.Filled.Archive),
        // Warning, like DriftCorrection: a correction, not a failure, but one an administrator should read.
        // PublishedWithChanges (a document icon with a revision mark) reads as "this value was revised",
        // distinct from Archive's "put away" and Fingerprint's "newly issued".
        [ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated] =
            () => new OutcomeDisplay(CausalityText.OutcomeGeneratedValueRemediatedLabel, CausalityTone.Warning, Icons.Material.Filled.PublishedWithChanges),

        // Generated Value Forms (#2023). Warning on Waiting, like ValuesPreserved: nothing failed, but an object is
        // still without its value, which an administrator should read. Info on a passed-over candidate: JIM did its job.
        [ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueWaiting] =
            () => new OutcomeDisplay(CausalityText.OutcomeGeneratedValueWaitingLabel, CausalityTone.Warning, Icons.Material.Filled.HourglassTop),
        [ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueCandidatesSkipped] =
            () => new OutcomeDisplay(CausalityText.OutcomeGeneratedValueCandidatesSkippedLabel, CausalityTone.Info, Icons.Material.Filled.SkipNext)
    };

    /// <summary>
    /// Gets the display mapping for a sync outcome type. Every enum value is mapped; an unmapped
    /// value (a future enum addition without a mapping here) falls back to the enum name so the UI
    /// still renders rather than throwing.
    /// </summary>
    public static OutcomeDisplay Get(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return Map.TryGetValue(outcomeType, out var factory)
            ? factory()
            : new OutcomeDisplay(outcomeType.ForDisplay(), CausalityTone.Secondary, Icons.Material.Filled.Circle);
    }

    /// <summary>
    /// The conditional-mood label a Sync Preview's speculative tree substitutes for the outcome's plain
    /// label (#1519, D-S1/D-S9), or null for an outcome type the preview engine never emits. See
    /// <see cref="OutcomeDisplay.SpeculativeLabel"/>; <c>SyncPreviewSpeculativeLabelCompletenessTests</c>
    /// asserts every type <c>SyncPreviewServer</c> can produce has a non-null entry here.
    /// </summary>
    public static string? GetSpeculativeLabel(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return Map.TryGetValue(outcomeType, out var factory) ? factory().SpeculativeLabel : null;
    }

    /// <summary>
    /// The decision-aware display for an Exported outcome (#1495): what the export actually did,
    /// keyed on the queueing edge's reason code because that is the only durable copy of the
    /// create/update/delete decision (the Pending Export row that knew it is deleted on execution,
    /// and the item's change snapshot records Exported for creates and updates alike). Falls back
    /// to the bare Exported mapping for any other code, which is the honest label for pre-edge
    /// history.
    /// </summary>
    public static OutcomeDisplay GetExportDecision(CausalReasonCode reasonCode)
    {
        return reasonCode switch
        {
            CausalReasonCode.ExportCreateStaged =>
                new OutcomeDisplay(CausalityText.OutcomeCsoCreatedLabel, CausalityTone.Success, Icons.Material.Filled.AddCircle),
            CausalReasonCode.ExportUpdateStaged =>
                new OutcomeDisplay(CausalityText.OutcomeChangesAppliedLabel, CausalityTone.Info, Icons.Material.Filled.Output),
            CausalReasonCode.ExportDeleteStaged =>
                new OutcomeDisplay(CausalityText.OutcomeCsoDeletedLabel, CausalityTone.Error, Icons.Material.Filled.Delete),
            CausalReasonCode.ExportDisableStaged =>
                new OutcomeDisplay(CausalityText.OutcomeCsoDisabledLabel, CausalityTone.Warning, Icons.Material.Filled.Block),
            _ => Get(ActivityRunProfileExecutionItemSyncOutcomeType.Exported)
        };
    }

    /// <summary>
    /// One tone and one icon per operation verb (#1495 follow-up refinement), shared by
    /// <see cref="GetHopOperation"/>, <see cref="GetEventOperation"/> and <see cref="GetQueueingDecisionOperation"/>.
    /// The chip is the operation vocabulary a reader scans a column on: Created is always
    /// <see cref="CausalityTone.Success"/> and <see cref="Icons.Material.Filled.Add"/>, Updated is always
    /// <see cref="CausalityTone.Info"/> and <see cref="Icons.Material.Filled.Edit"/>, Deleted is always
    /// <see cref="CausalityTone.Error"/> and <see cref="Icons.Material.Filled.Delete"/>, Joined is always
    /// <see cref="CausalityTone.Secondary"/> and <see cref="Icons.Material.Filled.Link"/>, regardless of
    /// which outcome type or edge produced the chip. Colour-on-colour previously varied per outcome
    /// (Primary for a projection, a distinct "circled" icon for a provision, PersonRemove for a deletion),
    /// which meant scanning a column for "what happened" needed reading every chip's icon rather than
    /// matching its colour. The outcome-specific icon and wording are not lost: the card's own title, the
    /// summary band's pills and the sentence beside a chain card (all built from the unchanged <see cref="Map"/>
    /// dictionary above) still carry them; only the chip itself is now one look per verb.
    /// </summary>
    /// <remarks>
    /// Checked in the order the hazard on <see cref="CausalChainCohort.MetaverseChangeType"/> requires:
    /// that field, then <see cref="CausalChainCohort.SourceImportChangeType"/>, then the edge type. Both
    /// derived-cohort fields carry no <see cref="CausalEdgeType"/> of their own, so a derived cohort's
    /// default <see cref="CausalEdgeType"/> (0, <see cref="CausalEdgeType.MetaverseObjectDeletionCausedDeprovision"/>)
    /// would otherwise be read as a Metaverse Object deletion that never happened.
    /// </remarks>
    public static OutcomeDisplay? GetHopOperation(CausalChainCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);

        if (cohort.MetaverseChangeType is { } metaverseChangeType)
        {
            return metaverseChangeType switch
            {
                ObjectChangeType.Projected =>
                    new OutcomeDisplay(CausalityText.OperationCreated, CausalityTone.Success, Icons.Material.Filled.Add),
                ObjectChangeType.Joined =>
                    new OutcomeDisplay(CausalityText.OperationJoined, CausalityTone.Secondary, Icons.Material.Filled.Link),
                ObjectChangeType.Created =>
                    new OutcomeDisplay(CausalityText.OperationCreated, CausalityTone.Success, Icons.Material.Filled.Add),
                _ => null
            };
        }

        if (cohort.SourceImportChangeType is { } sourceImportChangeType)
        {
            return sourceImportChangeType switch
            {
                ObjectChangeType.Added =>
                    new OutcomeDisplay(CausalityText.OperationCreated, CausalityTone.Success, Icons.Material.Filled.Add),
                ObjectChangeType.Updated =>
                    new OutcomeDisplay(CausalityText.OperationUpdated, CausalityTone.Info, Icons.Material.Filled.Edit),
                ObjectChangeType.Deleted =>
                    new OutcomeDisplay(CausalityText.OperationDeleted, CausalityTone.Error, Icons.Material.Filled.Delete),
                _ => null
            };
        }

        return cohort.EdgeType switch
        {
            CausalEdgeType.PendingExportQueueingCausedExportExecution => GetQueueingDecisionOperation(cohort.ReasonCode),
            CausalEdgeType.MetaverseObjectDeletionCausedDeprovision or CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval =>
                new OutcomeDisplay(CausalityText.OperationDeleted, CausalityTone.Error, Icons.Material.Filled.Delete),
            // Unique Value Generation (#242): the rejection revised the value, the same verb an attribute
            // update anywhere else in this map carries (AttributeFlow, DriftCorrection, ExportUpdateStaged).
            CausalEdgeType.ExportRejectionCausedGeneratedValueRevision =>
                new OutcomeDisplay(CausalityText.OperationUpdated, CausalityTone.Info, Icons.Material.Filled.Edit),
            // ExportCausedImportConfirmation, MetaverseObjectDeletionScheduleCausedDeletion and any seam this map
            // does not know fall through here: a confirmation is not itself an object operation, a scheduled
            // deletion is a state change (as its MvoDeletionScheduled event is), and an unknown edge is never guessed.
            _ => null
        };
    }

    /// <summary>
    /// The tone-tinted operation chip a this-run event card carries (#1495 follow-up): the same
    /// vocabulary as <see cref="GetHopOperation"/>, keyed on the outcome type directly rather than on a
    /// chain cohort, since a this-run event has no cohort of its own.
    /// </summary>
    /// <param name="outcomeType">The event's underlying sync outcome type.</param>
    /// <param name="exportReasonCode">
    /// For an <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.Exported"/> outcome, the staged
    /// change's reason code where the causal chain resolved one (the same lookup
    /// <see cref="CausalityModelBuilder"/> uses for the outcome's own title); null where the chain did
    /// not resolve one, or where it is not an Exported outcome. Ignored for every other outcome type.
    /// </param>
    /// <param name="stagedChangeType">
    /// For a <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated"/> outcome,
    /// the kind of change staged (<see cref="ActivityRunProfileExecutionItemSyncOutcome.StagedChangeType"/>),
    /// recorded at staging time so this outcome type, which collapses Create and Update, can still state
    /// which one it was. Null for an outcome recorded before this was captured. Ignored for every other
    /// outcome type: DeprovisionQueued already reads Deleted from its own outcome type and does not need it.
    /// </param>
    public static OutcomeDisplay? GetEventOperation(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType,
        CausalReasonCode? exportReasonCode = null,
        PendingExportChangeType? stagedChangeType = null)
    {
        if (outcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported)
            return exportReasonCode is { } reasonCode ? GetQueueingDecisionOperation(reasonCode) : null;

        if (outcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated)
        {
            return stagedChangeType switch
            {
                PendingExportChangeType.Create => GetQueueingDecisionOperation(CausalReasonCode.ExportCreateStaged),
                PendingExportChangeType.Update => GetQueueingDecisionOperation(CausalReasonCode.ExportUpdateStaged),
                PendingExportChangeType.Delete => GetQueueingDecisionOperation(CausalReasonCode.ExportDeleteStaged),
                PendingExportChangeType.Disable => GetQueueingDecisionOperation(CausalReasonCode.ExportDisableStaged),
                // Null covers outcomes recorded before this was captured: honestly "unknown kind"
                // rather than a guess.
                _ => null
            };
        }

        // A deprovisioning that disabled the object rather than deleting it says so (#2085).
        if (stagedChangeType == PendingExportChangeType.Disable &&
            outcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned or ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued)
            return GetQueueingDecisionOperation(CausalReasonCode.ExportDisableStaged);

        return outcomeType switch
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected =>
                new OutcomeDisplay(CausalityText.OperationCreated, CausalityTone.Success, Icons.Material.Filled.Add),
            ActivityRunProfileExecutionItemSyncOutcomeType.Joined =>
                new OutcomeDisplay(CausalityText.OperationJoined, CausalityTone.Secondary, Icons.Material.Filled.Link),
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded =>
                new OutcomeDisplay(CausalityText.OperationCreated, CausalityTone.Success, Icons.Material.Filled.Add),
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated =>
                new OutcomeDisplay(CausalityText.OperationUpdated, CausalityTone.Info, Icons.Material.Filled.Edit),
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted =>
                new OutcomeDisplay(CausalityText.OperationDeleted, CausalityTone.Error, Icons.Material.Filled.Delete),
            // The label matches the map's own AttributeFlow/DriftCorrection titles above so the chip
            // never disagrees with the card's own; both are honestly "an update", not a create or a delete.
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow =>
                new OutcomeDisplay(CausalityText.OperationUpdated, CausalityTone.Info, Icons.Material.Filled.Edit),
            ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection =>
                new OutcomeDisplay(CausalityText.OperationUpdated, CausalityTone.Info, Icons.Material.Filled.Edit),
            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned =>
                new OutcomeDisplay(CausalityText.OperationCreated, CausalityTone.Success, Icons.Material.Filled.Add),
            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted =>
                new OutcomeDisplay(CausalityText.OperationDeleted, CausalityTone.Error, Icons.Material.Filled.Delete),
            ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned =>
                new OutcomeDisplay(CausalityText.OperationDeleted, CausalityTone.Error, Icons.Material.Filled.Delete),
            // A queued deprovision is a staged delete; reuse the chain's own "Export Staged (Delete)"
            // chip rather than inventing a second vocabulary for the same staged kind.
            ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued =>
                GetQueueingDecisionOperation(CausalReasonCode.ExportDeleteStaged),
            // Every Would* preview (nothing executed), ExportConfirmed/ExportFailed (confirming or
            // failing an export is not itself an object operation),
            // DeletionDetected/Disconnected/DisconnectedOutOfScope/OutOfScopeRetainJoin/MvoDeletionScheduled/
            // MvoDeletionCancelled (a state change, not an operation this map states an icon for), AssertedNull/NoContributor
            // (attribute-priority housekeeping, not an object operation), ProvisioningCancelled (nothing was
            // ever created, updated or deleted anywhere: the whole point of a cancellation is that no
            // operation reached the target system), PendingExportChangesWithdrawn (likewise: what it withdrew is
            // exactly what will not reach the target system) and anything unmapped all fall through here: null
            // rather than a guess. PendingExportCreated never reaches this switch; it is handled above.
            _ => null
        };
    }

    /// <summary>
    /// The operation a Pending Export queueing edge's reason code states, shared between
    /// <see cref="GetHopOperation"/> (keyed on a chain cohort's edge) and <see cref="GetEventOperation"/>
    /// (keyed on the outcome type directly, for DeprovisionQueued and a decision-resolved Exported).
    /// </summary>
    private static OutcomeDisplay? GetQueueingDecisionOperation(CausalReasonCode reasonCode)
    {
        return reasonCode switch
        {
            CausalReasonCode.ExportCreateStaged =>
                new OutcomeDisplay(CausalityText.OperationCreated, CausalityTone.Success, Icons.Material.Filled.Add),
            CausalReasonCode.ExportUpdateStaged =>
                new OutcomeDisplay(CausalityText.OperationUpdated, CausalityTone.Info, Icons.Material.Filled.Edit),
            CausalReasonCode.ExportDeleteStaged =>
                new OutcomeDisplay(CausalityText.OperationDeleted, CausalityTone.Error, Icons.Material.Filled.Delete),
            CausalReasonCode.ExportDisableStaged =>
                new OutcomeDisplay(CausalityText.OperationDisabled, CausalityTone.Warning, Icons.Material.Filled.Block),
            // NotSet covers edges written before the reason codes existed: guessing create/update/delete
            // for that history would be worse than stating nothing.
            _ => null
        };
    }

    /// <summary>
    /// Whether an outcome's Lineage card head (the icon tile and title, <c>.evt-head</c>) is redundant
    /// because two other things on the panel already state it (#1495 second follow-up): the join label
    /// printed between the two columns the card sits between (PROJECTED / PROVISIONED / JOINED), and the
    /// card's own operation chip (<see cref="GetEventOperation"/>), which states the same verb again in
    /// the same first-child position the head would otherwise occupy. Stacking a third restatement (the
    /// head's own label) added a title a reader had already read twice by the time they reached it.
    /// </summary>
    /// <remarks>
    /// True for <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.Projected"/>,
    /// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.Joined"/> and
    /// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned"/> (whose Lineage join
    /// label states the same verb a third time), and for
    /// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.Exported"/>: its decision-specific
    /// titles ("Connected System Object created", "Changes applied", "Connected System Object deleted") say
    /// nothing the chip's own Created / Updated / Deleted does not, so once the chip exists the title is the
    /// restatement. Exported's chip only renders where the export's queueing decision resolved, and an item
    /// exported before causal capture existed resolves none; that no-chip case is exactly what
    /// <c>CausalityEventCard.HideTitle</c>'s misuse guard covers, keeping the bare "Exported" head rendering
    /// rather than leaving the card naming nothing, so this method stays a function of the outcome type alone.
    /// </remarks>
    public static bool IsTitleSubsumedByOperation(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return outcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Projected
            or ActivityRunProfileExecutionItemSyncOutcomeType.Joined
            or ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned
            or ActivityRunProfileExecutionItemSyncOutcomeType.Exported;
    }

    /// <summary>
    /// Maps a causality tone onto the corresponding MudBlazor palette colour.
    /// </summary>
    public static Color ToMudBlazorColor(CausalityTone tone)
    {
        return tone switch
        {
            CausalityTone.Primary => Color.Primary,
            CausalityTone.Success => Color.Success,
            CausalityTone.Info => Color.Info,
            CausalityTone.Warning => Color.Warning,
            CausalityTone.Error => Color.Error,
            CausalityTone.Secondary => Color.Secondary,
            _ => Color.Default
        };
    }
}
