// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Transactional;
using MudBlazor;

namespace JIM.Web.Causality;

/// <summary>
/// The single source of truth for how every <see cref="ActivityRunProfileExecutionItemSyncOutcomeType"/>
/// value is displayed: plain-language label, technical label, tone and icon. The Helpers outcome-type
/// methods delegate here so existing callers keep identical behaviour, and the causality visualisation
/// builds on the same mapping.
/// </summary>
public static class OutcomeDisplayMap
{
    private static readonly Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, OutcomeDisplay> Map = new()
    {
        // Import outcomes
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded] =
            new OutcomeDisplay("Record added", "CSO Added", CausalityTone.Success, Icons.Material.Filled.Add),
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated] =
            new OutcomeDisplay("Record updated", "CSO Updated", CausalityTone.Info, Icons.Material.Filled.Edit),
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted] =
            new OutcomeDisplay("Record deleted", "CSO Deleted", CausalityTone.Error, Icons.Material.Filled.Delete),
        [ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected] =
            new OutcomeDisplay("Deletion detected", "CSO Deletion Detected", CausalityTone.Warning, Icons.Material.Filled.RemoveCircle),

        // Import outcomes; confirming import (export confirmation)
        [ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed] =
            new OutcomeDisplay("Export confirmed", "CSO Export Confirmed", CausalityTone.Success, Icons.Material.Filled.CheckCircle),
        [ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed] =
            new OutcomeDisplay("Export failed", "CSO Export Failed", CausalityTone.Error, Icons.Material.Filled.Cancel),

        // Sync outcomes; inbound
        [ActivityRunProfileExecutionItemSyncOutcomeType.Projected] =
            new OutcomeDisplay("Identity created", "MVO Projected", CausalityTone.Primary, Icons.Material.Filled.AirlineStops),
        [ActivityRunProfileExecutionItemSyncOutcomeType.Joined] =
            new OutcomeDisplay("Joined to Identity", "CSO Joined", CausalityTone.Secondary, Icons.Material.Filled.Link),
        [ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow] =
            new OutcomeDisplay("Attributes flowed", "MVO Attribute Flow", CausalityTone.Secondary, Icons.Material.Filled.SyncAlt),
        [ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected] =
            new OutcomeDisplay("Disconnected", "CSO Disconnected", CausalityTone.Warning, Icons.Material.Filled.LinkOff),
        [ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope] =
            new OutcomeDisplay("Left scope", "Out of Scope", CausalityTone.Warning, Icons.Material.Filled.FilterAltOff),
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted] =
            new OutcomeDisplay("Identity deleted", "MVO Deleted", CausalityTone.Error, Icons.Material.Filled.PersonRemove),
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled] =
            new OutcomeDisplay("Identity deletion scheduled", "MVO Deletion Scheduled", CausalityTone.Warning, Icons.Material.Filled.HourglassBottom),
        // The survival counterpart of MvoDeletionScheduled above (#1620): a rejoin undid the disconnection
        // that scheduled it, so the object lives on. Success-toned (the object survived) and the
        // "hourglass disabled" icon reads as the same hourglass, stopped, rather than a new symbol.
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled] =
            new OutcomeDisplay("Deletion cancelled", "MVO Deletion Cancelled", CausalityTone.Success, Icons.Material.Filled.HourglassDisabled),
        [ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection] =
            new OutcomeDisplay("Drift corrected", "CSO Drift Corrected", CausalityTone.Warning, Icons.Material.Filled.CompareArrows),

        // Sync outcomes; outbound (Pending Export creation during sync)
        [ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned] =
            new OutcomeDisplay("Provisioned", "CSO Provisioned", CausalityTone.Primary, Icons.Material.Filled.SwitchAccessShortcut),
        [ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated] =
            new OutcomeDisplay("Export queued", "CSO Pending Export", CausalityTone.Info, Icons.Material.Filled.Schedule),
        // The delete-flavoured staging outcome. Error-toned and named for what it will do, because "Export
        // queued" over a single distinguishedName row read as an attribute update rather than an account
        // being removed. AutoDelete (a clock inside a bin) is the queued form of Deprovisioned's CloudOff.
        [ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued] =
            new OutcomeDisplay("Deprovision queued", "CSO Pending Delete", CausalityTone.Error, Icons.Material.Filled.AutoDelete),

        // Export execution outcomes
        [ActivityRunProfileExecutionItemSyncOutcomeType.Exported] =
            new OutcomeDisplay("Exported", "CSO Exported", CausalityTone.Info, Icons.Material.Filled.Output),
        [ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned] =
            new OutcomeDisplay("Deprovisioned", "CSO Deprovisioned", CausalityTone.Error, Icons.Material.Filled.CloudOff),

        // Attribute priority (#91): a deliberate blank assertion, and a value cleared with no
        // surviving contributor; both worth drawing the eye to.
        [ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull] =
            new OutcomeDisplay("Blank asserted", "MVO Null Asserted", CausalityTone.Warning, Icons.Material.Filled.DoNotDisturbOn),
        [ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor] =
            new OutcomeDisplay("Value cleared", "MVO No Contributor", CausalityTone.Warning, Icons.Material.Filled.HighlightOff),

        // #1570: values kept as last known state because no import source remains to assert the object;
        // the preserving counterpart of NoContributor above, and just as worth drawing the eye to.
        [ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved] =
            new OutcomeDisplay("Values preserved", "MVO Values Preserved", CausalityTone.Warning, Icons.Material.Filled.AcUnit),

        // Configuration change preview (#827): transitions a proposed configuration would cause. Nothing writes
        // these during a run, so they never reach an Activity's causality views; they are mapped because this is
        // the one place an outcome type's vocabulary lives, and a preview surface rendering through it should
        // inherit the same labels rather than grow a second, drifting set. Conditional tone throughout: a preview
        // states what would happen, so the tone marks the consequence's weight, not a failure that has occurred.
        //
        // The plain labels here are held to a stricter standard than the run outcomes above, because these are the
        // ones an administrator reads while deciding whether to save: present tense, no "Would" prefix. The panel
        // heading already establishes that nothing has happened yet, so repeating it per row spent a column's width
        // saying nothing, and the technical labels beside them (which the causality views want) stay untouched.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope] =
            new OutcomeDisplay("Enters import scope", "Would Fall In Scope", CausalityTone.Info, Icons.Material.Filled.FilterAlt,
                "enter import scope"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope] =
            new OutcomeDisplay("Leaves import scope", "Would Fall Out Of Scope", CausalityTone.Warning, Icons.Material.Filled.FilterAltOff,
                "leave import scope"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible] =
            new OutcomeDisplay("Becomes eligible for deletion", "Would Become Deletion Eligible", CausalityTone.Error, Icons.Material.Filled.DeleteOutline,
                "become eligible for deletion"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible] =
            new OutcomeDisplay("No longer eligible for deletion", "Would Cease To Be Deletion Eligible", CausalityTone.Success, Icons.Material.Filled.RestoreFromTrash,
                "no longer be eligible for deletion"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate] =
            new OutcomeDisplay("Deletion date changes", "Would Change Deletion Eligible Date", CausalityTone.Warning, Icons.Material.Filled.EditCalendar,
                "have their deletion date changed"),
        // Named for the disconnection rather than for the scope change, which is the fact that distinguishes it
        // from WouldFallOutOfScope above; that the object also leaves scope is carried by the delta's old and new
        // values, so the label does not have to spend itself restating it.
        //
        // Warning rather than Error, matching its own run-time outcomes (Disconnected, DisconnectedOutOfScope). A
        // disconnection recalls the attribute values the object contributed, which is serious, but re-selecting the
        // container and importing puts it back; a deletion past its grace period does not come back. Toning both
        // the same left the preview's severity column encoding nothing on the change that most needs it.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject] =
            new OutcomeDisplay("Disconnects from its Metaverse Object", "Would Disconnect From Metaverse Object", CausalityTone.Warning, Icons.Material.Filled.LinkOff,
                "disconnect from their Metaverse Object"),
        // The destructive-toggle preview's fates (#1115). Error tone on the delete because a deletion in the
        // target system past its recycle window does not come back; Success on the join kept because nothing is
        // destroyed; Warning on the exposure change because nothing happens on save, but the standing consequence
        // of every future scope exit changes.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport] =
            new OutcomeDisplay("Removed from the target system", "Would Stage Delete Export", CausalityTone.Error, Icons.Material.Filled.AutoDelete,
                "be removed from their target Connected System"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined] =
            new OutcomeDisplay("Keeps its Metaverse Object join", "Would Remain Joined", CausalityTone.Success, Icons.Material.Filled.Link,
                "keep their Metaverse Object join"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction] =
            new OutcomeDisplay("Scope-exit action changes", "Would Change Deprovision Action", CausalityTone.Warning, Icons.Material.Filled.SwapHoriz,
                "have their scope-exit action changed"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow] =
            new OutcomeDisplay("Attribute Flow does not evaluate", "Would Fail Attribute Flow", CausalityTone.Error, Icons.Material.Filled.RuleFolder,
                "have an Attribute Flow that would not evaluate"),
        // The Object Matching preview's fates (#1457). Error tone on joining a different Metaverse Object and on
        // projecting instead of joining, because both are identity corruption that nothing reports at run time:
        // one merges an account into the wrong identity, the other splits one identity into two. Ambiguity is a
        // Warning because the next synchronisation refuses the object loudly rather than joining it wrongly.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject] =
            new OutcomeDisplay("Joins a different Metaverse Object", "Would Join Different Metaverse Object", CausalityTone.Error, Icons.Material.Filled.SwapHoriz,
                "join a different Metaverse Object"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject] =
            new OutcomeDisplay("Joins instead of projecting", "Would Join Instead Of Project", CausalityTone.Success, Icons.Material.Filled.Link,
                "join an existing Metaverse Object instead of projecting a new one"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin] =
            new OutcomeDisplay("Projects instead of joining", "Would Project Instead Of Join", CausalityTone.Error, Icons.Material.Filled.CallSplit,
                "project a new Metaverse Object instead of joining an existing one"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously] =
            new OutcomeDisplay("Matches more than one Metaverse Object", "Would Match Ambiguously", CausalityTone.Warning, Icons.Material.Filled.QuestionMark,
                "match more than one Metaverse Object"),
        // The behaviour-toggle preview's fates (#1462). Warning rather than Error on all three: nothing existing
        // is destroyed by any of them, and what they cost is an identity, a Connected System Object or a correction that never
        // arrives, which is a different kind of harm from a deletion.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting] =
            new OutcomeDisplay("No longer creates an identity", "Would Stop Projecting", CausalityTone.Warning, Icons.Material.Filled.PersonOff,
                "no longer have a Metaverse Object created for them"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning] =
            new OutcomeDisplay("No longer creates a Connected System Object", "Would Stop Provisioning", CausalityTone.Warning, Icons.Material.Filled.NoAccounts,
                "no longer have a Connected System Object created for them"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift] =
            new OutcomeDisplay("Free to drift from JIM", "Would Stop Correcting Drift", CausalityTone.Warning, Icons.Material.Filled.SyncDisabled,
                "be free to drift from what JIM holds"),
        // The schema selection preview's fates (#1475). Warning on the freeze rather than Error, because nothing is
        // destroyed and nothing is disconnected; what happens is that values stop tracking their source while still
        // being contributed, which is a slower harm and an easier one to miss.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported] =
            new OutcomeDisplay("Stops being imported, stays joined", "Would Stop Being Imported", CausalityTone.Warning, Icons.Material.Filled.CloudOff,
                "stop being imported while staying joined, keeping the values they last imported"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported] =
            new OutcomeDisplay("Imported again", "Would Resume Being Imported", CausalityTone.Success, Icons.Material.Filled.CloudSync,
                "be imported again, so their values track the Connected System once more"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues] =
            new OutcomeDisplay("Contributed values withdrawn", "Would Withdraw Contributed Values", CausalityTone.Warning, Icons.Material.Filled.Undo,
                "have the values this Connected System contributed withdrawn"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues] =
            new OutcomeDisplay("Contributed values kept", "Would Retain Contributed Values", CausalityTone.Info, Icons.Material.Filled.Inventory2,
                "keep the values this Connected System contributed"),
        // The export-side scope pair. These exist because the import-side pair above were the only scope
        // transitions an export rule's scope preview could emit, and "Leaves import scope" against a Metaverse
        // Object leaving an export rule names a direction the rule does not have. Info on both: an identity the
        // rule stops standing over with nothing in the target to remove, and one it starts flowing to, are the
        // benign ends of a scope change. The costly ends have transitions of their own (WouldStageDeleteExport,
        // WouldDisconnectFromMetaverseObject, WouldStopProvisioning and Provisioned), which is what lets these
        // two be read as "nothing is created or removed" without qualification.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope] =
            new OutcomeDisplay("Leaves export scope, nothing to remove", "Would Leave Export Scope", CausalityTone.Info, Icons.Material.Filled.FilterAltOff,
                "leave export scope, with nothing in the target system to remove"),
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope] =
            new OutcomeDisplay("Enters export scope", "Would Enter Export Scope", CausalityTone.Info, Icons.Material.Filled.FilterAlt,
                "enter export scope")
    };

    /// <summary>
    /// Gets the display mapping for a sync outcome type. Every enum value is mapped; an unmapped
    /// value (a future enum addition without a mapping here) falls back to the enum name so the UI
    /// still renders rather than throwing.
    /// </summary>
    public static OutcomeDisplay Get(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        return Map.TryGetValue(outcomeType, out var display)
            ? display
            : new OutcomeDisplay(outcomeType.ToString(), outcomeType.ToString(), CausalityTone.Secondary, Icons.Material.Filled.Circle);
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
                new OutcomeDisplay("Record created", "CSO Exported (Create)", CausalityTone.Success, Icons.Material.Filled.AddCircle),
            CausalReasonCode.ExportUpdateStaged =>
                new OutcomeDisplay("Changes applied", "CSO Exported (Update)", CausalityTone.Info, Icons.Material.Filled.Output),
            CausalReasonCode.ExportDeleteStaged =>
                new OutcomeDisplay("Record deleted", "CSO Exported (Delete)", CausalityTone.Error, Icons.Material.Filled.Delete),
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
                    new OutcomeDisplay("Created", "MVO Projected", CausalityTone.Success, Icons.Material.Filled.Add),
                ObjectChangeType.Joined =>
                    new OutcomeDisplay("Joined", "CSO Joined", CausalityTone.Secondary, Icons.Material.Filled.Link),
                ObjectChangeType.Created =>
                    new OutcomeDisplay("Created", "MVO Created", CausalityTone.Success, Icons.Material.Filled.Add),
                _ => null
            };
        }

        if (cohort.SourceImportChangeType is { } sourceImportChangeType)
        {
            return sourceImportChangeType switch
            {
                ObjectChangeType.Added =>
                    new OutcomeDisplay("Created", "CSO Added", CausalityTone.Success, Icons.Material.Filled.Add),
                ObjectChangeType.Updated =>
                    new OutcomeDisplay("Updated", "CSO Updated", CausalityTone.Info, Icons.Material.Filled.Edit),
                ObjectChangeType.Deleted =>
                    new OutcomeDisplay("Deleted", "CSO Deleted", CausalityTone.Error, Icons.Material.Filled.Delete),
                _ => null
            };
        }

        return cohort.EdgeType switch
        {
            CausalEdgeType.PendingExportQueueingCausedExportExecution => GetQueueingDecisionOperation(cohort.ReasonCode),
            CausalEdgeType.MetaverseObjectDeletionCausedDeprovision or CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval =>
                new OutcomeDisplay("Deleted", "MVO Deleted", CausalityTone.Error, Icons.Material.Filled.Delete),
            // ExportCausedImportConfirmation and any seam this map does not know fall through here: a
            // confirmation is not itself an object operation, and an unknown edge is never guessed.
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
                // Null covers outcomes recorded before this was captured: honestly "unknown kind"
                // rather than a guess.
                _ => null
            };
        }

        return outcomeType switch
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected =>
                new OutcomeDisplay("Created", "MVO Projected", CausalityTone.Success, Icons.Material.Filled.Add),
            ActivityRunProfileExecutionItemSyncOutcomeType.Joined =>
                new OutcomeDisplay("Joined", "CSO Joined", CausalityTone.Secondary, Icons.Material.Filled.Link),
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded =>
                new OutcomeDisplay("Created", "CSO Added", CausalityTone.Success, Icons.Material.Filled.Add),
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated =>
                new OutcomeDisplay("Updated", "CSO Updated", CausalityTone.Info, Icons.Material.Filled.Edit),
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted =>
                new OutcomeDisplay("Deleted", "CSO Deleted", CausalityTone.Error, Icons.Material.Filled.Delete),
            // The plain labels match the map's own AttributeFlow/DriftCorrection titles above so the
            // chip's technical label never disagrees with the card's own; both are honestly "an update",
            // not a create or a delete.
            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow =>
                new OutcomeDisplay("Updated", "MVO Attribute Flow", CausalityTone.Info, Icons.Material.Filled.Edit),
            ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection =>
                new OutcomeDisplay("Updated", "CSO Drift Corrected", CausalityTone.Info, Icons.Material.Filled.Edit),
            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned =>
                new OutcomeDisplay("Created", "CSO Provisioned", CausalityTone.Success, Icons.Material.Filled.Add),
            ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted =>
                new OutcomeDisplay("Deleted", "MVO Deleted", CausalityTone.Error, Icons.Material.Filled.Delete),
            ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned =>
                new OutcomeDisplay("Deleted", "CSO Deprovisioned", CausalityTone.Error, Icons.Material.Filled.Delete),
            // A queued deprovision is a staged delete; reuse the chain's own "Export Staged (Delete)"
            // chip rather than inventing a second vocabulary for the same staged kind.
            ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued =>
                GetQueueingDecisionOperation(CausalReasonCode.ExportDeleteStaged),
            // Every Would* preview (nothing executed), ExportConfirmed/ExportFailed (confirming or
            // failing an export is not itself an object operation),
            // DeletionDetected/Disconnected/DisconnectedOutOfScope/MvoDeletionScheduled/MvoDeletionCancelled
            // (a state change, not an operation this map states an icon for), AssertedNull/NoContributor
            // (attribute-priority housekeeping, not an object operation) and anything unmapped all fall
            // through here: null rather than a guess. PendingExportCreated never reaches this switch; it is
            // handled above.
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
                new OutcomeDisplay("Created", "Export Staged (Create)", CausalityTone.Success, Icons.Material.Filled.Add),
            CausalReasonCode.ExportUpdateStaged =>
                new OutcomeDisplay("Updated", "Export Staged (Update)", CausalityTone.Info, Icons.Material.Filled.Edit),
            CausalReasonCode.ExportDeleteStaged =>
                new OutcomeDisplay("Deleted", "Export Staged (Delete)", CausalityTone.Error, Icons.Material.Filled.Delete),
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
    /// head's "Identity created" / "Provisioned" / "Joined to Identity") added a title a reader had
    /// already read twice by the time they reached it.
    /// </summary>
    /// <remarks>
    /// True for <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.Projected"/>,
    /// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.Joined"/> and
    /// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned"/> (whose Lineage join
    /// label states the same verb a third time), and for
    /// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.Exported"/>: its decision-specific
    /// titles ("Record created", "Changes applied", "Record deleted") say nothing the chip's own
    /// Created / Updated / Deleted does not, so once the chip exists the title is the restatement.
    /// Exported's chip only renders where the export's queueing decision resolved, and an item exported
    /// before causal capture existed resolves none; that no-chip case is exactly what
    /// <c>CausalityEventCard.HideTitle</c>'s misuse guard covers, keeping the bare "Exported" head
    /// rendering rather than leaving the card naming nothing, so this method stays a function of the
    /// outcome type alone.
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
