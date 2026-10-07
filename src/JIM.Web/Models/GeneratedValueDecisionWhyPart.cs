// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One piece of the "why it is held" sentence: plain text, or a Connected System rendered as its chip.
/// </summary>
/// <param name="Text">The text, or the Connected System's name when <see cref="IsConnectedSystem"/>.</param>
/// <param name="ConnectedSystemId">The Connected System, when this piece names one.</param>
public sealed record GeneratedValueDecisionWhyPart(string Text, int? ConnectedSystemId = null)
{
    /// <summary>Whether this piece names a Connected System (and so renders as its chip).</summary>
    public bool IsConnectedSystem => ConnectedSystemId.HasValue;
}
