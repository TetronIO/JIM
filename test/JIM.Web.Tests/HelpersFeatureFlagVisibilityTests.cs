// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Service Settings table's feature-flag (#1781) visibility rule. The real <see cref="FeatureFlagCatalogue"/>
/// carries no Preview-tier entry today (its one entry, Unique Value Generation, is In Development), which would
/// otherwise leave "a Preview row always appears" unprovable against real catalogue data. The
/// <see cref="FeatureFlagDefinition"/>-taking overload is exercised directly against a synthetic Preview
/// definition for exactly this reason; the string-key overload (what the page actually calls) is proven
/// separately against the real catalogue's actual key, so a rename or removal of that key would be caught here.
/// </summary>
[TestFixture]
public class HelpersFeatureFlagVisibilityTests
{
    private static readonly FeatureFlagDefinition SyntheticPreviewFlag = new(
        Key: "Features.SyntheticPreviewFlagForTesting",
        DisplayName: "Synthetic Preview Flag",
        Description: "A synthetic Preview-tier definition, used only to prove the visibility rule.",
        Tier: FeatureFlagTier.Preview,
        TrackingIssueNumber: 0);

    private static readonly FeatureFlagDefinition SyntheticInDevelopmentFlag = SyntheticPreviewFlag with
    {
        Key = "Features.SyntheticInDevelopmentFlagForTesting",
        Tier = FeatureFlagTier.InDevelopment
    };

    [Test]
    public void IsFeatureFlagVisible_PreviewTierDefinition_IsAlwaysVisible()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Helpers.IsFeatureFlagVisible(SyntheticPreviewFlag, includeInDevelopment: false), Is.True);
            Assert.That(Helpers.IsFeatureFlagVisible(SyntheticPreviewFlag, includeInDevelopment: true), Is.True);
        }
    }

    [Test]
    public void IsFeatureFlagVisible_InDevelopmentTierDefinition_VisibleOnlyWhenIncludeInDevelopment()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Helpers.IsFeatureFlagVisible(SyntheticInDevelopmentFlag, includeInDevelopment: false), Is.False);
            Assert.That(Helpers.IsFeatureFlagVisible(SyntheticInDevelopmentFlag, includeInDevelopment: true), Is.True);
        }
    }

    [Test]
    public void IsFeatureFlagVisible_NullDefinition_IsNeverVisible()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Helpers.IsFeatureFlagVisible((FeatureFlagDefinition?)null, includeInDevelopment: false), Is.False);
            Assert.That(Helpers.IsFeatureFlagVisible((FeatureFlagDefinition?)null, includeInDevelopment: true), Is.False);
        }
    }

    [Test]
    public void IsFeatureFlagVisible_ByKey_RealCatalogueInDevelopmentFlag_MatchesTheDefinitionRule()
    {
        // Proves the string-key overload (what Settings.razor actually calls) delegates correctly, against the
        // real catalogue's one live entry, so a rename or tier change of it would fail this test.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                Helpers.IsFeatureFlagVisible(FeatureFlagCatalogue.UniqueValueGeneration.Key, includeInDevelopment: false),
                Is.False);
            Assert.That(
                Helpers.IsFeatureFlagVisible(FeatureFlagCatalogue.UniqueValueGeneration.Key, includeInDevelopment: true),
                Is.True);
        }
    }

    [Test]
    public void IsFeatureFlagVisible_ByKey_UnknownKey_IsNeverVisible()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Helpers.IsFeatureFlagVisible("Features.DoesNotExist", includeInDevelopment: false), Is.False);
            Assert.That(Helpers.IsFeatureFlagVisible("Features.DoesNotExist", includeInDevelopment: true), Is.False);
        }
    }

    [Test]
    public void GetFeatureFlagDefinition_RealCatalogueKey_ReturnsTheDefinition()
    {
        var definition = Helpers.GetFeatureFlagDefinition(FeatureFlagCatalogue.UniqueValueGeneration.Key);

        Assert.That(definition, Is.SameAs(FeatureFlagCatalogue.UniqueValueGeneration));
    }

    [Test]
    public void GetFeatureFlagDefinition_UnknownKey_ReturnsNull()
    {
        Assert.That(Helpers.GetFeatureFlagDefinition("Features.DoesNotExist"), Is.Null);
    }
}
