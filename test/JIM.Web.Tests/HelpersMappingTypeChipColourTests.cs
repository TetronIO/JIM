// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Web;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Attribute Flow mapping type chip's colour comes from one place. Every declared
/// <see cref="SyncRuleMappingSourcesType"/> must have its own explicit entry rather than falling into
/// the default, so a new mapping type never renders as an unremarkable grey chip nobody noticed lacked
/// its own colour.
/// </summary>
[TestFixture]
public class HelpersMappingTypeChipColourTests
{
    [TestCase(SyncRuleMappingSourcesType.NotSet, Color.Default)]
    [TestCase(SyncRuleMappingSourcesType.AttributeMapping, Color.Info)]
    [TestCase(SyncRuleMappingSourcesType.ExpressionMapping, Color.Tertiary)]
    [TestCase(SyncRuleMappingSourcesType.AdvancedMapping, Color.Warning)]
    [TestCase(SyncRuleMappingSourcesType.GeneratedMapping, Color.Primary)]
    public void GetMappingTypeChipColour_EachSourceType_HasItsOwnColour(SyncRuleMappingSourcesType sourceType, Color expected)
    {
        Assert.That(Helpers.GetMappingTypeChipColour(sourceType), Is.EqualTo(expected));
    }

    [Test]
    public void GetMappingTypeChipColour_EveryDeclaredValue_IsExplicitlyMapped()
    {
        var unmapped = Enum.GetValues<SyncRuleMappingSourcesType>()
            .Where(type => type != SyncRuleMappingSourcesType.NotSet
                && Helpers.GetMappingTypeChipColour(type) == Color.Default)
            .ToList();

        Assert.That(unmapped, Is.Empty,
            "new mapping source type(s) fell through to the default colour: " + string.Join(", ", unmapped));
    }
}
