// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// One piece of an explanation's text (#348). A surface may style it by <see cref="Kind"/>; the words are fixed.
/// </summary>
public sealed class ExplanationSegment
{
    public ExplanationSegmentKind Kind { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The piece as a <see cref="JimMessage"/>, for the portal to show in the viewer's language (#3030); null for a piece
    /// that is data (an attribute's name, a value) or was written as English only. <see cref="Text"/> is its British
    /// English, which the REST API and PowerShell read.
    /// </summary>
    public JimMessage? Message { get; set; }
}
