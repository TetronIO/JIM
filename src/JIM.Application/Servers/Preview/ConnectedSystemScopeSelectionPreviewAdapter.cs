// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Utilities;
using System.Runtime.CompilerServices;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What a change to a Connected System's partition and container selection would do to the objects JIM already
/// holds from it (#1251, gap G4).
/// </summary>
/// <remarks>
/// Deselecting a container is one tick box and a cascade. The objects beneath it stop being searched, so the next
/// Full Import does not return them, so they are marked obsolete, so the following synchronisation disconnects them,
/// recalls whatever they contributed to the Metaverse, and may leave their Metaverse Objects with no connectors at
/// all. Nothing today states any of that before the tick box is saved.
///
/// **Container membership is derived, never stored.** A Connected System Object's container is a function of the
/// identifier the Connector already keeps current (the Distinguished Name, for a directory, which is the object's
/// secondary external ID and is maintained by both the import path and the export confirmation path). Adding a
/// stored container reference to <see cref="ConnectedSystemObject"/> would look tidier and would be wrong: objects
/// move between containers by design (an Export Attribute Flow that rewrites the Distinguished Name on disable moves
/// an account to the disabled-users container), so the stored value would be a second denormalisation maintained by
/// two synchronisation hot-path writers. When it drifted it would drift silently, and what it would corrupt is a
/// destructive count an administrator is about to consent to. Deriving membership introduces no new invariant at
/// all; it inherits one that already exists and is already tested.
///
/// **Containment is the Connector's rule, not the framework's.** The adapter asks through
/// <see cref="JIM.Models.Interfaces.IConnectorContainment"/>, which is the same rule export enforces when it refuses
/// to write outside the managed scope (#1250). A Connector with containers that cannot express containment gets a
/// preview that says so, rather than a zero that reads as "this change would affect nothing".
/// </remarks>
public class ConnectedSystemScopeSelectionPreviewAdapter : IConfigurationChangePreviewAdapter
{
    private readonly JimApplication _application;
    private readonly ISyncEngine _syncEngine;

    /// <summary>
    /// How a scope transition is written into a delta row's value columns.
    /// </summary>
    private const string InScope = "In import scope";
    private const string OutOfScope = "Out of import scope";
    private const string ScopeAttributeName = "Import scope";

