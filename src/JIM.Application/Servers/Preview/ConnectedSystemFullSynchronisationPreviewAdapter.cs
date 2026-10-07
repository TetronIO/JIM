// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// run's own obsoletion core, the unchanged-object optimisation, and the export scope review drained after the walk.
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

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(ConnectedSystemId(context));
        if (connectedSystem == null)
        {
            return [Blocking("This Connected System no longer exists, so there is no Full Synchronisation to preview.")];
        }

        if (connectedSystem.Status == ConnectedSystemStatus.Deleting)
        {
            return [Blocking("This Connected System is being deleted, so it will not be synchronised again.", nameof(connectedSystem.Status))];
        }

        var proposal = Proposal(context);
        if (proposal.MaxObjects is < 1)
        {
            return [Blocking("A preview capped at fewer than one object would evaluate nothing. Leave the cap out to evaluate every object.",
                nameof(proposal.MaxObjects))];
        }

        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystem.Id);
        if (!runProfiles.Exists(profile => profile.RunType == ConnectedSystemRunType.FullSynchronisation))
        {
            return [Blocking("This Connected System has no Full Synchronisation Run Profile, so a Full Synchronisation cannot be run. " +
                "Create one on its Run Profiles tab, then preview it.")];
        }

        if (await _application.ConnectedSystems.GetDerivedFlowCycleAsync() is { } cycle)
        {
            return [Blocking("A Full Synchronisation would refuse to start, because the enabled derived Attribute Flows form a " +
                $"dependency cycle: {cycle}")];
        }

        var findings = new List<PreviewValidationFinding>();
        if (proposal.MaxObjects is { } cap)
        {
            var population = await _application.SyncPreview.GetFullSyncPopulationAsync(connectedSystem.Id);
            if (cap < population)
            {
                findings.Add(new PreviewValidationFinding(PreviewValidationSeverity.Warning,
                    $"This preview evaluates only the first {cap:N0} of the system's {population:N0} objects. Its counts describe " +
                    "those objects, not the whole system, and a Full Synchronisation could do more than it shows.",
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

        var population = await _application.SyncPreview.GetFullSyncPopulationAsync(ConnectedSystemId(context));
        var cap = Proposal(context).MaxObjects;
        return new PreviewCostEstimate(cap.HasValue ? Math.Min(cap.Value, population) : population);
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
        TimeSpan? GracePeriodOf(int? typeId) => typeId.HasValue ? gracePeriods.GetValueOrDefault(typeId.Value) : null;

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

                case FullSyncPreviewItemKind.Unchanged:
                    objects++;
                    yield return FullSynchronisationPreviewDeltas.Unchanged(item, connectedSystemId);
                    break;

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

    private static PreviewValidationFinding Blocking(string message, string? field = null) =>
        new(PreviewValidationSeverity.Blocking, message, field);

    private static ConnectedSystemFullSynchronisationProposal Proposal(PreviewContext context) =>
        context.ProposedConfiguration as ConnectedSystemFullSynchronisationProposal ?? new ConnectedSystemFullSynchronisationProposal();

    private static int ConnectedSystemId(PreviewContext context) =>
        context.TargetId ?? throw new ArgumentException("A Full Synchronisation preview needs the Connected System as its target.", nameof(context));
}
