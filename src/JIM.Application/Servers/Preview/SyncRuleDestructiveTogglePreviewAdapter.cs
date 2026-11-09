// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Sync;
using System.Runtime.CompilerServices;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What flipping one of a Synchronisation Rule's two destructive toggles would do to the objects the rule stands
/// over (#1115, gap G3): the Outbound Deprovision Action, which can turn every future scope exit into a deletion
/// in the target system, and the Inbound Out-of-Scope Action, which can mass-disconnect joined objects. Both are
/// single dropdowns with no impact analysis in front of them today.
///
/// The toggles change no object's scope; they change what happens to an object whose scope membership is already
/// decided. The preview therefore answers two different questions and keeps them apart:
///
/// - **Imminent tier:** objects the next synchronisation would already act on, whose fate the proposal changes now.
///   This is the count an administrator consents to. For an export rule these are the joined target objects whose
///   Metaverse Object is out of the rule's scope: saving flags the rule's Metaverse Objects for review, and the next
///   synchronisation of any Connected System carries it out. For an import rule they are the joined Connected System
///   Objects out of import scope or already obsoleted: saving an enabled rule records the change as awaiting
///   synchronisation, so the next synchronisation of its Connected System, Delta or Full, processes every object of the
///   type and acts on them. (Before that was recorded a Delta skipped objects that had not changed, so only a Full
///   Synchronisation reached them.) A disabled rule acts on nothing until it is enabled, and enabling it is recorded in
///   the same way.
/// - **Exposure tier** (outbound only): objects nothing happens to on save, but whose fate on every *future*
///   scope exit changes with the proposed action. This is what makes "3,400 objects in this system move from
///   Disconnect to Delete" readable at a glance without overstating what the save itself does.
///
/// Fate is always put to the synchronisation engine's own decisions
/// (<see cref="ISyncEngine.DecideOutOfScopeDeprovisioning"/>, <see cref="ISyncEngine.DecideLastScopeExit"/>,
/// and the shared deletion-eligibility evaluator) rather than reimplemented, so the preview cannot drift from
/// what a run would do.
/// </summary>
public class SyncRuleDestructiveTogglePreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;
    private readonly ISyncEngine _syncEngine;

    /// <summary>
    /// How a fate transition is written into a delta row's value columns. The words match the editor's own
    /// options, so the drill-down reads as the dropdown the administrator just changed.
    /// </summary>
    private const string DeprovisioningActionAttributeName = "Deprovisioning Action";
    private const string OutOfScopeActionAttributeName = "Out-of-Scope Action";
    private const string DisconnectValue = "Disconnect";
    private const string DeleteValue = "Delete";
    private const string RemainJoinedValue = "Remain joined";

    /// <summary>
    /// How many joined objects' Metaverse Objects are loaded per batch in the outbound walk. Batched so a
    /// whole-connector-space preview never materialises the whole Metaverse population at once.
    /// </summary>
    private const int MetaverseObjectBatchSize = 500;

    public SyncRuleDestructiveTogglePreviewAdapter(JimApplication application, ISyncEngine syncEngine)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _syncEngine = syncEngine ?? throw new ArgumentNullException(nameof(syncEngine));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.SynchronisationRule;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(SyncRuleDestructiveToggleProposal);

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleDestructiveToggleProposal>();
        var rule = await GetRuleAsync(context);
        var findings = new List<PreviewValidationFinding>();

        var outboundChanged = proposal.OutboundDeprovisionAction != rule.OutboundDeprovisionAction;
        var inboundChanged = proposal.InboundOutOfScopeAction != rule.InboundOutOfScopeAction;

        if (!outboundChanged && !inboundChanged)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "Neither destructive setting changes under this proposal, so no object's fate changes and no impact " +
                "is counted below.",
                nameof(SyncRule.OutboundDeprovisionAction)));
            return findings;
        }

        if (outboundChanged && rule.Direction != SyncRuleDirection.Export)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "The Outbound Deprovision Action is read only by export Synchronisation Rules, so changing it on " +
                "this import rule changes no object's fate and is not counted below.",
                nameof(SyncRule.OutboundDeprovisionAction)));
        }

        if (inboundChanged && rule.Direction != SyncRuleDirection.Import)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "The Inbound Out-of-Scope Action is read only by import Synchronisation Rules, so changing it on " +
                "this export rule changes no object's fate and is not counted below.",
                nameof(SyncRule.InboundOutOfScopeAction)));
        }

        var effectiveChange = HasEffectiveChange(rule, proposal);

        if (effectiveChange && !rule.Enabled)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "This Synchronisation Rule is disabled, so no synchronisation applies its settings today. The " +
                "counts below describe what the next synchronisation would do once it is enabled.",
                nameof(SyncRule.Enabled)));
        }

        // The honest answer for a multi-rule object type, and it is not obvious (#2823): where an object leaves every
        // import rule for its type, the rules that held it decide, and Remain Joined from any of them keeps the join. So
        // switching this rule to Disconnect cannot disconnect an object a sibling saying Remain Joined also held, and the
        // counts below include only the objects it can.
        if (inboundChanged && rule.Direction == SyncRuleDirection.Import
            && proposal.InboundOutOfScopeAction == InboundOutOfScopeAction.Disconnect)
        {
            var keepingSiblings = (await GetImportRulesForTypeAsync(rule))
                .Where(sibling => sibling.Id != rule.Id && sibling.InboundOutOfScopeAction == InboundOutOfScopeAction.RemainJoined)
                .ToList();
            if (keepingSiblings.Count > 0)
            {
                var names = string.Join(", ", SyncEngine.InNameOrder(keepingSiblings).Select(sibling => $"'{sibling.Name}'"));
                findings.Add(new PreviewValidationFinding(
                    PreviewValidationSeverity.Warning,
                    $"{names} also covers this object type and keeps its objects joined (Remain Joined). Where an object " +
                    "leaves every import Synchronisation Rule, the rules that held it decide, and Remain Joined from any of " +
                    "them keeps the join, so this change disconnects only objects those rules never held. The counts below " +
                    "include only those.",
                    nameof(SyncRule.InboundOutOfScopeAction)));
            }
        }

        return findings;
    }

    public async Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleDestructiveToggleProposal>();
        var rule = await GetRuleAsync(context);

        if (!HasEffectiveChange(rule, proposal))
            return new PreviewCostEstimate(0);

        // The walk visits every joined object of the rule's type, whichever toggle moved: both toggles decide the
        // fate of a join. Set-based and indexed.
        var affected = await _application.ConnectedSystems.GetJoinedConnectedSystemObjectCountAsync(
            rule.ConnectedSystemId, rule.ConnectedSystemObjectTypeId);
        return new PreviewCostEstimate(affected);
    }

    /// <summary>
    /// Counted by streaming the same evaluation the delta stage reads, rather than by a set of SQL count queries.
    /// </summary>
    /// <remarks>
    /// A deliberate departure from the contract's "set-based SQL only", for the reason both earlier adapters
    /// departed from it: scope membership is decided by Scoping Criteria evaluation that cannot be expressed as
    /// SQL without reimplementing it, and a preview whose counts disagreed with its own drill-down about deletions
    /// is precisely the defect this framework exists to prevent. Where the population is large the framework's
    /// dispatch decision hands the whole preview to JIM.Worker, which is what that decision is for.
    /// </remarks>
    public async Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context) =>
        await PreviewImpactCounter.CountAsync((await CreateImpactCounterAsync(context))!, EvaluateDeltasAsync(context, CancellationToken.None));

    /// <summary>
    /// Stage 2 counted in the framework's one evaluation pass (#1530): one per delta, per transition, exactly as
    /// <see cref="CountImpactAsync"/> counts.
    /// </summary>
    public async Task<IPreviewImpactCounter?> CreateImpactCounterAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rule = await GetRuleAsync(context);
        return PreviewImpactCounter.PerDelta(connectedSystemId: rule.ConnectedSystemId);
    }

    public async IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<SyncRuleDestructiveToggleProposal>();
        var rule = await GetRuleAsync(context);

        // No effective change means no object's fate can have changed, so the population is not read at all.
        if (!HasEffectiveChange(rule, proposal))
            yield break;

        var csoTypeId = rule.ConnectedSystemObjectTypeId;

        if (rule.Direction == SyncRuleDirection.Export)
        {
            await foreach (var delta in EvaluateOutboundAsync(rule, csoTypeId, proposal, cancellationToken))
                yield return delta;
        }
        else
        {
            await foreach (var delta in EvaluateInboundAsync(rule, csoTypeId, proposal, cancellationToken))
                yield return delta;
        }
    }

    /// <summary>
    /// The outbound walk: every joined object in the export rule's target system, classified by whether its
    /// Metaverse Object is inside or outside the rule's scope today. Outside is the imminent tier (the next
    /// synchronisation deprovisions it, and the proposal changes what that means); inside is the exposure tier.
    /// </summary>
    private async IAsyncEnumerable<PreviewDelta> EvaluateOutboundAsync(SyncRule rule, int csoTypeId,
        SyncRuleDestructiveToggleProposal proposal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The fates are the engine's verdicts, not this adapter's reading of the enum: the preview and a run must
        // answer from the same code. The proposed rule is a minimal stand-in carrying only what the decision
        // reads, never a mutation of the loaded rule.
        var currentAction = _syncEngine.DecideOutOfScopeDeprovisioning(rule, existingPendingExport: null).Action;
        var proposedRule = new SyncRule
        {
            Name = rule.Name,
            Direction = SyncRuleDirection.Export,
            OutboundDeprovisionAction = proposal.OutboundDeprovisionAction
        };
        var proposedAction = _syncEngine.DecideOutOfScopeDeprovisioning(proposedRule, existingPendingExport: null).Action;

        if (currentAction == proposedAction)
            yield break;

        var oldValue = DescribeOutboundAction(rule.OutboundDeprovisionAction);
        var newValue = DescribeOutboundAction(proposal.OutboundDeprovisionAction);

        // One clock for the whole evaluation, so a long stream cannot classify two identical objects differently
        // depending on when each is reached (relative date criteria resolve against it).
        var asAt = DateTime.UtcNow;

        // The other enabled export rules on the same system and Object Type: one still covering an identity takes its
        // account over when it leaves this rule's scope, so this rule's action never reaches it (#2822).
        var siblingExportRules = (await _application.ConnectedSystems.GetSyncRulesAsync(rule.ConnectedSystemId, includeDisabledSyncRules: false))
            .Where(sibling => sibling.Id != rule.Id && sibling.Direction == SyncRuleDirection.Export &&
                              sibling.ConnectedSystemObjectTypeId == rule.ConnectedSystemObjectTypeId &&
                              sibling.MetaverseObjectTypeId == rule.MetaverseObjectTypeId)
            .ToList();

        var batch = new List<ConnectedSystemObject>(MetaverseObjectBatchSize);
        await foreach (var cso in _application.ConnectedSystems
            .StreamJoinedConnectedSystemObjects(rule.ConnectedSystemId, csoTypeId)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch.Add(cso);
            if (batch.Count < MetaverseObjectBatchSize)
                continue;

            foreach (var delta in await ClassifyOutboundBatchAsync(rule, siblingExportRules, batch, proposedRule, oldValue, newValue, asAt))
                yield return delta;
            batch.Clear();
        }

        foreach (var delta in await ClassifyOutboundBatchAsync(rule, siblingExportRules, batch, proposedRule, oldValue, newValue, asAt))
            yield return delta;
    }

    /// <summary>
    /// Loads one batch's Metaverse Objects and yields the imminent or exposure delta for each joined object the
    /// rule actually governs.
    /// </summary>
    /// <param name="proposedRule">A stand-in carrying the proposed action, for the engine to decide each scope exit's
    /// fate against.</param>
    private async Task<List<PreviewDelta>> ClassifyOutboundBatchAsync(SyncRule rule, List<SyncRule> siblingExportRules,
        List<ConnectedSystemObject> batch, SyncRule proposedRule,
        string oldValue, string newValue, DateTime asAt)
    {
        var deltas = new List<PreviewDelta>();
        if (batch.Count == 0)
            return deltas;

        var metaverseObjectIds = batch
            .Where(cso => cso.MetaverseObjectId.HasValue)
            .Select(cso => cso.MetaverseObjectId!.Value)
            .ToList();
        var metaverseObjectsById = (await _application.Metaverse.GetMetaverseObjectsByIdsNoTrackingAsync(metaverseObjectIds))
            .ToDictionary(mvo => mvo.Id);

        // The batch's connections, for a connection criterion (#2021): one read, and none when the rule has no such criterion.
        var connections = await _application.ExportEvaluation.ReadConnectionsAsync(metaverseObjectIds, [rule, .. siblingExportRules]);

        // An object joined to a Metaverse Object of another type is never governed by this rule: export rules
        // apply per Metaverse Object Type, so its scope exits are decided elsewhere. Null-state does not flow
        // through Where, hence the ! dereference after it.
        var governed = batch
            .Select(cso => (Cso: cso, Mvo: cso.MetaverseObjectId is { } metaverseObjectId &&
                metaverseObjectsById.TryGetValue(metaverseObjectId, out var mvo) ? mvo : null))
            .Where(pair => pair.Mvo != null && pair.Mvo.Type?.Id == rule.MetaverseObjectTypeId)
            .Select(pair => (pair.Cso, Mvo: pair.Mvo!, Scope: PreviewScopeAnswer.Decide(() => _application.ScopingEvaluation.IsMvoInScopeForExportRule(pair.Mvo!, rule, asAt, connections))))
            .ToList();

        // A scope exit's fate also turns on a Delete already queued for the object (#1970), so the engine decides each
        // one with it, exactly as the run will.
        var leavingIds = governed.Where(g => !g.Scope.Undecided && !g.Scope.InScope).Select(g => g.Cso.Id).ToList();
        var queuedByCsoId = leavingIds.Count == 0
            ? []
            : await _application.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(leavingIds);

        foreach (var (cso, mvo, scope) in governed)
        {
            // An object whose export scope cannot be decided is one the run stops with an error (#2264): it is neither
            // provisioned nor deprovisioned, so neither action reaches it. Said, with the reason, rather than guessed.
            if (scope.Undecided)
            {
                deltas.Add(OutboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFail, rule, cso, mvo,
                    PreviewScopeAnswer.AttributeName, null, scope.Describe()));
                continue;
            }

            ActivityRunProfileExecutionItemSyncOutcomeType transition;
            if (scope.InScope)
            {
                transition = ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction;
            }
            else
            {
                // Another export rule still covers the identity, so it takes the account over and nothing is deprovisioned
                // (#2822). A sibling whose scope cannot be decided stops the run for the object, which deprovisions nothing
                // either.
                if (siblingExportRules.Any(sibling => PreviewScopeAnswer.Decide(() =>
                        _application.ScopingEvaluation.IsMvoInScopeForExportRule(mvo, sibling, asAt, connections)) is var answer &&
                        (answer.Undecided || answer.InScope)))
                    continue;

                var fate = _syncEngine.DecideOutOfScopeDeprovisioning(proposedRule, queuedByCsoId.GetValueOrDefault(cso.Id)).Action;

                // A Delete already sent goes ahead whichever action the rule carries, so the proposal changes nothing.
                if (fate == OutOfScopeDeprovisioningAction.DeleteAlreadySent)
                    continue;

                transition = fate == OutOfScopeDeprovisioningAction.StageDeleteExport
                    ? ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport
                    : ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject;
            }

            deltas.Add(OutboundDelta(transition, rule, cso, mvo, DeprovisioningActionAttributeName, oldValue, newValue));
        }

        return deltas;
    }

    private static PreviewDelta OutboundDelta(ActivityRunProfileExecutionItemSyncOutcomeType transition, SyncRule rule,
        ConnectedSystemObject cso, MetaverseObject mvo, string attributeName, string? oldValue, string newValue) =>
        new(transition,
            ObjectDisplayName: cso.NameOrId,
            ObjectTypeName: cso.Type?.Name,
            MetaverseObjectTypeId: mvo.Type?.Id,
            MetaverseObjectId: mvo.Id,
            ConnectedSystemObjectId: cso.Id,
            ConnectedSystemId: rule.ConnectedSystemId,
            AttributeName: attributeName,
            OldValue: oldValue,
            NewValue: newValue);

    /// <summary>
    /// The inbound walk: every joined object of the import rule's type, classified by whether the next
    /// synchronisation would apply the Out-of-Scope Action to it (already obsoleted, or out of scope of every
    /// import rule), and whether the action the rules that held it decide on (#2823) changes under the proposal. The next
    /// synchronisation is the Connected System's next Delta or Full, because saving the change records the type as
    /// awaiting synchronisation and that run then processes every object of it.
    /// </summary>
    private async IAsyncEnumerable<PreviewDelta> EvaluateInboundAsync(SyncRule rule, int csoTypeId,
        SyncRuleDestructiveToggleProposal proposal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var siblings = await GetImportRulesForTypeAsync(rule);

        // The engine decides each path's action from the rule list; the proposal is applied by handing it the same list
        // with the edited rule's action swapped, never by mutating the loaded rule.
        var proposedSiblings = siblings
            .Select(sibling => sibling.Id == rule.Id ? WithInboundAction(sibling, proposal.InboundOutOfScopeAction) : sibling)
            .ToList();

        var rulesWithCriteria = siblings.Where(s => s.ObjectScopingCriteriaGroups.Count > 0).ToList();

        // One clock for the whole evaluation, so relative date criteria cannot classify two identical objects
        // differently depending on when each is reached in a long stream.
        var asAt = DateTime.UtcNow;

        var disconnectionsByMetaverseObject = new Dictionary<Guid, int>();

        await foreach (var cso in _application.ConnectedSystems
            .StreamJoinedConnectedSystemObjects(rule.ConnectedSystemId, csoTypeId)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cso.Status != ConnectedSystemObjectStatus.Obsolete)
            {
                // The scope-exit path fires only for an object out of scope of every import rule, and a rule with
                // no Scoping Criteria is in scope for everything, so it never fires while such a rule exists
                // (rulesWithCriteria smaller than siblings means one does). Mirrors the real path in
                // SyncTaskProcessorBase.ProcessMetaverseObjectChangesAsync.
                if (rulesWithCriteria.Count == 0 || rulesWithCriteria.Count < siblings.Count)
                    continue;

                // Every rule with criteria is decided, as the run decides them (GetInScopeImportRulesAsync), because
                // one the run cannot decide stops the object whatever the others say: an error, its join kept, and
                // no Out-of-Scope Action applied (#2264). Reported only where the proposal changes that action.
                var scopes = rulesWithCriteria
                    .Select(r => PreviewScopeAnswer.Decide(() => _application.ScopingEvaluation.IsCsoInScopeForImportRule(cso, r, asAt)))
                    .ToList();
                var undecided = scopes.FirstOrDefault(scope => scope.Undecided);
                if (!undecided.Undecided && scopes.Any(scope => scope.InScope))
                    continue;

                if (undecided.Undecided)
                {
                    var (actionNow, actionProposed) = await DecideAsync(cso, siblings, proposedSiblings);
                    if (actionNow != actionProposed)
                        yield return InboundDelta(cso, rule, ActivityRunProfileExecutionItemSyncOutcomeType.WouldFail,
                            null, undecided.Describe(), PreviewScopeAnswer.AttributeName);
                    continue;
                }
            }

            // Out of every import rule's scope, or already obsoleted (which leaves every rule at once): the rules that
            // held the object decide, and Remain Joined wins (#2823).
            var (currentAction, proposedAction) = await DecideAsync(cso, siblings, proposedSiblings);
            if (currentAction == proposedAction)
                continue;

            // A join an export rule made is kept whatever the action (#2021), so switching it moves no join.
            if (SyncEngine.IsJoinMadeByExportRule(cso))
                continue;

            if (proposedAction == InboundOutOfScopeAction.Disconnect)
            {
                if (cso.MetaverseObjectId is { } metaverseObjectId)
                    disconnectionsByMetaverseObject[metaverseObjectId] =
                        disconnectionsByMetaverseObject.GetValueOrDefault(metaverseObjectId) + 1;

                yield return InboundDelta(cso, rule,
                    ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject,
                    RemainJoinedValue, DisconnectValue);
            }
            else
            {
                yield return InboundDelta(cso, rule,
                    ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined,
                    DisconnectValue, RemainJoinedValue);
            }
        }

        // The Metaverse consequence, evaluated only once the disconnections are known, because whether an object
        // becomes eligible for deletion depends on how many of its connectors survive and this system may hold
        // more than one of them. Shared with the other disconnecting adapters so no two previews can disagree
        // about whether an object dies.
        await foreach (var delta in PreviewDeletionEligibilityEvaluator.EvaluateAsync(
                           _application, _syncEngine, rule.ConnectedSystemId, disconnectionsByMetaverseObject, cancellationToken))
            yield return delta;
    }

    private static PreviewDelta InboundDelta(ConnectedSystemObject cso, SyncRule rule,
        ActivityRunProfileExecutionItemSyncOutcomeType transition, string? oldValue, string newValue,
        string attributeName = OutOfScopeActionAttributeName) =>
        new(transition,
            ObjectDisplayName: cso.NameOrId,
            ObjectTypeName: cso.Type?.Name,
            MetaverseObjectId: cso.MetaverseObjectId,
            ConnectedSystemObjectId: cso.Id,
            ConnectedSystemId: rule.ConnectedSystemId,
            AttributeName: attributeName,
            OldValue: oldValue,
            NewValue: newValue);

    /// <summary>
    /// The action a synchronisation would apply to the object under the current rules and under the proposed ones.
    /// </summary>
    private async Task<(InboundOutOfScopeAction Current, InboundOutOfScopeAction Proposed)> DecideAsync(
        ConnectedSystemObject cso, List<SyncRule> siblings, List<SyncRule> proposedSiblings)
    {
        var actions = await PreviewLastScopeExit.DecideAsync(_application, _syncEngine, cso, siblings, proposedSiblings);
        return (actions[0], actions[1]);
    }

    /// <summary>
    /// Whether the proposal changes a setting the rule's direction actually reads. A toggle the direction never
    /// consumes changes no object's fate, and stage 1 says so instead of counting anything for it.
    /// </summary>
    private static bool HasEffectiveChange(SyncRule rule, SyncRuleDestructiveToggleProposal proposal) =>
        rule.Direction switch
        {
            SyncRuleDirection.Export => proposal.OutboundDeprovisionAction != rule.OutboundDeprovisionAction,
            SyncRuleDirection.Import => proposal.InboundOutOfScopeAction != rule.InboundOutOfScopeAction,
            _ => false
        };

    /// <summary>
    /// The enabled import Synchronisation Rules covering the edited rule's object type, in the order the engine
    /// reads them (the same server call and filter the synchronisation task performs).
    /// </summary>
    private async Task<List<SyncRule>> GetImportRulesForTypeAsync(SyncRule rule)
    {
        var rules = await _application.ConnectedSystems.GetSyncRulesAsync(rule.ConnectedSystemId, includeDisabledSyncRules: false);
        return
        [
            .. rules.Where(r => r.Direction == SyncRuleDirection.Import &&
                                r.ConnectedSystemObjectTypeId == rule.ConnectedSystemObjectTypeId)
        ];
    }

    /// <summary>
    /// A minimal stand-in for a sibling rule with the proposed action applied, carrying only what
    /// <see cref="ISyncEngine.DecideLastScopeExit"/> reads. A stand-in rather than a mutation, because the
    /// loaded rule is the current configuration and a preview must never change it, even in memory.
    /// </summary>
    private static SyncRule WithInboundAction(SyncRule rule, InboundOutOfScopeAction action) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        ConnectedSystemId = rule.ConnectedSystemId,
        ConnectedSystemObjectTypeId = rule.ConnectedSystemObjectTypeId,
        MetaverseObjectTypeId = rule.MetaverseObjectTypeId,
        Direction = rule.Direction,
        Enabled = rule.Enabled,
        InboundOutOfScopeAction = action,
        OutboundDeprovisionAction = rule.OutboundDeprovisionAction,
        ObjectScopingCriteriaGroups = rule.ObjectScopingCriteriaGroups
    };

    private static string DescribeOutboundAction(OutboundDeprovisionAction action) =>
        action == OutboundDeprovisionAction.Delete ? DeleteValue : DisconnectValue;

    private async Task<SyncRule> GetRuleAsync(PreviewContext context)
    {
        var id = context.TargetId ?? throw new InvalidOperationException(
            "A Synchronisation Rule destructive-toggle preview must name the rule it concerns.");
        return await _application.ConnectedSystems.GetSyncRuleAsync(id)
            ?? throw new InvalidOperationException(
                $"Cannot preview destructive toggles for Synchronisation Rule {id}: it no longer exists.");
    }
}
