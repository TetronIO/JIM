// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.TestSupport;

/// <summary>
/// Substitutes the feature-flag catalogue (#1781) for the calling test, so the mechanism (seeding, the flag server,
/// the REST and portal surfaces) can be exercised while <see cref="FeatureFlagCatalogue"/> declares no real flag,
/// which is its resting state once every flagged feature has shipped. Scoped to the test's asynchronous flow and
/// undone on dispose:
/// <code>
/// using var catalogue = FeatureFlagCatalogueScope.Use(FeatureFlagCatalogueScope.InDevelopmentFlag);
/// </code>
/// Creating it in <c>[SetUp]</c> and disposing it in <c>[TearDown]</c> works too: NUnit runs the test on the flow its
/// set-up left behind.
/// </summary>
public static class FeatureFlagCatalogueScope
{
    /// <summary>
    /// A synthetic In Development flag, the tier a new feature is introduced at.
    /// </summary>
    public static readonly FeatureFlagDefinition InDevelopmentFlag = new(
        Key: "Features.TestInDevelopment",
        DisplayName: "Test In Development Feature",
        Description: "A synthetic In Development flag that exists only in tests.",
        Tier: FeatureFlagTier.InDevelopment,
        TrackingIssueNumber: 1781);

    /// <summary>
    /// A synthetic Preview flag, for the rules that differ by tier.
    /// </summary>
    public static readonly FeatureFlagDefinition PreviewFlag = new(
        Key: "Features.TestPreview",
        DisplayName: "Test Preview Feature",
        Description: "A synthetic Preview flag that exists only in tests.",
        Tier: FeatureFlagTier.Preview,
        TrackingIssueNumber: 1781);

    /// <summary>
    /// Makes <see cref="FeatureFlagCatalogue.All"/> return exactly <paramref name="definitions"/> until the returned
    /// scope is disposed.
    /// </summary>
    public static IDisposable Use(params FeatureFlagDefinition[] definitions) =>
        FeatureFlagCatalogue.SubstituteForTesting(definitions);
}
