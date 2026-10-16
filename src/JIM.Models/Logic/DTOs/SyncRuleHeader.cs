// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// A Synchronisation Rule in summary: what it connects, which way, and how it is set.
/// </summary>
public class SyncRuleHeader
{
    /// <summary>
    /// The ID of the Synchronisation Rule.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the Synchronisation Rule.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// What the Synchronisation Rule is for, for administrators; null when none is set.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// When the Synchronisation Rule was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The ID of the Connected System the rule synchronises with.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The name of that Connected System.
    /// </summary>
    public string ConnectedSystemName { get; set; } = null!;

    /// <summary>
    /// The ID of the Connected System Object Type the rule applies to.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// The name of that Connected System Object Type.
    /// </summary>
    public string ConnectedSystemObjectTypeName { get; set; } = null!;

    /// <summary>
    /// The ID of the Metaverse Object Type the rule applies to.
    /// </summary>
    public int MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The name of that Metaverse Object Type.
    /// </summary>
    public string MetaverseObjectTypeName { get; set; } = null!;

    /// <summary>
    /// Import for a rule that flows values from the Connected System into the Metaverse, Export for one that flows
    /// them out to it.
    /// </summary>
    public SyncRuleDirection Direction { get; set; }

    /// <summary>
    /// Whether an export rule creates a Connected System Object for each Metaverse Object in its scope that has
    /// none; null when it has not been set.
    /// </summary>
    public bool? ProvisionToConnectedSystem { get; set; }

    /// <summary>
    /// Whether an import rule creates a Metaverse Object for each Connected System Object in its scope that matches
    /// none; null when it has not been set.
    /// </summary>
    public bool? ProjectToMetaverse { get; set; }

    /// <summary>
    /// Whether the rule takes part in synchronisation; a disabled rule is skipped.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// For an export rule, whether a change imported from the Connected System re-evaluates the rule, so the value
    /// JIM exported is put back when the target drifts from it. Only meaningful on an export rule.
    /// </summary>
    public bool EnforceState { get; set; }

    /// <summary>
    /// For an export rule, what happens to a Connected System Object when its Metaverse Object leaves the rule's
    /// scope or is deleted: Disconnect breaks the join and leaves the object in the Connected System, and Delete
    /// removes it from there. Only meaningful on an export rule.
    /// </summary>
    public OutboundDeprovisionAction OutboundDeprovisionAction { get; set; }

    /// <summary>
    /// For an import rule, what happens when a Connected System Object leaves the rule's scope: Disconnect breaks
    /// the join, and RemainJoined keeps the join and stops the values flowing. Only meaningful on an import rule.
    /// </summary>
    public InboundOutOfScopeAction InboundOutOfScopeAction { get; set; }

    /// <summary>
    /// Creates a header from a SyncRule entity.
    /// </summary>
    public static SyncRuleHeader FromEntity(SyncRule entity)
    {
        return new SyncRuleHeader
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Created = entity.Created,
            ConnectedSystemId = entity.ConnectedSystemId,
            ConnectedSystemName = entity.ConnectedSystem?.Name ?? string.Empty,
            ConnectedSystemObjectTypeId = entity.ConnectedSystemObjectTypeId,
            ConnectedSystemObjectTypeName = entity.ConnectedSystemObjectType?.Name ?? string.Empty,
            MetaverseObjectTypeId = entity.MetaverseObjectTypeId,
            MetaverseObjectTypeName = entity.MetaverseObjectType?.Name ?? string.Empty,
            Direction = entity.Direction,
            ProvisionToConnectedSystem = entity.ProvisionToConnectedSystem,
            ProjectToMetaverse = entity.ProjectToMetaverse,
            Enabled = entity.Enabled,
            EnforceState = entity.EnforceState,
            OutboundDeprovisionAction = entity.OutboundDeprovisionAction,
            InboundOutOfScopeAction = entity.InboundOutOfScopeAction
        };
    }
}