// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Runtime.CompilerServices;
using JIM.Application.Exceptions;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What deleting a Connected System with Synchronised Deprovisioning would do (#134): which Metaverse values it would
/// clear or hand to another contributor, which objects would become eligible for deletion, and what every other
/// Connected System would be sent as a result.
///
/// The evaluation is the deprovisioning itself, run read-only
/// (<see cref="ConnectedSystemServer.PreviewSynchronisedDeprovisioningAsync"/>): the same obsoletion core, the same
/// re-election and the same export decisions, so the preview and the deletion it describes cannot disagree. Its
/// agreement with the real run is proven fact by fact in the workflow tests. This adapter adds only what the framework
/// asks of every surface around that: what is refused or caveated before anything is evaluated, an estimate of the
/// work, and counts an administrator can consent to.
/// </summary>
public class ConnectedSystemDeletionPreviewAdapter : IConfigurationChangePreviewAdapter
{
    /// <summary>
    /// The transitions whose subject is an object in another Connected System rather than the Metaverse Object it is
    /// joined to: "objects would be updated in their target Connected System" counts the target's objects, so one
    /// person provisioned to two targets is two of them.
    /// </summary>
    private static readonly HashSet<ActivityRunProfileExecutionItemSyncOutcomeType> TargetObjectTransitions =
    [
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject,
        ActivityRunProfileExecutionItemSyncOutcomeType.ProvisioningCancelled,
        ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportChangesWithdrawn
    ];

    private readonly JimApplication _application;

    public ConnectedSystemDeletionPreviewAdapter(JimApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.ConnectedSystemDeletion;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(ConnectedSystemDeletionProposal);

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(ConnectedSystemId(context));
        if (connectedSystem == null)
        {
            return
            [
                new PreviewValidationFinding(PreviewValidationSeverity.Blocking,
                    "This Connected System no longer exists, so there is no deletion to preview.")
            ];
        }

        if (connectedSystem.Status == ConnectedSystemStatus.Deleting)
        {
            return
            [
                new PreviewValidationFinding(PreviewValidationSeverity.Blocking,
                    "This Connected System is already being deleted. Part of what a preview would describe has " +
                    "already happened, so it cannot be previewed; its deletion Activity reports the progress.",
                    nameof(connectedSystem.Status))
            ];
        }

        try
        {
            var derivedReaders = await _application.ConnectedSystems.GetDerivedFlowsReadingDeprovisionedAttributesAsync(connectedSystem.Id);
            return
            [
                .. derivedReaders.Select(reader => new PreviewValidationFinding(
                    PreviewValidationSeverity.Warning,
                    $"Derived flows on {string.Join(", ", reader.HostingSystemNames)} read '{reader.MetaverseAttributeName}', " +
                    "which this deletion clears or changes. The deletion marks the objects affected so those flows " +
                    "recompute at the next synchronisation of the system hosting them; this preview shows the change " +
                    $"to '{reader.MetaverseAttributeName}' but not the derived values that follow from it.",
                    MetaverseAttributeName: reader.MetaverseAttributeName))
            ];
        }
        catch (DerivedFlowCycleException exception)
        {
            return
            [
                new PreviewValidationFinding(PreviewValidationSeverity.Blocking,
                    "The remaining Synchronisation Rules' derived flows form a cycle, which Synchronised Deprovisioning " +
                    $"refuses to run with: {exception.Message}")
            ];
        }
    }

    public Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Each object can move every attribute the system contributes, which is the dominant term; the export rows that
        // follow are bounded by the same attributes per target, so this tracks the real shape rather than a constant
        // measured on one fixture.
        return _application.ConnectedSystems.EstimateSynchronisedDeprovisioningPreviewAsync(ConnectedSystemId(context));
    }

    /// <remarks>
    /// Counted by streaming <see cref="EvaluateDeltasAsync"/>, as every engine-driven adapter counts, rather than from
    /// set-based SQL as the framework prefers: which values a deletion clears and which it hands to another
    /// contributor is decided by Attribute Priority re-election per object, and no count query can answer that without
    /// re-implementing it. One count per transition, of distinct objects, because the verdict states each as "N objects
    /// would ..." and an object losing three values has lost them once.
    /// </remarks>
    public async Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context) =>
        await PreviewImpactCounter.CountAsync((await CreateImpactCounterAsync(context))!, EvaluateDeltasAsync(context, CancellationToken.None));

    /// <summary>
    /// Stage 2 counted in the framework's one evaluation pass (#1530): distinct objects per transition, exactly as
    /// <see cref="CountImpactAsync"/> counts.
    /// </summary>
    public Task<IPreviewImpactCounter?> CreateImpactCounterAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult<IPreviewImpactCounter?>(PreviewImpactCounter.PerSubject(SubjectOf));
    }

    public async IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await foreach (var delta in _application.ConnectedSystems.PreviewSynchronisedDeprovisioningAsync(ConnectedSystemId(context), cancellationToken))
            yield return delta;
    }

    /// <summary>
    /// Adds the object a delta is about to its transition's set: the target's object for an export transition, the
    /// Metaverse Object otherwise.
    /// </summary>
    /// <summary>
    /// The object a delta counts against: the target account for a transition that happens to one, else the Metaverse
    /// Object.
    /// </summary>
    private static Guid? SubjectOf(PreviewDelta delta) =>
        TargetObjectTransitions.Contains(delta.TransitionType)
            ? delta.ConnectedSystemObjectId ?? delta.MetaverseObjectId
            : delta.MetaverseObjectId ?? delta.ConnectedSystemObjectId;

    private static int ConnectedSystemId(PreviewContext context) =>
        context.TargetId ?? throw new ArgumentException("A Connected System deletion preview needs the system as its target.", nameof(context));
}
