// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;

namespace JIM.Application.Services;

/// <summary>
/// Works out why an imported reference value most likely could not be resolved to a Connected System Object, and
/// what to do about it (#959).
/// </summary>
/// <remarks>
/// Judged from the Connected System's <b>current</b> Container Scope, with the Connector's own containment rule
/// (<see cref="IConnectorContainment"/>) and the same scope evaluation the import, export and the scope preview use
/// (<see cref="ConnectedSystemScope"/>, <see cref="ContainerSpecificity"/>). Nothing here knows what a
/// Distinguished Name is: a Connector that cannot say what its Containers hold gets <see cref="UnresolvedReferenceCause.NotFound"/>
/// rather than a guess. Nothing is stored, so widening the scope changes the answer the next time someone looks.
///
/// Checked in order, the first match deciding: ambiguous across Object Types; outside every partition; in a partition
/// that is not selected; in scope yet not found; carved out by an exclusion; covered by no selection.
/// Build one per Connected System and ask it per value: the partition and Container sets are prepared once.
/// </remarks>
public sealed class UnresolvedReferenceClassifier
{
    private readonly ConnectedSystem _connectedSystem;
    private readonly IConnectorContainment? _containment;
    private readonly ConnectedSystemScope? _scope;

    // Partitions and Containers recast with Subtree scope, for the questions "which partition holds this value?" and
    // "which known Container is nearest above it?". Those are about where a value sits, whatever each Container's own
    // scope says about importing; the scope question is asked separately, of the real Containers.
    private readonly List<ConnectedSystemContainer> _partitionBoundaries = [];
    private readonly Dictionary<int, ConnectedSystemPartition> _partitionsById = [];
    private readonly Dictionary<int, List<ConnectedSystemContainer>> _containerBoundariesByPartitionId = [];
    private readonly Dictionary<int, ConnectedSystemContainer> _containersById = [];

    // Every Container making a statement about scope, exclusions first so that a tie between an exclusion and a
    // selection resolves as ContainerSpecificity.IsInScope resolves it: excluded wins.
    private readonly List<ConnectedSystemContainer> _scopeDecidingContainers = [];

    public UnresolvedReferenceClassifier(ConnectedSystem connectedSystem, IConnectorContainment? containment)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        _connectedSystem = connectedSystem;
        _containment = containment;

        var partitions = connectedSystem.Partitions ?? [];
        if (containment == null || partitions.Count == 0)
            return;

        _scope = ConnectedSystemScope.From(connectedSystem, ConnectedSystemScopeSelectionProposal.FromCurrentSelection(connectedSystem), containment);
        _scopeDecidingContainers.AddRange(_scope.ExcludedContainers);
        _scopeDecidingContainers.AddRange(_scope.SelectedContainers);

