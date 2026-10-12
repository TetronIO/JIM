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
    public void ResolveSampleMatchValues_ExpressionOverATypedValue_ReturnsTheComputedValueInTheMetaverseType()
    {
        var resolution = _jim.ObjectMatching.ResolveSampleMatchValues(
            ExpressionRule("\"E\" + Trim(cs[\"employeeId\"])", AttributeDataType.Text), _objectType,
            new Dictionary<string, string?> { ["employeeId"] = " 1007 " });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Values));
            Assert.That(resolution.Values, Is.EqualTo(new object[] { "E1007" }));
            Assert.That(resolution.ValueType, Is.EqualTo(AttributeDataType.Text));
        }
    }

    [Test]
    public void ResolveSampleMatchValues_NumberInputConvertedToLongNumber_ComparesAsALongNumber()
    {
        // The sample is typed as text and read as the attribute's own type, exactly as an imported value would be.
        var resolution = _jim.ObjectMatching.ResolveSampleMatchValues(
            ExpressionRule("cs[\"employeeNumber\"]", AttributeDataType.LongNumber), _objectType,
            new Dictionary<string, string?> { ["employeeNumber"] = "1007" });

        Assert.That(resolution.Values, Is.EqualTo(new object[] { 1007L }));
    }

    [Test]
    public void ResolveSampleMatchValues_AttributeRule_ComparesTheSampleItself()
    {
        var rule = new ObjectMatchingRule
        {
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = AttributeDataType.Text },
            Sources = [new ObjectMatchingRuleSource { ConnectedSystemAttributeId = 101 }]
        };

        var resolution = _jim.ObjectMatching.ResolveSampleMatchValues(rule, _objectType,
            new Dictionary<string, string?> { ["employeeId"] = "E1007" });

        Assert.That(resolution.Values, Is.EqualTo(new object[] { "E1007" }));
    }

    [Test]
    public void ResolveSampleMatchValues_NoSampleForTheInput_IsNoValueSoTheNextRuleWouldBeTried()
    {
        var resolution = _jim.ObjectMatching.ResolveSampleMatchValues(
            ExpressionRule("cs[\"employeeId\"]", AttributeDataType.Text), _objectType,
            new Dictionary<string, string?> { ["employeeId"] = "" });

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.NoValue));
    }

    [Test]
    public void ResolveSampleMatchValues_SampleThatIsNotItsAttributesType_FailsNamingTheInput()
    {
        var resolution = _jim.ObjectMatching.ResolveSampleMatchValues(
            ExpressionRule("cs[\"employeeNumber\"]", AttributeDataType.Number), _objectType,
            new Dictionary<string, string?> { ["employeeNumber"] = "E1007" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
            Assert.That(resolution.FailureMessage, Does.Contain("cs[\"employeeNumber\"]").And.Contain("Number"));
        }
    }

    [Test]
    public void ResolveSampleMatchValues_ResultThatCannotBecomeTheMetaverseType_FailsAsARunWould()
    {
        var resolution = _jim.ObjectMatching.ResolveSampleMatchValues(
            ExpressionRule("\"E\" + cs[\"employeeId\"]", AttributeDataType.Number), _objectType,
            new Dictionary<string, string?> { ["employeeId"] = "1007" });

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
    }

    private static ObjectMatchingRule ExpressionRule(string expression, AttributeDataType targetType) => new()
    {
        TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = targetType },
        Sources = [new ObjectMatchingRuleSource { Expression = expression }]
    };
}