    public ConnectedSystemScopeSelectionPreviewAdapter(JimApplication application, ISyncEngine syncEngine)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _syncEngine = syncEngine ?? throw new ArgumentNullException(nameof(syncEngine));
    }

    public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.ConnectedSystem;

    public bool ProducesDeltas => true;

    public Type ProposalType => typeof(ConnectedSystemScopeSelectionProposal);

    public async Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<ConnectedSystemScopeSelectionProposal>();
        var connectedSystem = await GetConnectedSystemAsync(context);
        var findings = new List<PreviewValidationFinding>();

        if (connectedSystem.ConnectorDefinition?.SupportsPartitions != true)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Blocking,
                $"'{connectedSystem.Name}' uses a Connector that does not have partitions, so there is no partition " +
                "or container selection to preview.",
                nameof(ConnectedSystem.Partitions)));

            // Nothing below this can say anything useful about a system with no partitions.
            return findings;
        }

        // Evaluated against a Connected System carrying the proposed selection rather than the saved one, because
        // the question is whether the *proposal* leaves a workable configuration.
        if (!WithSelection(connectedSystem, proposal).HasPartitionsOrContainersSelected())
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                "This selection leaves nothing for JIM to manage on this Connected System: " +
                $"{WithSelection(connectedSystem, proposal).BuildPartitionSelectionDiagnostic()}. Run Profiles cannot " +
                "be executed against it, and every object already imported would be taken out of scope.",
                nameof(ConnectedSystemPartition.Selected)));
        }

        var inoperableRunProfiles = InoperableRunProfileNames(connectedSystem, proposal);
        if (inoperableRunProfiles.Count > 0)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                $"{inoperableRunProfiles.Count} Run Profile(s) target a partition this selection deselects and would " +
                $"be refused when run: {string.Join(", ", inoperableRunProfiles)}. Point them at a selected partition, " +
                "or keep the partition selected.",
                nameof(ConnectedSystemRunProfile.Partition)));
        }

        if (connectedSystem.ConnectorDefinition.SupportsPartitionContainers &&
            _application.ConnectedSystems.GetConnectorContainment(connectedSystem) is null)
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Warning,
                $"The '{connectedSystem.ConnectorDefinition.Name}' Connector has containers but cannot say whether an " +
                "object sits inside one, so this preview counts only what changing the partition selection would do. " +
                "Container changes are not measured, and the counts below are not a complete answer.",
                nameof(ConnectedSystemContainer.Selected)));
        }

        if (SelectsSomethingNew(connectedSystem, proposal))
        {
            findings.Add(new PreviewValidationFinding(
                PreviewValidationSeverity.Information,
                "This selection brings scope into JIM that it does not currently import. Objects JIM already holds " +
                "there are counted below; objects it has never imported cannot be, because there is nothing to count " +
                "until a Full Import runs and discovers them.",
                nameof(ConnectedSystemContainer.Selected)));
        }

        return findings;
    }

    public async Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The whole connector space, because a proposal can move objects in either direction and the objects
        // entering scope are exactly the ones the current selection excludes. Set-based and indexed.
        var affected = await _application.ConnectedSystems.GetConnectedSystemObjectCountAsync(TargetIdOf(context));
        return new PreviewCostEstimate(affected);
    }

    /// <summary>
    /// Counted by streaming the same evaluation the delta stage reads, rather than by a set of SQL count queries.
    /// </summary>
    /// <remarks>
    /// A deliberate departure from the contract's "set-based SQL only", for the reason the pilot adapter departed
    /// from it: containment is the Connector's rule and cannot be expressed as SQL without reimplementing it, and a
    /// preview whose counts disagreed with its own drill-down about disconnections is precisely the defect this
    /// framework exists to prevent. Where the population is large the framework's dispatch decision hands the whole
    /// preview to JIM.Worker, which is what that decision is for.
    /// </remarks>
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
                .Select(c => new PreviewImpactCount(c.Key, c.Value, ConnectedSystemId: TargetIdOf(context)))
        ];
    }

    public async IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var proposal = context.ProposedAs<ConnectedSystemScopeSelectionProposal>();
        var connectedSystem = await GetConnectedSystemAsync(context);

        var currentSelection = ConnectedSystemScopeSelectionProposal.FromCurrentSelection(connectedSystem);
        if (currentSelection.DescribesSameSelectionAs(proposal))
            yield break;

        var containment = _application.ConnectedSystems.GetConnectorContainment(connectedSystem);
        var currentScope = ConnectedSystemScope.From(connectedSystem, currentSelection, containment);
        var proposedScope = ConnectedSystemScope.From(connectedSystem, proposal, containment);

        // One entry per Metaverse Object that would lose a connector, counting how many of its Connected System
        // Objects in *this* system leave. Bounded by the objects leaving scope rather than by the connector space,
        // and only the joined ones are recorded, because a disconnector leaving scope has no Metaverse consequence.
        var disconnectionsByMetaverseObject = new Dictionary<Guid, int>();

        await foreach (var candidate in _application.ConnectedSystems
            .StreamConnectedSystemObjectScopeCandidates(connectedSystem.Id)
            .WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inScopeNow = currentScope.Contains(candidate.PartitionId, candidate.ContainerIdentifier);
            var inScopeProposed = proposedScope.Contains(candidate.PartitionId, candidate.ContainerIdentifier);

            // Undetermined either side means this object cannot be spoken for: it predates partition tracking, has
            // no identifier to test containment against, or the Connector cannot express containment. Counting it
            // as leaving would overstate a destructive change; counting it as staying would hide one.
            if (inScopeNow is not { } wasInScope || inScopeProposed is not { } wouldBeInScope)
                continue;

            if (wasInScope && !wouldBeInScope)
            {
                if (candidate.MetaverseObjectId is { } metaverseObjectId)
                {
                    disconnectionsByMetaverseObject[metaverseObjectId] =
                        disconnectionsByMetaverseObject.GetValueOrDefault(metaverseObjectId) + 1;

                    yield return ScopeDelta(candidate,
                        ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject,
                        connectedSystem.Id, InScope, OutOfScope);
                }
                else
                {
                    yield return ScopeDelta(candidate,
                        ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope,
                        connectedSystem.Id, InScope, OutOfScope);
                }
            }
            else if (!wasInScope && wouldBeInScope)
            {
                yield return ScopeDelta(candidate,
                    ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope,
                    connectedSystem.Id, OutOfScope, InScope);
            }
        }

        // The Metaverse consequence, evaluated only once the disconnections are known, because whether an object
        // becomes eligible for deletion depends on how many of its connectors survive and this system may hold more
        // than one of them. Shared with the other disconnecting adapters so no two previews can disagree about
        // whether an object dies.
        await foreach (var delta in PreviewDeletionEligibilityEvaluator.EvaluateAsync(
                           _application, _syncEngine, connectedSystem.Id, disconnectionsByMetaverseObject, cancellationToken))
            yield return delta;
    }

    private static PreviewDelta ScopeDelta(
        Models.Staging.DTOs.ConnectedSystemObjectScopeCandidate candidate,
        ActivityRunProfileExecutionItemSyncOutcomeType transition,
        int connectedSystemId,
        string oldValue,
        string newValue) =>
        new(transition,
            // The container identifier is the object's most recognisable name to an administrator looking at a
            // container tree, and it is the value the scope decision was actually made on.
            ObjectDisplayName: candidate.ContainerIdentifier,
            ObjectTypeName: candidate.ObjectTypeName,
            ConnectedSystemObjectId: candidate.Id,
            ConnectedSystemId: connectedSystemId,
            MetaverseObjectId: candidate.MetaverseObjectId,
            AttributeName: ScopeAttributeName,
            OldValue: oldValue,
            NewValue: newValue);

    /// <summary>
    /// A shallow view of the Connected System carrying the proposed selection instead of the saved one, for asking
    /// the existing selection-validity helpers about the proposal. The partitions and containers themselves are
    /// shared with the loaded graph, so this must never be persisted; it exists to be read.
    /// </summary>
    private static ConnectedSystem WithSelection(ConnectedSystem connectedSystem, ConnectedSystemScopeSelectionProposal proposal)
    {
        var partitionIds = proposal.SelectedPartitionIds.ToHashSet();
        var containerIds = proposal.SelectedContainerIds.ToHashSet();
        var excludedIds = (proposal.ExcludedContainerIds ?? []).ToHashSet();

        return new ConnectedSystem
        {
            Id = connectedSystem.Id,
            Name = connectedSystem.Name,
            ConnectorDefinition = connectedSystem.ConnectorDefinition,
            Partitions =
            [
                .. (connectedSystem.Partitions ?? []).Select(partition => new ConnectedSystemPartition
                {
                    Id = partition.Id,
                    Name = partition.Name,
                    ExternalId = partition.ExternalId,
                    Selected = partitionIds.Contains(partition.Id),
                    Containers = partition.Containers == null
                        ? null
                        : CloneWithSelection(partition.Containers, containerIds, excludedIds)
                })
            ]
        };
    }

    private static HashSet<ConnectedSystemContainer> CloneWithSelection(
        IEnumerable<ConnectedSystemContainer> containers,
        IReadOnlySet<int> selectedContainerIds,
        IReadOnlySet<int> excludedContainerIds) =>
        [.. containers.Select(container => CloneWithSelection(container, selectedContainerIds, excludedContainerIds))];

    private static ConnectedSystemContainer CloneWithSelection(
        ConnectedSystemContainer container,
        IReadOnlySet<int> selectedContainerIds,
        IReadOnlySet<int> excludedContainerIds)
    {
        // Both halves of the proposal are carried, not just the selections. No helper reading this view asks about
        // exclusions today, and a clone that silently dropped half of what a selection says is the kind of thing a
        // later reader takes at face value.
        var clone = new ConnectedSystemContainer
        {
            Id = container.Id,
            Name = container.Name,
            ExternalId = container.ExternalId,
            Selected = selectedContainerIds.Contains(container.Id),
            Excluded = excludedContainerIds.Contains(container.Id)
        };

        clone.ChildContainers.UnionWith(
            CloneWithSelection(container.ChildContainers, selectedContainerIds, excludedContainerIds));
        return clone;
    }

    /// <summary>
    /// The Run Profiles this proposal would leave unable to run: those naming a partition it deselects.
    /// </summary>
    private static List<string> InoperableRunProfileNames(
        ConnectedSystem connectedSystem, ConnectedSystemScopeSelectionProposal proposal)
    {
        var partitionIds = proposal.SelectedPartitionIds.ToHashSet();
        return
        [
            .. (connectedSystem.RunProfiles ?? [])
                .Where(runProfile => runProfile.Partition != null && !partitionIds.Contains(runProfile.Partition.Id))
                .Select(runProfile => $"'{runProfile.Name}'")
                .Order(StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// Whether the proposal brings scope in that JIM does not import today, which is what makes it discover
    /// objects it has never held.
    /// </summary>
    /// <remarks>
    /// Three ways in, not two: a newly selected partition, a newly selected container, and an exclusion the
    /// proposal lifts (#1255). The third is easy to overlook because no tick box moves, and it is the one that can
    /// bring the most in at once: a carve-out sits inside a branch that is already selected, so lifting it exposes
    /// everything beneath it in a single step.
    /// </remarks>
    private static bool SelectsSomethingNew(
        ConnectedSystem connectedSystem, ConnectedSystemScopeSelectionProposal proposal)
    {
        var current = ConnectedSystemScopeSelectionProposal.FromCurrentSelection(connectedSystem);
        return proposal.SelectedPartitionIds.Except(current.SelectedPartitionIds).Any() ||
               proposal.SelectedContainerIds.Except(current.SelectedContainerIds).Any() ||
               (current.ExcludedContainerIds ?? []).Except(proposal.ExcludedContainerIds ?? []).Any();
    }

    private async Task<ConnectedSystem> GetConnectedSystemAsync(PreviewContext context)
    {
        var id = TargetIdOf(context);
        return await _application.ConnectedSystems.GetConnectedSystemAsync(id)
            ?? throw new InvalidOperationException(
                $"Cannot preview the partition and container selection for Connected System {id}: it no longer exists.");
    }

    private static int TargetIdOf(PreviewContext context) =>
        context.TargetId ?? throw new InvalidOperationException(
            "A partition and container selection preview must name the Connected System it concerns.");
}
