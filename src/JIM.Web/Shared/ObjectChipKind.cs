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
    RunProfile,

    /// <summary>
    /// An API key, as who did something, shown with a three-letter <c>KEY</c> glyph.
    /// </summary>
    ApiKey,

    /// <summary>
    /// A Schedule, as what started something, shown with a three-letter <c>SCH</c> glyph.
    /// </summary>
    Schedule,

    /// <summary>
    /// An Activity: a recorded operation, such as a parent Activity, shown with a three-letter <c>ACT</c> glyph, its
    /// type as the prefix and its target as the name.
    /// </summary>
    Activity,

    /// <summary>
    /// JIM itself, as who did something (seeding, housekeeping, a sign-in), shown with a three-letter <c>JIM</c> glyph.
    /// Never a link: there is nothing to open.
    /// </summary>
    System
}
