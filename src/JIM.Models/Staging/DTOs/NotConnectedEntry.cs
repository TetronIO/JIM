// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic.Scoping;

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// An enabled export Synchronisation Rule for a Metaverse Object's type whose Connected System holds no object joined
/// to it, and why (#348). One entry per rule, so two rules on one Connected System are two entries.
/// </summary>
public sealed class NotConnectedEntry
{
    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = string.Empty;

    public ConnectedSystemStatus ConnectedSystemStatus { get; set; }

    public int SyncRuleId { get; set; }

    public string SyncRuleName { get; set; } = string.Empty;

    /// <summary>The Connected System Object Type the rule would provision or join.</summary>
    public string ObjectTypeName { get; set; } = string.Empty;

    public NotConnectedReason Reason { get; set; }

    /// <inheritdoc cref="NotConnectedSummary.Hint"/>
    public string Hint { get; set; } = string.Empty;

    /// <inheritdoc cref="NotConnectedSummary.BulletsTitle"/>
    public string BulletsTitle { get; set; } = string.Empty;

    public List<ExplanationBullet> Bullets { get; set; } = [];

    /// <inheritdoc cref="NotConnectedSummary.Summary"/>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The rule's scoping evaluated against the Metaverse Object now.</summary>
    public ScopingExplanation Scoping { get; set; } = new();
}
