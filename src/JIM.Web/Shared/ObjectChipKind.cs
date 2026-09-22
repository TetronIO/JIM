// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// The full set of objects an <see cref="ObjectChip"/> can name: the site's one object chip
/// replaces both the portal's earlier CS/MV-only <c>ObjectChip</c> and the causality panel's own
/// <c>CausalityEntityChip</c>, so this enum carries every kind either one named, honestly rather than
/// abbreviated (a Connected System Object is not "ConnectedSystem": that value now names the Connected
/// System itself).
/// </summary>
public enum ObjectChipKind
{
    /// <summary>
    /// A Connected System Object: a record as it exists in a Connected System, shown with a three-letter
    /// <c>CSO</c> glyph.
    /// </summary>
    ConnectedSystemObject,

    /// <summary>
    /// A Metaverse Object: the object JIM holds, shown with a three-letter <c>MVO</c> glyph.
    /// </summary>
    MetaverseObject,

    /// <summary>
    /// A Connected System itself (not one of its objects), shown with a two-letter <c>CS</c> glyph.
    /// </summary>
    ConnectedSystem,

    /// <summary>
    /// A Synchronisation Rule, shown with a two-letter <c>SR</c> glyph.
    /// </summary>
    SynchronisationRule,

    /// <summary>
    /// A Pending Export, shown with a two-letter <c>PE</c> glyph.
    /// </summary>
    PendingExport,

    /// <summary>
    /// A Deletion Record, shown with a two-letter <c>DR</c> glyph.
    /// </summary>
    DeletionRecord,

    /// <summary>
    /// A Run Profile, shown with a two-letter <c>RP</c> glyph.
    /// </summary>
    RunProfile
}
