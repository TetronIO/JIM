// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// One segment of a causality sentence: either plain text or an entity mention. Sentences are never
/// pre-rendered HTML; the renderer emits each segment through Blazor so values sourced from
/// connected systems are always encoded at render time.
/// </summary>
public abstract record SummarySegment
{
    private SummarySegment()
    {
    }

    /// <summary>
    /// A plain text segment.
    /// </summary>
    /// <param name="Value">The text to render verbatim (encoded by Blazor at render time).</param>
    public sealed record Text(string Value) : SummarySegment;

    /// <summary>
    /// An entity mention, rendered as a highlighted token chip. Href is null when the entity cannot
    /// be navigated to (e.g. a Run Profile name, or a rule known only by its snapshot name).
    /// </summary>
    /// <param name="Label">Display label for the entity.</param>
    /// <param name="Href">Destination href, or null for an unlinked mention.</param>
    /// <param name="Kind">The kind of entity, for glyph selection.</param>
    public sealed record Entity(string Label, string? Href, CausalityEntityKind Kind) : SummarySegment;
}
