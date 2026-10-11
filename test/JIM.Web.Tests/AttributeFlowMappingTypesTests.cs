// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Mapping Type an Attribute Flow is filtered under on the Attribute Flow tab: its source type, except that an import
/// Expression reading the Metaverse is a Derived Attribute Flow (#1750), so derived and generated flows can be picked out
/// together or apart. The categories never overlap.
/// </summary>
[TestFixture]
public class AttributeFlowMappingTypesTests
{
    private static SyncRuleMapping Expression(string expression, bool generated = false)
    {
        var mapping = new SyncRuleMapping
        {
            Generation = generated ? new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken } : null
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        return mapping;
    }

    [Test]
    public void Of_ImportExpressionReadingTheMetaverse_IsDerived()
    {
        Assert.That(AttributeFlowMappingTypes.Of(Expression("mv[\"Account Name\"] + \"@panoply.local\""), SyncRuleDirection.Import),
            Is.EqualTo(AttributeFlowMappingType.Derived));
    }

    [Test]
    public void Of_ImportExpressionReadingOnlyTheConnectedSystem_IsAnExpression()
    {
        Assert.That(AttributeFlowMappingTypes.Of(Expression("cs[\"firstName\"]"), SyncRuleDirection.Import),
            Is.EqualTo(AttributeFlowMappingType.ExpressionMapping));
    }

    [Test]
    public void Of_ExportExpression_IsAnExpressionEvenThoughItReadsTheMetaverse()
    {
        // Every export expression reads the Metaverse; that is what export does, not a derived flow.
        Assert.That(AttributeFlowMappingTypes.Of(Expression("mv[\"Email\"]"), SyncRuleDirection.Export),
            Is.EqualTo(AttributeFlowMappingType.ExpressionMapping));
    }

    [Test]
    public void Of_GeneratedValueWhoseBaseReadsTheMetaverse_IsGenerated()
    {
        Assert.That(AttributeFlowMappingTypes.Of(Expression("mv[\"First Name\"]", generated: true), SyncRuleDirection.Import),
            Is.EqualTo(AttributeFlowMappingType.GeneratedMapping));
    }

    [Test]
    public void Of_DirectAttributeFlow_IsAnAttributeMapping()
    {
        var mapping = new SyncRuleMapping();
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "givenName" } });

        Assert.That(AttributeFlowMappingTypes.Of(mapping, SyncRuleDirection.Import), Is.EqualTo(AttributeFlowMappingType.AttributeMapping));
    }
}
