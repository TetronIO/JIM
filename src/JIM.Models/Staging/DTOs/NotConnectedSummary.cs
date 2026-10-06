// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic.Scoping;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// The words for one not-connected entry (#348), generated once on the server so the portal, the REST API and
/// PowerShell say exactly the same thing.
/// </summary>
public sealed class NotConnectedSummary
{
    /// <summary>
    /// A one-line qualifier for the reason, naming failing attributes without their values, for example "Fails on
    /// Department; Cost Centre or Job Title" or "In scope; nothing staged yet".
    /// </summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>What the bullets answer, for example "To come into scope" or "What happens next".</summary>
    public string BulletsTitle { get; set; } = string.Empty;

    public List<ExplanationBullet> Bullets { get; set; } = [];

    /// <summary>The whole entry as plain text to paste into a message or ticket, with the evaluation time in UTC.</summary>
    public string Summary { get; set; } = string.Empty;
}
