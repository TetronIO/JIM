// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// A feature flag's catalogue definition paired with its current persisted state (#1781). Returned by
/// <c>FeatureFlagServer.GetFeatureFlagsAsync</c> and <c>FeatureFlagServer.SetFeatureFlagAsync</c> for the portal
/// card and the REST API to render.
/// </summary>
public class FeatureFlagState
{
    /// <summary>
    /// The flag's catalogue entry: key, display name, description, tier and tracking issue.
    /// </summary>
    public required FeatureFlagDefinition Definition { get; init; }

    /// <summary>
    /// Whether the flag is currently switched on.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// When the flag was last changed, or null if it has never been changed from its seeded default.
    /// </summary>
    public DateTime? LastUpdated { get; init; }

    /// <summary>
    /// The display name of the principal that last changed the flag, or null if it has never been changed.
    /// </summary>
    public string? LastUpdatedByName { get; init; }
}
