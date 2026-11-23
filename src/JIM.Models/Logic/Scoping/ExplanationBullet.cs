// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// One line of a list explaining what an object needs, or what happens next (#348), for example "Department must
/// equal Finance (currently Engineering)".
/// </summary>
public sealed class ExplanationBullet
{
    /// <summary>
    /// The line in pieces, for surfaces that style values instead of quoting them; joined, they read as the line does
    /// without quotes.
    /// </summary>
    public List<ExplanationSegment> Segments { get; set; } = [];

    /// <summary>
    /// The line as plain text, with text values in double quotes so their boundaries survive being pasted into a
    /// message. This is the form the copyable summary and PowerShell use.
    /// </summary>
    public string PlainText { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="PlainText"/> as a <see cref="JimMessage"/> (#3030), for the copyable summary in the viewer's language;
    /// null for a line written as English only.
    /// </summary>
    public JimMessage? PlainTextDetail { get; set; }
}
