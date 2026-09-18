// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// A link (or unlinked mention) from a causality event to an entity's detail page. Href is null when
/// the entity can no longer be navigated to (e.g. a Synchronisation Rule known only by its snapshot
/// name, or a deleted Identity named on an event that links to the deletion record instead).
/// </summary>
/// <param name="Label">Display label for the entity.</param>
/// <param name="Href">Destination href, or null for an unlinked mention.</param>
/// <param name="Kind">The kind of entity, for glyph selection.</param>
/// <param name="ObjectTypeName">
/// The entity's own object type (e.g. "user", "person"), where the channel that produced this link
/// carried one. Null when the type is unknown, in which case every consumer renders the name alone
/// rather than guessing a type. Only ever set for <see cref="CausalityEntityKind.Record"/> links: a
/// Connected System, a Synchronisation Rule and the rest are not typed the way a Connected System
/// Object or a Metaverse Object is.
/// </param>
public sealed record CausalityEntityLink(string Label, string? Href, CausalityEntityKind Kind, string? ObjectTypeName = null);
