// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

[TestFixture]
public class SyncRuleMappingGetSourceTypeTests
{
    [Test]
    public void GetSourceType_GenerationSetWithNoSources_ReturnsGeneratedMapping()
    {
        // A Sequence or Random token needs no base expression, so a generated mapping can legitimately have
        // zero sources; GetSourceType() must not fall through to NotSet.
        var mapping = new SyncRuleMapping
        {
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence }
        };

        Assert.That(mapping.GetSourceType(), Is.EqualTo(SyncRuleMappingSourcesType.GeneratedMapping));
    }

    [Test]
    public void GetSourceType_GenerationSetWithOneExpressionSource_ReturnsGeneratedMapping()
    {
        var mapping = new SyncRuleMapping
        {
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken }
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        Assert.That(mapping.GetSourceType(), Is.EqualTo(SyncRuleMappingSourcesType.GeneratedMapping));
    }

    [Test]
    public void GetSourceType_GenerationNullWithExpressionSource_ReturnsExpressionMapping()
    {
        // Existing behaviour must be unchanged for a mapping that carries no generation row.
        var mapping = new SyncRuleMapping();
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"First Name\"]" });

        Assert.That(mapping.GetSourceType(), Is.EqualTo(SyncRuleMappingSourcesType.ExpressionMapping));
    }

    [Test]
    public void GetSourceType_GenerationNullWithNoSources_ReturnsNotSet()
    {
        var mapping = new SyncRuleMapping();

        Assert.That(mapping.GetSourceType(), Is.EqualTo(SyncRuleMappingSourcesType.NotSet));
    }
}
