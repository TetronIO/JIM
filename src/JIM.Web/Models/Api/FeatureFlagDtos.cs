// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a feature flag: its catalogue definition and its current state.
/// </summary>
public class FeatureFlagDto
{
    /// <summary>
    /// The flag's key, e.g. "Features.UniqueValueGeneration".
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// What the flag controls.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The flag's tier: "Preview" (visible to administrators) or "InDevelopment" (developers only).
    /// </summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>
    /// The GitHub issue tracking removal of this flag once the feature it gates ships.
    /// </summary>
    public int TrackingIssueNumber { get; set; }

    /// <summary>
    /// Whether the flag is currently enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When the flag was last changed, or null if never changed from its seeded default.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The display name of the principal that last changed the flag, or null if never changed.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    public static FeatureFlagDto FromState(FeatureFlagState state)
    {
        return new FeatureFlagDto
        {
            Key = state.Definition.Key,
            DisplayName = state.Definition.DisplayName,
            Description = state.Definition.Description,
            Tier = state.Definition.Tier.ToString(),
            TrackingIssueNumber = state.Definition.TrackingIssueNumber,
            Enabled = state.Enabled,
            LastUpdated = state.LastUpdated,
            LastUpdatedByName = state.LastUpdatedByName
        };
    }
}

/// <summary>
/// Request DTO for switching a feature flag on or off.
/// </summary>
public class FeatureFlagUpdateRequestDto
{
    /// <summary>
    /// Whether the flag should be enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Required to enable an In Development flag; ignored when disabling, or when the flag is Preview-tier.
    /// </summary>
    public bool AllowInDevelopment { get; set; }
}
