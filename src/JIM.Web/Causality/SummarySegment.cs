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
    /// A plain text segment, optionally carrying the wording to use when the technical-names toggle is on.
    /// </summary>
    /// <remarks>
    /// Both wordings are built together rather than the sentence being rebuilt on toggle, because the summary
    /// is composed once per (Item, Context) pair while the toggle flips at any time; rebuilding on toggle
    /// would either discard the panel's expanded and selected event or need a second, parallel build path.
    /// A segment with no technical alternative renders the same either way, which is right for the many parts
    /// of the sentence that carry no JIM vocabulary at all ("on", "was deleted", counts and punctuation).
    /// </remarks>
    /// <param name="Value">The plain-language text to render verbatim (encoded by Blazor at render time).</param>
    /// <param name="Technical">
    /// The same text in JIM's own vocabulary ("the record" becomes "the Connected System Object"), or null
    /// where the wording is already technical or has no technical counterpart.
    /// </param>
    public sealed record Text(string Value, string? Technical = null) : SummarySegment;

    /// <summary>
    /// An entity mention, rendered as a highlighted token chip. Href is null when the entity cannot
    /// be navigated to (e.g. a Run Profile name, or a rule known only by its snapshot name).
    /// </summary>
    /// <param name="Label">Display label for the entity.</param>
    /// <param name="Href">Destination href, or null for an unlinked mention.</param>
    /// <param name="Kind">The kind of entity, for glyph selection.</param>
    public sealed record Entity(string Label, string? Href, CausalityEntityKind Kind) : SummarySegment;
}
