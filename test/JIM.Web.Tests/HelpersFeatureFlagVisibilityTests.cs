// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.TestSupport;
using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Service Settings table's feature-flag (#1781) visibility rule. The real <see cref="FeatureFlagCatalogue"/>
/// declares no flag while no feature is behind one, so the rule is proven against synthetic definitions: the
/// <see cref="FeatureFlagDefinition"/>-taking overload directly, and the string-key overload (what the page actually
/// calls) through a substituted catalogue (<see cref="FeatureFlagCatalogueScope"/>).
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
    public void IsFeatureFlagVisible_ByKey_CatalogueInDevelopmentFlag_MatchesTheDefinitionRule()
    {
        // Proves the string-key overload (what Settings.razor actually calls) resolves the key through the catalogue
        // and delegates to the definition rule.
        using var catalogue = FeatureFlagCatalogueScope.Use(FeatureFlagCatalogueScope.InDevelopmentFlag);
        var key = FeatureFlagCatalogueScope.InDevelopmentFlag.Key;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Helpers.IsFeatureFlagVisible(key, includeInDevelopment: false), Is.False);
            Assert.That(Helpers.IsFeatureFlagVisible(key, includeInDevelopment: true), Is.True);
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
    public void GetFeatureFlagDefinition_CatalogueKey_ReturnsTheDefinition()
    {
        using var catalogue = FeatureFlagCatalogueScope.Use(FeatureFlagCatalogueScope.InDevelopmentFlag);

        var definition = Helpers.GetFeatureFlagDefinition(FeatureFlagCatalogueScope.InDevelopmentFlag.Key);

        Assert.That(definition, Is.SameAs(FeatureFlagCatalogueScope.InDevelopmentFlag));
    }

    [Test]
    public void GetFeatureFlagDefinition_UnknownKey_ReturnsNull()
    {
        Assert.That(Helpers.GetFeatureFlagDefinition("Features.DoesNotExist"), Is.Null);
    }
}
