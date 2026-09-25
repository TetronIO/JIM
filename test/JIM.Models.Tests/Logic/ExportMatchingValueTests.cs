// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// <see cref="ExportMatchingValue.Resolve"/> reproduces
/// <c>ConnectedSystemRepository.FindConnectedSystemObjectUsingMatchingRuleAsync</c>'s value-extraction
/// steps exactly, so both the per-object export-matching query and the page-scoped batch candidate
/// query agree on what counts as a resolvable value. These tests exercise every outcome and every
/// supported attribute type.
/// </summary>
[TestFixture]
public class ExportMatchingValueTests
{
    private static ConnectedSystemObjectTypeAttribute CreateConnectedSystemAttribute(string name = "employeeId")
        => new() { Id = 1, Name = name, Type = AttributeDataType.Text };

    private static MetaverseAttribute CreateMetaverseAttribute(int id, AttributeDataType type)
        => new() { Id = id, Name = "Employee Id", Type = type };

    private static ObjectMatchingRule CreateRule(
        MetaverseAttribute? targetAttribute,
        ConnectedSystemObjectTypeAttribute? sourceAttribute,
        bool caseSensitive = false,
        int sourceCount = 1)
    {
        var rule = new ObjectMatchingRule
        {
            Id = 1,
            TargetMetaverseAttribute = targetAttribute,
            TargetMetaverseAttributeId = targetAttribute?.Id,
            CaseSensitive = caseSensitive
        };

        for (var i = 0; i < sourceCount; i++)
        {
            rule.Sources.Add(new ObjectMatchingRuleSource
            {
                Id = i + 1,
                Order = i,
                ConnectedSystemAttribute = sourceAttribute,
                ConnectedSystemAttributeId = sourceAttribute?.Id
            });
        }

        return rule;
    }

    #region Non-resolved outcomes

