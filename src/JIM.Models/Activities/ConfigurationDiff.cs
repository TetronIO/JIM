// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// The structured difference between two configuration snapshots of the same object, produced by
/// <c>ConfigurationDiffService</c>. The same tree feeds every surface: the web UI renders it as an audit-style field
/// history (each field's value before and after), PowerShell renders it as a git-style coloured diff, and the REST API
/// returns it as data.
/// </summary>
public class ConfigurationDiff
{
    /// <summary>The configuration object type, e.g. "SynchronisationRule" or "ConnectedSystem".</summary>
    public string ObjectType { get; set; } = null!;

    /// <summary>The integer database identifier of the configuration object; zero for Guid-keyed objects (see <see cref="ObjectGuidId"/>).</summary>
    public int ObjectId { get; set; }

    /// <summary>The Guid database identifier of the configuration object, for Guid-keyed objects (e.g. a Schedule); null otherwise.</summary>
    public Guid? ObjectGuidId { get; set; }

    /// <summary>The display name of the configuration object (from the newer snapshot).</summary>
    public string? ObjectName { get; set; }

    /// <summary>The per-object version of the older snapshot, or null when comparing against nothing (a creation).</summary>
    public int? OldVersion { get; set; }

    /// <summary>The per-object version of the newer snapshot, when known.</summary>
    public int? NewVersion { get; set; }

    /// <summary>The root node of the diff tree.</summary>
    public ConfigurationDiffNode Root { get; set; } = null!;

    /// <summary>Count of added items and scalars.</summary>
    public int AddedCount { get; set; }

    /// <summary>Count of removed items and scalars.</summary>
    public int RemovedCount { get; set; }

    /// <summary>Count of modified scalars.</summary>
    public int ModifiedCount { get; set; }

    /// <summary>True when anything changed between the two snapshots.</summary>
    public bool HasChanges => AddedCount > 0 || RemovedCount > 0 || ModifiedCount > 0;
}