        foreach (var partition in partitions)
        {
            _partitionsById[partition.Id] = partition;
            _partitionBoundaries.Add(AsSubtreeBoundary(partition.Id, partition.ExternalId));

            var boundaries = new List<ConnectedSystemContainer>();
            foreach (var container in Flatten(partition.Containers ?? []))
            {
                _containersById[container.Id] = container;
                boundaries.Add(AsSubtreeBoundary(container.Id, container.ExternalId));
            }

            _containerBoundariesByPartitionId[partition.Id] = boundaries;
        }
    }

    /// <summary>
    /// Whether this classifier can place a value in the Connected System's partitions and Containers: the Connector
    /// says what its Containers hold and the Connected System has partitions. Where it cannot, every value that is
    /// not ambiguous is <see cref="UnresolvedReferenceCause.NotFound"/>.
    /// </summary>
    public bool CanPlaceValues => _scope != null;

    /// <summary>
    /// Diagnoses one unresolved reference value.
    /// </summary>
    /// <param name="attributeName">The attribute the value is on.</param>
    /// <param name="value">The reference value as imported.</param>
    /// <param name="ambiguousWith">
    /// The names of the Object Types whose objects the value matches, where it matches more than one and the attribute
    /// declares no target; null or a single name otherwise. Deciding this needs the connector space, so the caller
    /// supplies it.
    /// </param>
    public ConnectedSystemObjectUnresolvedReference Diagnose(string attributeName, string value, IReadOnlyList<string>? ambiguousWith = null)
    {
        ArgumentNullException.ThrowIfNull(attributeName);
        ArgumentNullException.ThrowIfNull(value);

        var diagnosis = new ConnectedSystemObjectUnresolvedReference { AttributeName = attributeName, Value = value };

        if (ambiguousWith is { Count: > 1 })
        {
            diagnosis.CandidateObjectTypeNames = [.. ambiguousWith.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
            return Conclude(diagnosis, UnresolvedReferenceCause.AmbiguousObjectType,
                $"This value matches objects of more than one Object Type ({string.Join(", ", diagnosis.CandidateObjectTypeNames)}), and attribute " +
                $"'{attributeName}' does not declare which one it references.",
                "Declare its target Object Type on the Schema tab if the Connector supports doing so.");
        }

        if (_containment == null || _scope == null)
            return Conclude(diagnosis, UnresolvedReferenceCause.NotFound,
                $"Nothing with this value has been imported into {_connectedSystem.Name}.",
                "Run a Full Import to rule out a missed change. If it is still unresolved, the source refers to an object that is not in it, " +
                "or one whose Object Type is not imported.");

        var partitionBoundary = ContainerSpecificity.ResolveMostSpecific(value, _partitionBoundaries, _containment.IsWithinContainer);
        if (partitionBoundary == null)
            return Conclude(diagnosis, UnresolvedReferenceCause.OutsideConnectedSystem,
                $"This value is in none of {_connectedSystem.Name}'s partitions, so JIM cannot import the object it names.",
                OutsideConnectedSystemRemedy());

        var partition = _partitionsById[partitionBoundary.Id];
        if (!partition.Selected)
        {
            diagnosis.PartitionExternalId = partition.ExternalId;
            return Conclude(diagnosis, UnresolvedReferenceCause.PartitionNotSelected,
                $"Partition {partition.ExternalId} is not selected.",
                "Select it on the Scope tab, then run a Full Import.");
        }

        var nearest = NearestKnownContainer(partition.Id, value);
        if (_scope.Contains(partition.Id, value) is not { } inScope)
            return Conclude(diagnosis, UnresolvedReferenceCause.NotFound,
                $"Nothing with this value has been imported into {_connectedSystem.Name}.",
                "Run a Full Import to rule out a missed change.");

        // Where no known Container holds the value, the partition is the place a finding can point at.
        diagnosis.ContainerExternalId = nearest?.ExternalId;
        if (nearest == null)
            diagnosis.PartitionExternalId = partition.ExternalId;

        if (inScope)
            return Conclude(diagnosis, UnresolvedReferenceCause.NotFoundInScope,
                $"{nearest?.ExternalId ?? partition.ExternalId} is in Container Scope, but nothing with this value has been imported.",
                $"Run a Full Import to rule out a missed change. If it is still unresolved, the object no longer exists in {_connectedSystem.Name}, " +
                "or its Object Type is not imported.");

        var decider = ContainerSpecificity.ResolveMostSpecific(value, _scopeDecidingContainers, _containment.IsWithinContainer);
        if (decider != null && _scope.ExcludedContainers.Contains(decider))
        {
            diagnosis.ContainerExternalId = decider.ExternalId;
            diagnosis.PartitionExternalId = null;
            return Conclude(diagnosis, UnresolvedReferenceCause.ContainerExcluded,
                $"{decider.ExternalId} is excluded from Container Scope.",
                "If its objects belong in JIM, lift the exclusion on the Scope tab, then run a Full Import.");
        }

        return nearest != null
            ? Conclude(diagnosis, UnresolvedReferenceCause.ContainerNotInScope,
                $"{nearest.ExternalId} is not in Container Scope.",
                "Select it on the Scope tab, then run a Full Import.")
            : Conclude(diagnosis, UnresolvedReferenceCause.ContainerNotInScope,
                $"The object sits in {partition.ExternalId}, outside every Container JIM knows of.",
                "Refresh the hierarchy on the Scope tab, then select the Container that holds it and run a Full Import.");
    }

    /// <summary>
    /// Where the fix for a cause lies: JIM's configuration for the scope and schema causes, the Connected System for
    /// the rest.
    /// </summary>
    public static UnresolvedReferenceFixLocation FixLocationFor(UnresolvedReferenceCause cause) => cause switch
    {
        UnresolvedReferenceCause.ContainerNotInScope
            or UnresolvedReferenceCause.ContainerExcluded
            or UnresolvedReferenceCause.PartitionNotSelected
            or UnresolvedReferenceCause.AmbiguousObjectType => UnresolvedReferenceFixLocation.Jim,
        _ => UnresolvedReferenceFixLocation.SourceSystem
    };

    private static ConnectedSystemObjectUnresolvedReference Conclude(
        ConnectedSystemObjectUnresolvedReference diagnosis, UnresolvedReferenceCause cause, string finding, string remedy)
    {
        diagnosis.Cause = cause;
        diagnosis.FixLocation = FixLocationFor(cause);
        diagnosis.Finding = finding;
        diagnosis.Remedy = remedy;
        return diagnosis;
    }

    private ConnectedSystemContainer? NearestKnownContainer(int partitionId, string value)
    {
        if (!_containerBoundariesByPartitionId.TryGetValue(partitionId, out var boundaries) || boundaries.Count == 0)
            return null;

        var nearestBoundary = ContainerSpecificity.ResolveMostSpecific(value, boundaries, _containment!.IsWithinContainer);
        return nearestBoundary == null ? null : _containersById[nearestBoundary.Id];
    }

    private static ConnectedSystemContainer AsSubtreeBoundary(int id, string externalId) =>
        new() { Id = id, ExternalId = externalId, Name = externalId, Scope = ConnectedSystemContainerScope.Subtree };

    private static IEnumerable<ConnectedSystemContainer> Flatten(IEnumerable<ConnectedSystemContainer> containers)
    {
        foreach (var container in containers)
        {
            yield return container;

            foreach (var descendant in Flatten(container.ChildContainers))
                yield return descendant;
        }
    }

    /// <summary>
    /// What to do about a value JIM cannot import, given the handling already chosen: each step suggests the next quieter
    /// setting, so the advice never names the one in force.
    /// </summary>
    private string OutsideConnectedSystemRemedy() => _connectedSystem.UnresolvedReferenceHandling switch
    {
        UnresolvedReferenceHandling.Warn =>
            "If references like this are expected, set Unresolved Reference Handling to Ignore so runs stop reporting them.",
        UnresolvedReferenceHandling.Ignore =>
            "Runs already leave references like this unreported; nothing in JIM needs changing if they are expected.",
        _ => "If references like this are expected, set Unresolved Reference Handling to Warn."
    };
}