    [Test]
    public void Resolve_RuleHasNoSources_ReturnsNoSources()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Text);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute(), sourceCount: 0);
        var mvo = new MetaverseObject();

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.NoSources));
    }

    [Test]
    public void Resolve_RuleHasMultipleSources_ReturnsMultipleSources()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Text);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute(), sourceCount: 2);
        var mvo = new MetaverseObject();

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.MultipleSources));
    }

    [Test]
    public void Resolve_SourceHasNoConnectedSystemAttribute_ReturnsNoConnectedSystemAttribute()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Text);
        var rule = CreateRule(targetAttribute, sourceAttribute: null);
        var mvo = new MetaverseObject();

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.NoConnectedSystemAttribute));
    }

    [Test]
    public void Resolve_RuleHasNoTargetMetaverseAttribute_ReturnsNoTargetMetaverseAttribute()
    {
        var rule = CreateRule(targetAttribute: null, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject();

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.NoTargetMetaverseAttribute));
    }

    [Test]
    public void Resolve_MvoHasNoAttributeValueRow_ReturnsNoMetaverseValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Text);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject(); // no attribute values at all

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.NoMetaverseValue));
    }

    [TestCase("")]
    [TestCase(null)]
    public void Resolve_TextValueNullOrEmpty_ReturnsNoMetaverseValue(string? stringValue)
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Text);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10, StringValue = stringValue }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.NoMetaverseValue));
    }

    [Test]
    public void Resolve_NumberValueMissing_ReturnsNoMetaverseValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Number);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10 }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.NoMetaverseValue));
    }

    [Test]
    public void Resolve_LongNumberValueMissing_ReturnsNoMetaverseValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.LongNumber);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10 }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.NoMetaverseValue));
    }

    [Test]
    public void Resolve_DecimalValueMissing_ReturnsNoMetaverseValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Decimal);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10 }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.NoMetaverseValue));
    }

    [Test]
    public void Resolve_GuidValueMissing_ReturnsNoMetaverseValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Guid);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10 }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.NoMetaverseValue));
    }

    [TestCase(AttributeDataType.DateTime)]
    [TestCase(AttributeDataType.Binary)]
    [TestCase(AttributeDataType.Reference)]
    [TestCase(AttributeDataType.Boolean)]
    [TestCase(AttributeDataType.NotSet)]
    public void Resolve_UnsupportedAttributeType_ReturnsUnsupportedAttributeType(AttributeDataType dataType)
    {
        var targetAttribute = CreateMetaverseAttribute(10, dataType);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10, StringValue = "some value" }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.UnsupportedAttributeType));
    }

    #endregion

    #region Resolved, per supported type

    [Test]
    public void Resolve_TextValuePresent_ReturnsResolvedWithStringValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Text);
        var sourceAttribute = CreateConnectedSystemAttribute("employeeId");
        var rule = CreateRule(targetAttribute, sourceAttribute, caseSensitive: true);
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10, StringValue = "E12345" }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.Resolved));
            Assert.That(result.DataType, Is.EqualTo(AttributeDataType.Text));
            Assert.That(result.Value, Is.EqualTo("E12345"));
            Assert.That(result.ConnectedSystemAttributeName, Is.EqualTo("employeeId"));
            Assert.That(result.CaseSensitive, Is.True);
        }
    }

    [Test]
    public void Resolve_NumberValuePresent_ReturnsResolvedWithIntValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Number);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10, IntValue = 42 }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.Resolved));
            Assert.That(result.DataType, Is.EqualTo(AttributeDataType.Number));
            Assert.That(result.Value, Is.EqualTo(42));
        }
    }

    [Test]
    public void Resolve_LongNumberValuePresent_ReturnsResolvedWithLongValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.LongNumber);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10, LongValue = 9_000_000_000L }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.Resolved));
            Assert.That(result.DataType, Is.EqualTo(AttributeDataType.LongNumber));
            Assert.That(result.Value, Is.EqualTo(9_000_000_000L));
        }
    }

    [Test]
    public void Resolve_DecimalValuePresent_ReturnsResolvedWithDecimalValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Decimal);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10, DecimalValue = 5.00m }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.Resolved));
            Assert.That(result.DataType, Is.EqualTo(AttributeDataType.Decimal));
            Assert.That(result.Value, Is.EqualTo(5.00m));
        }
    }

    [Test]
    public void Resolve_GuidValuePresent_ReturnsResolvedWithGuidValue()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Guid);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var guid = Guid.NewGuid();
        var mvo = new MetaverseObject
        {
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10, GuidValue = guid }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.Resolved));
            Assert.That(result.DataType, Is.EqualTo(AttributeDataType.Guid));
            Assert.That(result.Value, Is.EqualTo(guid));
        }
    }

    #endregion

    #region MVO value lookup matches AttributeId or Attribute navigation Id

    [Test]
    public void Resolve_ValueMatchesByAttributeIdScalar_ReturnsResolved()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Text);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());
        var mvo = new MetaverseObject
        {
            // AttributeId scalar matches; no Attribute navigation loaded.
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = 10, StringValue = "value-by-scalar-id" }]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.Resolved));
        Assert.That(result.Value, Is.EqualTo("value-by-scalar-id"));
    }

    [Test]
    public void Resolve_ValueMatchesByAttributeNavigationId_ReturnsResolved()
    {
        var targetAttribute = CreateMetaverseAttribute(10, AttributeDataType.Text);
        var rule = CreateRule(targetAttribute, CreateConnectedSystemAttribute());

        // AttributeId scalar deliberately left at its default (0, not matching), so the lookup can only
        // succeed via the Attribute navigation's Id, exercising the FirstOrDefault's second predicate branch.
        var mvo = new MetaverseObject
        {
            AttributeValues =
            [
                new MetaverseObjectAttributeValue
                {
                    AttributeId = 0,
                    Attribute = targetAttribute,
                    StringValue = "value-by-navigation-id"
                }
            ]
        };

        var result = ExportMatchingValue.Resolve(mvo, rule);

        Assert.That(result.Outcome, Is.EqualTo(ExportMatchingOutcome.Resolved));
        Assert.That(result.Value, Is.EqualTo("value-by-navigation-id"));
    }

    #endregion
}
