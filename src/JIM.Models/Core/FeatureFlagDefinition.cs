// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// Describes one feature flag (#1781): its Service Setting key, how it is presented to an administrator, its
/// tier, and the issue that will remove it once the feature it gates has shipped. Catalogued once in
/// <see cref="FeatureFlagCatalogue"/>; never constructed elsewhere.
/// </summary>
/// <param name="Key">
/// The underlying <see cref="ServiceSetting"/> key. By convention, "Features.&lt;Name&gt;".
/// </param>
/// <param name="DisplayName">The name shown to administrators wherever the flag surfaces.</param>
/// <param name="Description">What the flag controls, shown beside its switch.</param>
/// <param name="Tier">Whether the flag is shown to administrators (<see cref="FeatureFlagTier.Preview"/>) or
/// developers only (<see cref="FeatureFlagTier.InDevelopment"/>).</param>
/// <param name="TrackingIssueNumber">
/// The GitHub issue tracking removal of this flag once the feature it gates ships and the flag is deleted.
/// </param>
public record FeatureFlagDefinition(
    string Key,
    string DisplayName,
    string Description,
    FeatureFlagTier Tier,
    int TrackingIssueNumber);
