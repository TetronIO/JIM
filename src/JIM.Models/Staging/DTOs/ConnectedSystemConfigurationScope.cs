// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// The set of configuration objects whose change affects one Connected System's synchronisation outcomes. Used to
/// decide, precisely, whether a given configuration change is relevant to this system: a Metaverse Attribute edit
/// matters only to systems whose Synchronisation Rules actually reference that attribute, so unrelated systems are
/// not made to display a re-run prompt they do not need.
/// </summary>
public class ConnectedSystemConfigurationScope
{
    /// <summary>
    /// The Connected System this scope belongs to.
    /// </summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>
    /// The ids of this system's Synchronisation Rules. Deleted rules are necessarily absent, which is why a rule
    /// deletion attributes to its system through the Activity's Connected System id instead.
    /// </summary>
    public HashSet<int> SyncRuleIds { get; init; } = [];

    /// <summary>
    /// The ids of the Metaverse Object Types this system's Synchronisation Rules target.
    /// </summary>
    public HashSet<int> MetaverseObjectTypeIds { get; init; } = [];

    /// <summary>
    /// The ids of the Metaverse Attributes this system's Synchronisation Rules reference, whether as an Attribute
    /// Flow target, an Attribute Flow source, a scoping criterion, or an Object Matching Rule target.
    /// </summary>
    public HashSet<int> MetaverseAttributeIds { get; init; } = [];
}
