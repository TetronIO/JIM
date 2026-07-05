// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests;

/// <summary>
/// Tests the coarse Activity category grouping used by the Activities list quick-filter: every target type must
/// belong to exactly one category, so a category can be expanded to a target-type filter without gaps or overlaps.
/// </summary>
[TestFixture]
public class ActivityTargetCategoryTests
{
    [Test]
    public void GetCategory_EveryTargetType_IsCategorised()
    {
        foreach (var targetType in Enum.GetValues<ActivityTargetType>())
        {
            Assert.That(() => ActivityTargetTypeCategories.GetCategory(targetType), Throws.Nothing,
                $"every ActivityTargetType must map to a category; '{targetType}' does not. " +
                "When adding a new target type, add it to ActivityTargetTypeCategories.");
        }
    }

    [Test]
    public void GetCategory_ConfigurationObjects_MapToConfiguration()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.ConnectedSystem), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.SyncRule), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.ObjectMatchingRule), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.MetaverseAttribute), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.MetaverseObjectType), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.ServiceSetting), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.Schedule), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.TrustedCertificate), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.ApiKey), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.Role), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.PredefinedSearch), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.ConnectorDefinition), Is.EqualTo(ActivityTargetCategory.Configuration));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.ExampleDataSet), Is.EqualTo(ActivityTargetCategory.Configuration));
        });
    }

    [Test]
    public void GetCategory_OtherGroups_MapAsExpected()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.MetaverseObject), Is.EqualTo(ActivityTargetCategory.IdentityData));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.ConnectedSystemRunProfile), Is.EqualTo(ActivityTargetCategory.SyncRuns));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.TemporalScopeReconciliation), Is.EqualTo(ActivityTargetCategory.SyncRuns));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.HistoryRetentionCleanup), Is.EqualTo(ActivityTargetCategory.System));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.System), Is.EqualTo(ActivityTargetCategory.System));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.NotSet), Is.EqualTo(ActivityTargetCategory.System));
            Assert.That(ActivityTargetTypeCategories.GetCategory(ActivityTargetType.ExampleDataTemplate), Is.EqualTo(ActivityTargetCategory.System));
        });
    }

    [Test]
    public void GetTargetTypes_RoundTripsWithGetCategory()
    {
        foreach (var category in Enum.GetValues<ActivityTargetCategory>())
        {
            var types = ActivityTargetTypeCategories.GetTargetTypes(category);
            Assert.That(types, Is.Not.Empty, $"category '{category}' must contain at least one target type");
            foreach (var type in types)
                Assert.That(ActivityTargetTypeCategories.GetCategory(type), Is.EqualTo(category));
        }

        var allGroupedTypes = Enum.GetValues<ActivityTargetCategory>()
            .SelectMany(ActivityTargetTypeCategories.GetTargetTypes)
            .ToList();
        Assert.That(allGroupedTypes, Is.Unique, "a target type must not appear in more than one category");
        Assert.That(allGroupedTypes, Is.EquivalentTo(Enum.GetValues<ActivityTargetType>()),
            "the categories must partition the full set of target types");
    }
}
