// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// What kind of object a causal timeline (#2100) is about. Read-time only, never persisted, so it carries no
/// ordinal guarantee.
/// </summary>
public enum CausalTimelineSubjectKind
{
    /// <summary>
    /// A Connected System Object, live or deleted.
    /// </summary>
    ConnectedSystemObject,

    /// <summary>
    /// A Metaverse Object, live or deleted.
    /// </summary>
    MetaverseObject
}
