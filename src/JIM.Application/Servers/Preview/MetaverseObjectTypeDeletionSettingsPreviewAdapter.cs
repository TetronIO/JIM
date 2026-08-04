// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Preview;
using System.Runtime.CompilerServices;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What a change to a Metaverse Object Type's deletion settings would do to the objects already on their way to
/// deletion (#1114). The framework's pilot adapter, and the surface that most needed one: today an administrator
/// changes a dropdown and finds out what it meant from the deletion Activity afterwards.
///
/// The question is narrower than it first appears, and the narrowing is the useful part. Automatic deletion acts on
/// objects carrying a **disconnection mark**, set when the synchronisation engine saw their last (or authoritative)
/// connector go. Given that mark and whether the object still has connectors, the date JIM would delete it on is
/// fully determined by the two settings, so the whole preview is: evaluate every marked object twice, once under
/// the settings in force and once under the proposal, and report where the two answers differ. Objects with no
/// mark cannot be affected by any settings change, which is why the population is small and this adapter is cheap.
///
/// **The trigger system list is deliberately not part of that evaluation.** It is consulted at the moment a
/// Connected System Object disconnects, not by the housekeeping sweep, so changing it moves no object's deletion
/// date today. Stage 1 says so explicitly rather than letting an empty result read as a broken preview.
/// </summary>
public class MetaverseObjectTypeDeletionSettingsPreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;

    /// <summary>
    /// How a deletion date is written into a delta row. Sortable and unambiguous, and stated in UTC because that is
    /// what the housekeeping sweep compares against; rendering a local time here would have an administrator
    /// checking a deadline against the wrong clock.
    /// </summary>
    private const string DeletionDateFormat = "yyyy-MM-dd HH:mm:ss 'UTC'";

    public MetaverseObjectTypeDeletionSettingsPreviewAdapter(JimApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.MetaverseObjectType;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(MetaverseObjectTypeDeletionSettingsProposal);

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<MetaverseObjectTypeDeletionSettingsProposal>();
        var objectType = await GetObjectTypeAsync(context);
        var findings = new List<PreviewValidationFinding>();

        if (proposal.DeletionRule == MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected &&
            proposal.DeletionTriggerConnectedSystemIds.Count == 0)
        {
            // Blocking rather than a warning because the engine's fallback for this state is to behave as
            // WhenLastConnectorDisconnected. An administrator who chose the authoritative-source rule and got the
            // other one has a configuration that does something they did not ask for, silently.
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Blocking,
                "Deleting when an authoritative source disconnects needs at least one Connected System named as an " +
                "authoritative source. Without one, synchronisation falls back to deleting when the last connector " +
                "disconnects, which is a different rule from the one selected.",
                nameof(MetaverseObjectType.DeletionTriggerConnectedSystemIds)));
        }

        if (proposal.DeletionGracePeriod is { } grace && grace < TimeSpan.Zero)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Blocking,
                "A Deletion Grace Period cannot be negative.",
                nameof(MetaverseObjectType.DeletionGracePeriod)));
        }

        var sourcesChanged = !proposal.DeletionTriggerConnectedSystemIds.OrderBy(id => id)
            .SequenceEqual(objectType.DeletionTriggerConnectedSystemIds.OrderBy(id => id));
        var modeChanged = proposal.DeletionTriggerMode != objectType.DeletionTriggerMode;

        if (sourcesChanged || modeChanged)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "The authoritative sources have changed. This decides what happens the next time a Connected System " +
                "Object disconnects, so it moves no Metaverse Object's deletion date today and is not counted below.",
                sourcesChanged
                    ? nameof(MetaverseObjectType.DeletionTriggerConnectedSystemIds)
                    : nameof(MetaverseObjectType.DeletionTriggerMode)));
        }

        return findings;
    }

    public async Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var affected = await _application.Metaverse.GetMetaverseObjectDeletionCandidateCountAsync(TargetIdOf(context));
        return new PreviewCostEstimate(affected);
    }

    /// <summary>
    /// Counted by streaming the same narrow projection the delta stage reads, rather than by a set of SQL count
    /// queries. A deliberate departure from the contract's "set-based SQL only", for one reason: the eligibility
    /// rule would otherwise exist twice, and a preview whose counts disagreed with its own drill-down about
    /// deletions is exactly the defect this framework exists to prevent. The population is bounded by the objects
    /// awaiting deletion rather than by the metaverse, and where it is large the framework's dispatch decision
    /// hands the whole preview to JIM.Worker, which is what that decision is for.
    /// </summary>
    public async Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var counts = new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int>();
        await foreach (var delta in EvaluateDeltasAsync(context, CancellationToken.None))
            counts[delta.TransitionType] = counts.GetValueOrDefault(delta.TransitionType) + 1;

        return
        [
            .. counts
                .OrderByDescending(c => c.Value)
                .ThenBy(c => c.Key)
                .Select(c => new PreviewImpactCount(c.Key, c.Value, MetaverseObjectTypeId: TargetIdOf(context)))
        ];
    }

    public async IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<MetaverseObjectTypeDeletionSettingsProposal>();
        var objectType = await GetObjectTypeAsync(context);

        var current = MetaverseObjectDeletionSettings.From(objectType);
        var proposed = proposal.ToSettings();

        // Named once for the whole proposal because it is a property of the proposal, not of any object: every
        // affected object was affected by the same edit. Empty means neither setting moved, and no object's fate
        // can have changed, so the population is not read at all.
        var changedSettings = DescribeChangedSettings(current, proposed);
        if (changedSettings is null)
            yield break;

        // One clock for the whole evaluation. Reading DateTime.UtcNow per object would let a long stream classify
        // two identical objects differently depending on when each was reached.
        var asAt = DateTime.UtcNow;

        await foreach (var candidate in _application.Metaverse
            .StreamMetaverseObjectDeletionCandidates(objectType.Id)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var delta = Classify(candidate, current, proposed, asAt, objectType, changedSettings);
            if (delta is not null)
                yield return delta;
        }
    }

    private static PreviewDelta? Classify(MetaverseObjectDeletionCandidate candidate,
        MetaverseObjectDeletionSettings current, MetaverseObjectDeletionSettings proposed, DateTime asAt,
        MetaverseObjectType objectType, string changedSettings)
    {
        var currentDate = current.DeletionEligibleAt(candidate.LastConnectorDisconnectedDate, candidate.HasConnectedSystemObjects);
        var proposedDate = proposed.DeletionEligibleAt(candidate.LastConnectorDisconnectedDate, candidate.HasConnectedSystemObjects);

        var eligibleNow = currentDate is { } currentAt && currentAt <= asAt;
        var eligibleProposed = proposedDate is { } proposedAt && proposedAt <= asAt;

        // The three questions in the order they matter. "Would this delete something that is safe today" first,
        // because it is the one an administrator is consenting to; "would this stop a deletion" second; and only
        // then the objects whose deletion merely moves, which is worth reporting but is not a deletion today.
        ActivityRunProfileExecutionItemSyncOutcomeType transition;
        if (!eligibleNow && eligibleProposed)
            transition = ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible;
        else if (eligibleNow && !eligibleProposed)
            transition = ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible;
        else if (currentDate != proposedDate)
            transition = ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate;
        else
            return null;

        return new PreviewDelta(
            transition,
            ObjectDisplayName: candidate.DisplayName,
            ObjectTypeName: objectType.Name,
            MetaverseObjectTypeId: objectType.Id,
            MetaverseObjectId: candidate.Id,
            AttributeName: changedSettings,
            OldValue: FormatDeletionDate(currentDate),
            NewValue: FormatDeletionDate(proposedDate));
    }

    /// <summary>
    /// Which settings moved, phrased as the thing a summary group is named by, or null when neither did.
    /// </summary>
    private static string? DescribeChangedSettings(MetaverseObjectDeletionSettings current, MetaverseObjectDeletionSettings proposed)
    {
        var ruleChanged = current.Rule != proposed.Rule;

        // Null and zero are the same grace period as far as deletion is concerned, so an edit between them is not
        // a change and must not produce a preview full of objects whose dates are identical.
        var graceChanged = Normalise(current.GracePeriod) != Normalise(proposed.GracePeriod);

        return (ruleChanged, graceChanged) switch
        {
            (true, true) => "Deletion Rule and Deletion Grace Period",
            (true, false) => "Deletion Rule",
            (false, true) => "Deletion Grace Period",
            _ => null
        };
    }

    private static TimeSpan Normalise(TimeSpan? gracePeriod) =>
        gracePeriod is { } grace && grace > TimeSpan.Zero ? grace : TimeSpan.Zero;

    private static string? FormatDeletionDate(DateTime? deletionDate) =>
        deletionDate?.ToString(DeletionDateFormat, System.Globalization.CultureInfo.InvariantCulture);

    private async Task<MetaverseObjectType> GetObjectTypeAsync(PreviewContext context)
    {
        var id = TargetIdOf(context);
        return await _application.Metaverse.GetMetaverseObjectTypeAsync(id, includeChildObjects: false)
            ?? throw new InvalidOperationException(
                $"Cannot preview deletion settings for Metaverse Object Type {id}: it no longer exists.");
    }

    private static int TargetIdOf(PreviewContext context) =>
        context.TargetId ?? throw new InvalidOperationException(
            "A Metaverse Object Type deletion settings preview must name the object type it concerns.");
}
