// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// One property an administrator is about to change, described in the terms they need to consent to it: what the
/// property is called, what it is changing from and to, how consequential that is, and (for a destructive change)
/// what it will actually do to their data.
/// </summary>
public class ConfigurationChangePreflightItem
{
    /// <summary>
    /// The configuration snapshot node key, e.g. "outboundDeprovisionAction". This is the same key the classifier
    /// and <c>engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md</c> are written against.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Human-readable name of the property, qualified by its parent sections where it is nested, e.g.
    /// "Object Matching Rules &gt; Rule &gt; Case Sensitive". Taken from the snapshot's own labels, so it cannot drift from
    /// what the change history displays for the same change.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// How consequential this individual property change is. This is what lets the acknowledgement single out the
    /// dangerous property sitting beside harmless ones.
    /// </summary>
    public ConfigurationChangeClass Class { get; init; }

    /// <summary>
    /// Whether the property was modified, or a whole item added to or removed from a collection. Added and Removed
    /// items carry no before-and-after values: what changed is the item's existence, not one of its properties.
    /// </summary>
    public ConfigurationDiffChangeType ChangeType { get; init; }

    /// <summary>
    /// The value before the change, rendered for display. Null when the property is being added, or when the value
    /// is a secret (secrets are reported as changed and never by value).
    /// </summary>
    public string? OldDisplayValue { get; init; }

    /// <summary>
    /// The value after the change, rendered for display. Null when the property is being removed, or when the value
    /// is a secret.
    /// </summary>
    public string? NewDisplayValue { get; init; }

    /// <summary>
    /// What this change will do, in plain terms, for a destructive property; null for everything else. Present only
    /// where the consequence is specific enough to be worth stating, which is why it is curated per property rather
    /// than generated.
    /// </summary>
    public string? Consequence { get; init; }
}
