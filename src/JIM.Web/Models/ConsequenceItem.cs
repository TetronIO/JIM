// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// A single line in a consequence or blocker list shown by <c>ConsequenceConfirmationDialog</c>:
/// one thing that will be removed, changed, or that stands in the way of the change.
/// </summary>
public sealed class ConsequenceItem
{
    /// <summary>
    /// The text describing this consequence, already formatted for display.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Optional MudBlazor icon shown beside the text.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Optional link target, so an administrator can navigate to the objects concerned. When set,
    /// the item renders as a link.
    /// </summary>
    public string? Href { get; init; }
}
