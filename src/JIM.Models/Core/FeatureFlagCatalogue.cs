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
/// <para>
/// The catalogue is empty whenever no feature is behind a flag, which is its normal resting state. The mechanism's own
/// tests exercise it against synthetic definitions substituted through <see cref="SubstituteForTesting"/>, so they do
/// not depend on which real flags exist.
/// </para>
/// </summary>
public static class FeatureFlagCatalogue
{
    /// <summary>
    /// The SCIM 2.0 Service Provider (#124): JIM's own SCIM endpoint and the Connector that imports what SCIM clients
    /// write to it. Built hidden, layer by layer on <c>main</c>, then promoted to Preview once the JIM-to-JIM acceptance
    /// scenario passed, for at least one release before the flag is removed (design decision D16 in
    /// <c>engineering/plans/doing/SCIM_SERVER_DESIGN.md</c>).
    /// </summary>
    public static readonly FeatureFlagDefinition ScimServiceProvider = new(
        "Features.ScimServiceProvider",
        "SCIM 2.0 Service Provider",
        "Lets SCIM 2.0 clients, such as an identity provider, provision Users and Groups into JIM through its own SCIM endpoint, imported through the SCIM 2.0 Service Provider Connector.",
        FeatureFlagTier.Preview,
        TrackingIssueNumber: 2372);

    /// <summary>
    /// The declared flags. Add a <c>public static readonly FeatureFlagDefinition</c> field above this for each new
    /// flag (so gates can reference it by name), and list it here.
    /// </summary>
    private static readonly IReadOnlyList<FeatureFlagDefinition> Declared = [ScimServiceProvider];

    /// <summary>
    /// A test's substitute catalogue, scoped to its own asynchronous flow so concurrently running tests cannot see
    /// each other's. Null everywhere outside a test.
    /// </summary>
    private static readonly AsyncLocal<IReadOnlyList<FeatureFlagDefinition>?> Substitute = new();

    /// <summary>
    /// Every declared flag. The seeding pass converges the database to exactly this set: creating a row for a
    /// flag added here, and removing any <see cref="ServiceSettingCategory.FeatureFlags"/> row whose key is no
    /// longer present, so deleting a flag from this list leaves nothing behind.
    /// </summary>
    public static IReadOnlyList<FeatureFlagDefinition> All => Substitute.Value ?? Declared;

    /// <summary>
    /// Replaces the catalogue with <paramref name="definitions"/> for the calling asynchronous flow until the returned
    /// scope is disposed, so the feature-flag mechanism can be tested while no real flag exists. Test-only: reached
    /// through <c>JIM.TestSupport.FeatureFlagCatalogueScope</c>, never from product code.
    /// </summary>
    internal static IDisposable SubstituteForTesting(IReadOnlyList<FeatureFlagDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var previous = Substitute.Value;
        Substitute.Value = definitions;
        return new SubstituteScope(previous);
    }

    /// <summary>
    /// Restores the catalogue that was in force before <see cref="SubstituteForTesting"/>.
    /// </summary>
    private sealed class SubstituteScope(IReadOnlyList<FeatureFlagDefinition>? previous) : IDisposable
    {
        public void Dispose() => Substitute.Value = previous;
    }
}
