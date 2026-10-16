// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Try it (#2052): what an Object Matching Rule would compare for an object holding the values an administrator types,
/// worked out by the same resolver synchronisation uses, so the answer in the editor cannot differ from the answer a
/// run gives.
/// </summary>
[TestFixture]
public class ObjectMatchingSampleTests
{
    private JimApplication _jim = null!;
    private ConnectedSystemObjectType _objectType = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _jim = new JimApplication(new Mock<IRepository>().Object);
        _objectType = new ConnectedSystemObjectType
        {
            Id = 7,
            Name = "user",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "employeeId", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 102, Name = "employeeNumber", Type = AttributeDataType.Number }
            ]
        };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task TestAsync_ExpressionOverATypedValue_ReturnsTheComputedValueInTheMetaverseTypeAsync()
    {
        var resolution = (await _jim.ObjectMatching.TestAsync(ExpressionRule("\"E\" + Trim(cs[\"employeeId\"])", AttributeDataType.Text), _objectType,
            [new AttributeSample("employeeId", [" 1007 "])], lookUp: false)).Resolution!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Values));
            Assert.That(resolution.Values, Is.EqualTo(new object[] { "E1007" }));
            Assert.That(resolution.ValueType, Is.EqualTo(AttributeDataType.Text));
        }
    }

    [Test]
    public async Task TestAsync_NumberInputConvertedToLongNumber_ComparesAsALongNumberAsync()
    {
        // The sample is typed as text and read as the attribute's own type, exactly as an imported value would be.
        var resolution = (await _jim.ObjectMatching.TestAsync(ExpressionRule("cs[\"employeeNumber\"]", AttributeDataType.LongNumber), _objectType,
            [new AttributeSample("employeeNumber", ["1007"])], lookUp: false)).Resolution!;

        Assert.That(resolution.Values, Is.EqualTo(new object[] { 1007L }));
    }

    [Test]
    public async Task TestAsync_AttributeRule_ComparesTheSampleItselfAsync()
    {
        var rule = new ObjectMatchingRule
        {
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = AttributeDataType.Text },
            Sources = [new ObjectMatchingRuleSource { ConnectedSystemAttributeId = 101 }]
        };

        var resolution = (await _jim.ObjectMatching.TestAsync(rule, _objectType,
            [new AttributeSample("employeeId", ["E1007"])], lookUp: false)).Resolution!;

        Assert.That(resolution.Values, Is.EqualTo(new object[] { "E1007" }));
    }

    [Test]
    public async Task TestAsync_NoSampleForTheInput_IsNoValueSoTheNextRuleWouldBeTriedAsync()
    {
        var resolution = (await _jim.ObjectMatching.TestAsync(ExpressionRule("cs[\"employeeId\"]", AttributeDataType.Text), _objectType,
            [new AttributeSample("employeeId", [])], lookUp: false)).Resolution!;

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.NoValue));
    }

    [Test]
    public async Task TestAsync_SampleThatIsNotItsAttributesType_IsAnInputErrorNamingTheInputAsync()
    {
        var result = await _jim.ObjectMatching.TestAsync(
            ExpressionRule("cs[\"employeeNumber\"]", AttributeDataType.Number), _objectType,
            [new AttributeSample("employeeNumber", ["E1007"])], lookUp: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Resolution, Is.Null);
            Assert.That(result.InputErrors.Single(), Does.Contain("cs[\"employeeNumber\"]").And.Contain("Number"));
        }
    }

    [Test]
    public async Task TestAsync_ResultThatCannotBecomeTheMetaverseType_FailsAsARunWouldAsync()
    {
        var resolution = (await _jim.ObjectMatching.TestAsync(ExpressionRule("\"E\" + cs[\"employeeId\"]", AttributeDataType.Number), _objectType,
            [new AttributeSample("employeeId", ["1007"])], lookUp: false)).Resolution!;

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
    }

    private static ObjectMatchingRule ExpressionRule(string expression, AttributeDataType targetType) => new()
    {
        TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = targetType },
        Sources = [new ObjectMatchingRuleSource { Expression = expression }]
    };
}
