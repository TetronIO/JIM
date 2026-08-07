// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations.Schema;
namespace JIM.Models.Staging;

public class ConnectedSystemContainer
{
    public int Id { get; set; }

    /// <summary>
    /// If this is a top-level container, then it may reside in a Connected System Partition, though this isn't required if the connector doesn't implement partitions.
    /// </summary>
    public ConnectedSystemPartition? Partition { get; set; }

    /// <summary>
    /// FK to <see cref="Partition"/>. Exposed as an explicit property (rather than a shadow FK) so that
    /// repository queries can filter on it directly and unit tests can seed it without EF Core tracking.
    /// Set only on top-level containers; null on nested descendants (whose partition is reached via
    /// <see cref="ParentContainer"/>).
    /// </summary>
    public int? PartitionId { get; set; }

    /// <summary>
    /// If this is a top-level container and the connector doesn't implement partitions then it'll be a child of a Connected System.
    /// If partitions are implemented, then a Partition reference is required on top-level containers.
    /// </summary>
    public ConnectedSystem? ConnectedSystem { get; set; }

    /// <summary>
    /// The unique identifier for this container in the Connected System.
    /// For LDAP systems, this would be the DN (Distinguished Name).
    /// </summary>
    public string ExternalId { get; set; } = null!;

    /// <summary>
    /// The Connected System's own immutable identifier for this container, where the Connector can supply one:
    /// objectGUID on Active Directory, entryUUID on OpenLDAP. Null for containers enumerated before stable
    /// identifiers were recorded, and for Connectors that have none to give.
    /// </summary>
    /// <remarks>
    /// This is what container identity is keyed on during a hierarchy refresh, because <see cref="ExternalId"/> is
    /// the Distinguished Name and therefore changes on every rename and move. Matching on the Distinguished Name
    /// alone read those as a removal plus an addition, and the re-added container arrived unselected, quietly taking
    /// its objects out of import scope and obsoleting them on the next Full Import. Populated on the next hierarchy
    /// refresh for containers that predate it; the merge falls back to the Distinguished Name until then.
    /// </remarks>
    public string? StableId { get; set; }

    /// <summary>
    /// The human-readable name for the container.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// An optional description for the container
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Some systems enable containers to be hidden by default, to reduce the risk of exposing internal objects to end-users.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Indicates whether the container has been selected to be managed, i.e. whether or not objects are
    /// imported from here or not.
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>
    /// How far beneath this Container objects are imported from, when it is selected. Subtree (the default)
    /// imports from this Container and every Container beneath it; OneLevel imports only objects held directly
    /// in this Container, leaving descendants to be selected in their own right.
    /// Ignored when <see cref="Selected"/> is false.
    /// </summary>
    public ConnectedSystemContainerScope Scope { get; set; } = ConnectedSystemContainerScope.Subtree;

    /// <summary>
    /// Containers can container children containers.
    /// Enables a hierarchy of containers to be built out, i.e a directory DIT.
    /// </summary>
    public HashSet<ConnectedSystemContainer> ChildContainers { get; } = new();

    #region For MudBlazor TreeView
    public ConnectedSystemContainer? ParentContainer { get; set; }

    /// <summary>
    /// FK to <see cref="ParentContainer"/>. Exposed as an explicit property (rather than a shadow FK) so
    /// that repository queries can filter on it directly and unit tests can seed it without EF Core tracking.
    /// Null on top-level containers.
    /// </summary>
    public int? ParentContainerId { get; set; }

    [NotMapped]
    public bool Expanded { get; set; }

    [NotMapped]
    public bool Included { get; set; }
    #endregion

    public void AddChildContainer(ConnectedSystemContainer container)
    {
        container.ParentContainer = this;
        ChildContainers.Add(container);
    }

    public bool AreAnyChildContainersSelected()
    {
        if (ChildContainers.Count == 0)
            return false;

        if (ChildContainers.Any(c => c.Selected))
            return true;

        // look further down the tree
        return ChildContainers.Any(DetermineIfAnyChildrenAreSelected);
    }

    private static bool DetermineIfAnyChildrenAreSelected(ConnectedSystemContainer connectedSystemContainer)
    {
        if (connectedSystemContainer.ChildContainers.Any(c => c.Selected))
            return true;

        // look further down the tree
        foreach (var childContainer in connectedSystemContainer.ChildContainers)
        {
            if (childContainer.Selected)
                return true;

            if (DetermineIfAnyChildrenAreSelected(childContainer))
                return true;
        }

        return false;
    }
}