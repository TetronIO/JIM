// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

public class ConnectorContainer
{
    /// <summary>
    /// The unique identifier for the container.
    /// For LDAP systems, this would be the Distinguished Name (DN).
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// The human-readable name for the container.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The Connected System's own immutable identifier for this container, where it has one: objectGUID on Active
    /// Directory, entryUUID on OpenLDAP. Null when the Connector cannot supply one.
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> is the container's address, not its identity: for a directory it is the Distinguished Name,
    /// which every rename and every move rewrites. A hierarchy refresh that matched on it alone therefore read a
    /// renamed container as one removed and another added, dropping the administrator's selection and silently
    /// narrowing import scope. Supply this wherever the system offers a stable identifier; the merge prefers it and
    /// falls back to <see cref="Id"/> when it is absent.
    /// </remarks>
    public string? StableId { get; set; }

    /// <summary>
    /// An optional description for the container
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Some systems enable containers to be hidden by default, to reduce the risk of exposing internal objects to end-users.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// If this is a top-level container, then it may reside in a connector partition, though this isn't required if the connector doesn't implement partitions.
    /// </summary>
    public ConnectorPartition? ConnectorPartition { get; set; }

    /// <summary>
    /// Containers can container children containers.
    /// Enables a hierarchy of containers to be built out, i.e a directory DIT.
    /// </summary>
    public List<ConnectorContainer> ChildContainers { get; set; }

    public ConnectorContainer(string id, string name, bool hidden = false)
    {
        Id = id;
        Name = name;
        Hidden = hidden;
        ChildContainers = new List<ConnectorContainer>();
    }

    public ConnectorContainer(string id, string name, string description, bool hidden = false)
    {
        Id = id;
        Name = name;
        Description = description;
        Hidden = hidden;
        ChildContainers = new List<ConnectorContainer>();
    }
}