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
    /// How the property changed. Note that a scalar property can be Added or Removed in its own right (an entry
    /// joining or leaving a list of identifiers); use <see cref="IsCollectionItem"/> to tell that apart from a whole
    /// item joining or leaving a collection.
    /// </summary>
    public ConfigurationDiffChangeType ChangeType { get; init; }

    /// <summary>
    /// True when this is a whole item added to or removed from a collection (a container, a Run Profile, an Object
    /// Type) rather than a property of one. Such an item carries no before-and-after values, because what changed is
    /// its existence: <see cref="Label"/> names it and <see cref="ChangeType"/> says which way it went.
    /// </summary>
    public bool IsCollectionItem { get; init; }

    /// <summary>
    /// What a collection item's arrival or departure actually did, where "Added" and "Removed" would misdescribe it;
    /// null everywhere else, leaving the plain reading to stand.
    /// </summary>
    /// <remarks>
    /// A Connected System Container carved out of a selection is the case this exists for. Only the Containers an
    /// administrator has said something about are captured, so excluding one puts it into the snapshot for the first
    /// time and it arrives as an addition, identically to selecting one. Described from the arrival alone, the
    /// confirmation reads "Added" over prose about objects coming into scope, at the moment they are leaving it.
    /// </remarks>
    public string? CollectionItemVerb { get; init; }

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
