// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// One piece of what overtook a preview (#2022): plain text, or a thing it names.
/// </summary>
/// <param name="Kind">Whether this is text, and if not, what kind of thing it names.</param>
/// <param name="Text">The text, or the thing's name.</param>
/// <param name="TypeName">For a named thing, what it is, as JIM names it ("Run Profile"); null for text.</param>
/// <param name="EntityId">For a Connected System or Synchronisation Rule, its id, where it still has one.</param>
/// <param name="ActivityId">For a Run Profile, the Activity that ran or changed it.</param>
public sealed record PreviewOvertakingPart(
    PreviewOvertakingPartKind Kind,
    string Text,
    string? TypeName = null,
    int? EntityId = null,
    Guid? ActivityId = null)
{
    /// <summary>The piece as plain text: a named thing reads as its kind and quoted name.</summary>
    public string ToText() => Kind == PreviewOvertakingPartKind.Text ? Text : $"{TypeName} '{Text}'";
}
