// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// The complete, hardcoded set of JIM feature flags (#1781). Adding a flag means adding a definition here and
/// nothing else discovers flags by reflection or configuration: this is the one place that lists them, so an
/// administrator, a developer and the seeding pass all read the same catalogue.
/// <para>
/// Every flag defaults off, is one <see cref="ServiceSetting"/> row in the <see cref="ServiceSettingCategory.FeatureFlags"/>
/// category, and gets a tracking issue for its own removal filed when it is introduced. See the "Feature Flags"
/// section of <c>engineering/DEVELOPER_GUIDE.md</c> for the full lifecycle.
/// </para>
/// </summary>
public static class FeatureFlagCatalogue
{
    /// <summary>
    /// JIM generates unique values, such as account names and employee numbers, for Attribute Flows (#242).
    /// In development: on only in development and in the integration harness, never surfaced to administrators.
    /// </summary>
    public static readonly FeatureFlagDefinition UniqueValueGeneration = new(
        Key: "Features.UniqueValueGeneration",
        DisplayName: "Unique Value Generation",
        Description: "JIM generates unique values, such as account names and employee numbers, for Attribute Flows.",
        Tier: FeatureFlagTier.InDevelopment,
        TrackingIssueNumber: 242);

    /// <summary>
    /// Every declared flag. The seeding pass converges the database to exactly this set: creating a row for a
    /// flag added here, and removing any <see cref="ServiceSettingCategory.FeatureFlags"/> row whose key is no
    /// longer present, so deleting a flag from this list leaves nothing behind.
    /// </summary>
    public static readonly IReadOnlyList<FeatureFlagDefinition> All =
    [
        UniqueValueGeneration
    ];
}
