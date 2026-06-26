// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// A complete, redaction-aware, point-in-time snapshot of a configuration object's state, captured when the object is
/// created, updated or deleted. Stored as a JSON (jsonb) document on the <see cref="Activity"/> that recorded the
/// change. The structure is a generic tree so that one diff engine and one renderer can serve every configuration type.
/// It never contains secret material: encrypted values are represented by a keyed hash (see
/// <see cref="ConfigurationSnapshotNode.IsSecret"/>).
/// </summary>
public class ConfigurationSnapshot
{
    /// <summary>
    /// The snapshot document schema version, so the diff engine and renderers can evolve without a data migration.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// The configuration object type this snapshot describes, e.g. "SynchronisationRule" or "ConnectedSystem".
    /// </summary>
    public string ObjectType { get; set; } = null!;

    /// <summary>
    /// The database identifier of the configuration object.
    /// </summary>
    public int ObjectId { get; set; }

    /// <summary>
    /// The display name of the configuration object at capture time.
    /// </summary>
    public string? ObjectName { get; set; }

    /// <summary>
    /// The root node of the snapshot tree: an Object node whose children describe the object's configuration.
    /// </summary>
    public ConfigurationSnapshotNode Root { get; set; } = null!;
}
