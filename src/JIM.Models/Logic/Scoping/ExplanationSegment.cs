// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// One piece of an explanation's text (#348). A surface may style it by <see cref="Kind"/>; the words are fixed.
/// </summary>
public sealed class ExplanationSegment
{
    public ExplanationSegmentKind Kind { get; set; }

    public string Text { get; set; } = string.Empty;
}
