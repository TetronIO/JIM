// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics.CodeAnalysis;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;

namespace JIM.Web.Causality;

/// <summary>
/// One thing the Causality Explorer (#2100) can focus on: an execution item or an object, by id.
/// </summary>
public sealed record CausalityFocus(CausalityFocusKind Kind, Guid Id)
{
    /// <summary>
    /// The explorer's own address for the focus, without a trail.
    /// </summary>
    public string Path => Kind switch
    {
        CausalityFocusKind.ConnectedSystemObject => $"/causality/connected-system-objects/{Id}",
        CausalityFocusKind.MetaverseObject => $"/causality/metaverse-objects/{Id}",
        _ => $"/causality/items/{Id}"
    };

    /// <summary>
    /// How the focus is written in a trail: a one-letter kind, a colon and the id.
    /// </summary>
    public string Token => Kind switch
    {
        CausalityFocusKind.ConnectedSystemObject => $"c:{Id}",
        CausalityFocusKind.MetaverseObject => $"m:{Id}",
        _ => $"i:{Id}"
    };

    /// <summary>
    /// The object the focus names, for the reads that take one; null for an item.
    /// </summary>
    public CausalTimelineSubject? Subject => Kind switch
    {
        CausalityFocusKind.ConnectedSystemObject => new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, Id),
        CausalityFocusKind.MetaverseObject => new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, Id),
        _ => null
    };

    /// <summary>
    /// Reads a trail token, or returns false for anything that is not one.
    /// </summary>
    public static bool TryParse(string? token, [NotNullWhen(true)] out CausalityFocus? focus)
    {
        focus = null;
        var value = token?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length < 3 || value[1] != ':' || !Guid.TryParse(value[2..], out var id))
            return false;

        CausalityFocusKind? kind = value[0] switch
        {
            'i' => CausalityFocusKind.Item,
            'c' => CausalityFocusKind.ConnectedSystemObject,
            'm' => CausalityFocusKind.MetaverseObject,
            _ => null
        };
        if (kind is not { } focusKind)
            return false;

        focus = new CausalityFocus(focusKind, id);
        return true;
    }
}
