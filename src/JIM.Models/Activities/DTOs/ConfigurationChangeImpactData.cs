// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// The minimal projection of a configuration change Activity needed to decide which Connected Systems it affects.
/// Carries the change's classification and its target columns; the drift calculation matches those columns against
/// each system's configuration scope rather than loading whole Activities.
/// </summary>
public class ConfigurationChangeImpactData
{
    /// <summary>
    /// When the change was recorded.
    /// </summary>
    public DateTime When { get; init; }

    /// <summary>
    /// How consequential the change was.
    /// </summary>
    public ConfigurationChangeClass Class { get; init; }

    /// <summary>
    /// Set when the change targets a Connected System directly (including its Run Profiles, object types, partitions
    /// and Simple Mode Object Matching Rules), and on a Synchronisation Rule deletion, where it is the only surviving
    /// link back to the owning system.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    /// <summary>
    /// Set when the change targets a Synchronisation Rule that still exists.
    /// </summary>
    public int? SyncRuleId { get; init; }

    /// <summary>
    /// Set when the change targets a Metaverse Object Type.
    /// </summary>
    public int? MetaverseObjectTypeId { get; init; }

    /// <summary>
    /// Set when the change targets a Metaverse Attribute.
    /// </summary>
    public int? MetaverseAttributeId { get; init; }

    /// <summary>
    /// Set when the change targets a Service Setting, identified by its key.
    /// </summary>
    public string? ServiceSettingKey { get; init; }
}
