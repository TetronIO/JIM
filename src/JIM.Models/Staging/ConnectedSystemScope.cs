// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Preview;

namespace JIM.Models.Staging;

/// <summary>
/// One selection of partitions and containers, resolved into the question that actually matters: is a given object
/// inside the scope JIM manages?
/// </summary>
/// <remarks>
/// Import builds its search scope from the selected partitions and the top-level selected containers; export refuses
/// to write outside the selected containers (#1250); and a preview counts what a proposed selection would take out
/// of scope (#1251). All three are the same question, and answering it in three places is how a preview comes to
/// state a count that the next import contradicts. This type is that answer, computed once from a selection and
/// asked per object.
///
/// It deliberately holds no opinion on what containment means. That belongs to the Connector, which is asked through
/// <see cref="IConnectorContainment"/>; a Connector that cannot answer leaves membership undetermined rather than
/// letting the framework invent a rule. Each container's own
/// <see cref="ConnectedSystemContainer.Scope"/> travels with it into that question, so a
/// <see cref="ConnectedSystemContainerScope.OneLevel"/> container is never mistaken for a licence over its whole
/// subtree.
/// </remarks>
public sealed class ConnectedSystemScope
{
    private readonly IConnectorContainment? _containment;

    /// <summary>The partitions in this selection.</summary>
    public IReadOnlySet<int> SelectedPartitionIds { get; }

    /// <summary>
    /// The containers in this selection, from partitions in this selection only. Each carries its own
    /// <see cref="ConnectedSystemContainer.Scope"/>, which decides how far beneath it objects are in scope, so a
    /// descendant of one of these may or may not be covered by it.
    /// </summary>
    public IReadOnlyList<ConnectedSystemContainer> SelectedContainers { get; }

    /// <summary>
    /// Whether container membership is part of scope for this Connected System. False for a Connector with
    /// partitions but no containers, where a selected partition is the whole answer.
    /// </summary>
    public bool ContainersDecideScope { get; }

    private ConnectedSystemScope(
        IReadOnlySet<int> selectedPartitionIds,
        IReadOnlyList<ConnectedSystemContainer> selectedContainers,
        bool containersDecideScope,
        IConnectorContainment? containment)
    {
        SelectedPartitionIds = selectedPartitionIds;
        SelectedContainers = selectedContainers;
        ContainersDecideScope = containersDecideScope;
        _containment = containment;
    }

    /// <summary>
    /// Resolves <paramref name="selection"/> against <paramref name="connectedSystem"/>'s hierarchy.
    /// </summary>
    /// <param name="connectedSystem">
    /// The Connected System, whose partition and container hierarchy supplies the external ids. Its own
    /// <c>Selected</c> flags are not read: the selection is the parameter, so the same method serves the current
    /// selection and a proposed one.
    /// </param>
    /// <param name="selection">The partitions and containers to treat as selected.</param>
    /// <param name="containment">
    /// The Connector's containment rule, or null when it cannot express one. Null leaves every container-dependent
    /// membership question undetermined rather than guessed at.
    /// </param>
    public static ConnectedSystemScope From(
        ConnectedSystem connectedSystem,
        ConnectedSystemScopeSelectionProposal selection,
        IConnectorContainment? containment)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ArgumentNullException.ThrowIfNull(selection);

        var partitionIds = selection.SelectedPartitionIds.ToHashSet();
        var containerIds = selection.SelectedContainerIds.ToHashSet();
        var containers = new List<ConnectedSystemContainer>();

        foreach (var partition in (connectedSystem.Partitions ?? []).Where(p => partitionIds.Contains(p.Id) && p.Containers != null))
            CollectSelectedContainers(partition.Containers!, containerIds, containers);

        return new ConnectedSystemScope(
            partitionIds,
            containers,
            connectedSystem.ConnectorDefinition?.SupportsPartitionContainers ?? false,
            containment);
    }

    /// <summary>
    /// Whether an object is inside this scope.
    /// </summary>
    /// <param name="partitionId">The partition the object was imported from.</param>
    /// <param name="containerIdentifier">
    /// The object's identifier in the Connected System's own terms, from which the Connector derives containment.
    /// </param>
    /// <returns>
    /// <c>true</c> or <c>false</c> where scope is determined, and <c>null</c> where it cannot be: the object
    /// records no partition (rows predating partition tracking), it carries no identifier to test containment
    /// against, or the Connector cannot express containment. Undetermined is reported as undetermined; a preview
    /// that resolved it to "out of scope" would count objects as leaving that may not be, and one that resolved it
    /// to "in scope" would quietly omit objects that are.
    /// </returns>
    public bool? Contains(int? partitionId, string? containerIdentifier)
    {
        if (partitionId is null)
            return null;

        if (!SelectedPartitionIds.Contains(partitionId.Value))
            return false;

        if (!ContainersDecideScope)
            return true;

        // A selected partition with no selected containers imports nothing, which is a determined answer and the
        // one an administrator who has just cleared every container needs to see counted.
        if (SelectedContainers.Count == 0)
            return false;

        if (_containment is null || string.IsNullOrEmpty(containerIdentifier))
            return null;

        // The most specific selected container decides, rather than any of them being enough
        // (<see cref="ContainerSpecificity"/>). While every container says the same thing the two are the same
        // answer; they part company once a container can contradict the branch it sits in (#1255).
        return ContainerSpecificity.ResolveMostSpecific(containerIdentifier, SelectedContainers, _containment.IsWithinContainer) is not null;
    }

    private static void CollectSelectedContainers(
        IEnumerable<ConnectedSystemContainer> containers,
        IReadOnlySet<int> selectedContainerIds,
        List<ConnectedSystemContainer> collected)
    {
        foreach (var container in containers)
        {
            if (selectedContainerIds.Contains(container.Id))
                collected.Add(container);

            CollectSelectedContainers(container.ChildContainers, selectedContainerIds, collected);
        }
    }
}
