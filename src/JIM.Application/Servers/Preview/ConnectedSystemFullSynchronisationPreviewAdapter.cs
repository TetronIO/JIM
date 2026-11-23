// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Localisation;
using System.Runtime.CompilerServices;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What a Full Synchronisation of a Connected System would do (#1530), stated in the rows every Configuration Change
/// Preview states: who would project, join, leave or be deleted, which Metaverse values would change, and what every
/// target system would be sent, with how many objects would not change at all.
///
/// The evaluation is the Sync Preview Engine's whole-system walk
/// (<see cref="SyncPreviewServer.StreamFullSyncPreviewAsync"/>), which takes the run's semantics: what each object's
/// own changes export, drift corrected only in the system being synchronised, obsolete objects torn down through the
/// run's own obsoletion core, and the export scope review drained after the walk.
/// Its agreement with the run is proven per case in the fidelity tests. This adapter turns each item into rows
/// (<see cref="FullSynchronisationPreviewDeltas"/>) and adds what the framework asks of every surface: what is refused
/// or caveated before anything is evaluated, an estimate, and counts.
/// </summary>
public class ConnectedSystemFullSynchronisationPreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;

    public ConnectedSystemFullSynchronisationPreviewAdapter(JimApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(ConnectedSystemFullSynchronisationProposal);

    /// <inheritdoc />
    public Task<IReadOnlyCollection<int>> GetEvaluatedConnectorSpacesAsync(PreviewContext context) =>
        Task.FromResult(PreviewConnectorSpaces.Of(ConnectedSystemId(context)));

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(ConnectedSystemId(context));
        if (connectedSystem == null)
        {
            return [Blocking(JimMessage.Create(ApplicationMessageCatalogues.Preview, nameof(PreviewMessages.FullSyncSystemGone)))];
        }

        if (connectedSystem.Status == ConnectedSystemStatus.Deleting)
        {
            return [Blocking(JimMessage.Create(ApplicationMessageCatalogues.Preview, nameof(PreviewMessages.FullSyncSystemDeleting)), nameof(connectedSystem.Status))];
        }

        var proposal = Proposal(context);
        if (proposal.MaxObjects is < 1)
        {
            return [Blocking(JimMessage.Create(ApplicationMessageCatalogues.Preview, nameof(PreviewMessages.FullSyncCapTooLow)),
                nameof(proposal.MaxObjects))];
        }

        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystem.Id);
        if (!runProfiles.Exists(profile => profile.RunType == ConnectedSystemRunType.FullSynchronisation))
        {
            return [Blocking(JimMessage.Create(ApplicationMessageCatalogues.Preview, nameof(PreviewMessages.FullSyncNoRunProfile)))];
        }

        if (await _application.ConnectedSystems.GetDerivedFlowCycleAsync() is { } cycle)
        {
            return [Blocking(JimMessage.Create(ApplicationMessageCatalogues.Preview, nameof(PreviewMessages.FullSyncDerivedFlowCycle), cycle))];
        }

        // A stopped import (#2521) is warned of by the framework, from GetEvaluatedConnectorSpacesAsync (#3014).
        var findings = new List<PreviewValidationFinding>();
        if (proposal.MaxObjects is { } cap)
        {
            var population = await _application.SyncPreview.GetFullSyncPopulationAsync(connectedSystem.Id);
            if (cap < population)
            {
                findings.Add(PreviewValidationFinding.Of(PreviewValidationSeverity.Warning,
                    JimMessage.Create(ApplicationMessageCatalogues.Preview, nameof(PreviewMessages.FullSyncCapped), cap, population),
                    nameof(proposal.MaxObjects)));
            }
        }

        return findings;
    }

    /// <remarks>
    /// Every object is evaluated, and each yields at least one row: most objects of a repeat Full Synchronisation would
    /// not change, and say so in one row; the changed minority add a row per attribute, which the framework's per-group
    /// cap bounds. So the population is the estimate, one row each.
    /// </remarks>
    public async Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connectedSystemId = ConnectedSystemId(context);
        var population = await _application.SyncPreview.GetFullSyncPopulationAsync(connectedSystemId);
        var cap = Proposal(context).MaxObjects;
        var evaluated = cap is { } limit ? Math.Min(limit, population) : population;

        // The last run is measured over the system's population, not its Activity's ObjectsToProcess: each counting step
        // of a run resets that counter, so on a completed run it holds whichever step counted last, not the objects the
        // run went through.
        var lastRun = await _application.Activities.GetLatestCompletedFullSynchronisationAsync(connectedSystemId);
        return new PreviewCostEstimate(evaluated,
            EstimatedDuration: FullSynchronisationDurationEstimate.For(evaluated, lastRun is null ? null : population, lastRun?.ExecutionTime));
    }

    /// <remarks>
    /// Counted by evaluating, as every engine-driven adapter counts: what a Full Synchronisation does to each object is
    /// the whole engine's answer, and no count query can give it. Distinct objects per transition, an object in a
    /// target system told apart by system, because the verdict states each as "N objects would ...".
    /// </remarks>
    public async Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context) =>
        await PreviewImpactCounter.CountAsync((await CreateImpactCounterAsync(context))!, EvaluateDeltasAsync(context, CancellationToken.None));

    /// <summary>
    /// Stage 2 counted in the framework's one evaluation pass, exactly as <see cref="CountImpactAsync"/> counts.
    /// </summary>
    public Task<IPreviewImpactCounter?> CreateImpactCounterAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult<IPreviewImpactCounter?>(PreviewImpactCounter.PerSubject(FullSynchronisationPreviewDeltas.SubjectOf));
    }

    public async IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connectedSystemId = ConnectedSystemId(context);
        var gracePeriods = (await _application.Metaverse.GetMetaverseObjectTypesAsync(false))
            .ToDictionary(type => type.Id, type => type.DeletionGracePeriod);
        TimeSpan? GracePeriodOf(int? typeId) => typeId is { } id ? gracePeriods.GetValueOrDefault(id) : null;

        var options = new FullSyncPreviewStreamOptions { MaxObjects = Proposal(context).MaxObjects };
        var objects = 0;
        await foreach (var item in _application.SyncPreview.StreamFullSyncPreviewAsync(connectedSystemId, options, cancellationToken: cancellationToken))
        {
            switch (item.Kind)
            {
                // Validation refuses a cycle before the preview starts; one appearing since is a refusal all the same,
                // and a preview that pretended to have evaluated would be the wrong answer stated confidently.
                case FullSyncPreviewItemKind.Refused:
                    throw new InvalidOperationException("A Full Synchronisation of this Connected System would refuse to start: " +
                        string.Join(" ", item.Preview?.Errors.Select(error => error.Detail) ?? []));

                case FullSyncPreviewItemKind.Evaluated:
                case FullSyncPreviewItemKind.Obsolete:
                    objects++;
                    foreach (var delta in FullSynchronisationPreviewDeltas.ForObject(item, connectedSystemId, GracePeriodOf))
                        yield return delta;
                    break;

                case FullSyncPreviewItemKind.ExportScopeReview:
                    foreach (var delta in FullSynchronisationPreviewDeltas.ForExportScopeReview(item))
                        yield return delta;
                    break;

                // A cap the administrator chose stops the walk; validation has already said the counts describe only
                // the objects evaluated.
                case FullSyncPreviewItemKind.Truncated:
                    Log.Information("ConnectedSystemFullSynchronisationPreviewAdapter: Full Synchronisation preview of Connected System {SystemId} stopped at its cap after {Objects} object(s).",
                        connectedSystemId, objects);
                    yield break;
            }
        }
    }

    private static PreviewValidationFinding Blocking(JimMessage message, string? field = null) =>
        PreviewValidationFinding.Of(PreviewValidationSeverity.Blocking, message, field);

    private static ConnectedSystemFullSynchronisationProposal Proposal(PreviewContext context) =>
        context.ProposedConfiguration as ConnectedSystemFullSynchronisationProposal ?? new ConnectedSystemFullSynchronisationProposal();

    private static int ConnectedSystemId(PreviewContext context) =>
        context.TargetId ?? throw new ArgumentException("A Full Synchronisation preview needs the Connected System as its target.", nameof(context));
}
