// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The one pipeline that turns an Object Matching Rule and a Connected System Object's values into the values to
/// look up (#2052). Import join, export matching and both previews use it, so they cannot disagree about what a
/// rule computes. Its three outcomes are the contract: values to look up, no value (the next rule is tried), or a
/// failure that stops the object, never a value quietly skipped.
/// </summary>
public class MatchValueResolverTests
{
    private static readonly ConnectedSystemObjectTypeAttribute EmployeeNumber = new() { Id = 10, Name = "employeeNumber", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute ProxyAddresses = new() { Id = 11, Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
    private static readonly ConnectedSystemObjectTypeAttribute StaffNumber = new() { Id = 12, Name = "staffNumber", Type = AttributeDataType.Number };

    private static readonly MetaverseAttribute EmployeeId = new() { Id = 100, Name = "Employee ID", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute EmployeeNumberMv = new() { Id = 101, Name = "Employee Number", Type = AttributeDataType.Number };
    private static readonly MetaverseAttribute Email = new() { Id = 102, Name = "Email", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute Active = new() { Id = 103, Name = "Active", Type = AttributeDataType.Boolean };

    private MatchValueResolver _resolver = null!;

    [SetUp]
    public void SetUp() => _resolver = new MatchValueResolver(new DynamicExpressoEvaluator());

    [Test]
    public void Resolve_AttributeSourceWithAValue_ReturnsIt()
    {
        var resolution = _resolver.Resolve(AttributeRule(EmployeeNumber, EmployeeId), [Text(EmployeeNumber, "E100")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Values));
        Assert.That(resolution.Values, Is.EqualTo(new object[] { "E100" }));
        Assert.That(resolution.ValueType, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void Resolve_MultiValuedAttributeSource_ReturnsEveryDistinctValue()
    {
        // Every value is looked up; picking one would be an arbitrary choice on the administrator's behalf.
        var resolution = _resolver.Resolve(AttributeRule(ProxyAddresses, Email),
            [Text(ProxyAddresses, "smtp:a@corp.example"), Text(ProxyAddresses, "smtp:b@corp.example"), Text(ProxyAddresses, "smtp:a@corp.example")]);

        Assert.That(resolution.Values, Is.EquivalentTo(new object[] { "smtp:a@corp.example", "smtp:b@corp.example" }));
    }

    [Test]
    public void Resolve_AttributeSourceWithoutAValue_IsNoValue()
    {
        var resolution = _resolver.Resolve(AttributeRule(EmployeeNumber, EmployeeId), [Text(EmployeeNumber, "")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.NoValue));
    }

    [Test]
    public void Resolve_AttributeSourceOfAnotherType_Fails()
    {
        var resolution = _resolver.Resolve(AttributeRule(EmployeeNumber, EmployeeNumberMv), [Text(EmployeeNumber, "48213")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
        Assert.That(resolution.FailureMessage, Does.Contain("employeeNumber").And.Contain("Employee Number"));
    }

    [Test]
    public void Resolve_ExpressionSource_ReturnsTheReshapedValue()
    {
        var resolution = _resolver.Resolve(ExpressionRule("Trim(Replace(cs[\"employeeNumber\"], \"EMP-\", \"\"))", EmployeeId),
            [Text(EmployeeNumber, "EMP-48213 ")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Values));
        Assert.That(resolution.Values, Is.EqualTo(new object[] { "48213" }));
        Assert.That(resolution.Expression, Is.EqualTo("Trim(Replace(cs[\"employeeNumber\"], \"EMP-\", \"\"))"));
    }

    [Test]
    public void Resolve_ExpressionSource_ConvertsToTheMetaverseAttributesType()
    {
        var resolution = _resolver.Resolve(ExpressionRule("Replace(cs[\"employeeNumber\"], \"EMP-\", \"\")", EmployeeNumberMv),
            [Text(EmployeeNumber, "EMP-48213")]);

        Assert.That(resolution.Values, Is.EqualTo(new object[] { 48213 }));
        Assert.That(resolution.ValueType, Is.EqualTo(AttributeDataType.Number));
    }

    [Test]
    public void Resolve_ExpressionResultThatCannotBeConverted_Fails()
    {
        // A value that cannot be represented is never skipped: skipping would let the object project a duplicate.
        var resolution = _resolver.Resolve(ExpressionRule("cs[\"employeeNumber\"]", EmployeeNumberMv), [Text(EmployeeNumber, "EMP-48213")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
        Assert.That(resolution.FailureMessage, Does.Contain("EMP-48213").And.Contain("Number"));
    }

    [TestCase("null")]
    [TestCase("\"\"")]
    [TestCase("\"   \"")]
    public void Resolve_ExpressionReturningNoText_IsNoValue(string expression)
    {
        var resolution = _resolver.Resolve(ExpressionRule(expression, EmployeeId), [Text(EmployeeNumber, "E100")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.NoValue));
    }

    [Test]
    public void Resolve_ExpressionWithAMissingInput_IsEvaluatedAndCanBeNoValue()
    {
        // A missing input is not an error: the expression runs with it absent, and here produces nothing.
        var resolution = _resolver.Resolve(ExpressionRule("Trim(cs[\"employeeNumber\"])", EmployeeId), []);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.NoValue));
    }

    [Test]
    public void Resolve_ExpressionThatDoesNotParse_FailsNamingTheExpression()
    {
        var resolution = _resolver.Resolve(ExpressionRule("Trim(cs[\"employeeNumber\"]", EmployeeId), [Text(EmployeeNumber, "E100")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
        Assert.That(resolution.Expression, Is.EqualTo("Trim(cs[\"employeeNumber\"]"));
        Assert.That(resolution.FailureMessage, Is.Not.Empty);
    }

    [Test]
    public void Resolve_ExpressionReturningSeveralValues_ReturnsEach()
    {
        var resolution = _resolver.Resolve(ExpressionRule("Split(cs[\"employeeNumber\"], \";\")", EmployeeId), [Text(EmployeeNumber, "E100;E200; ")]);

        Assert.That(resolution.Values, Is.EqualTo(new object[] { "E100", "E200" }));
    }

    [Test]
    public void Resolve_ExpressionReturningSeveralNumbers_ReturnsEachConverted()
    {
        // Any collection gives candidates (#2179), not only a list of text.
        var resolution = _resolver.Resolve(ExpressionRule("new int[] { 1, 2, 1 }", EmployeeNumberMv), [Text(EmployeeNumber, "E100")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Values));
            Assert.That(resolution.Values, Is.EqualTo(new object[] { 1, 2 }));
        }
    }

    [Test]
    public void Resolve_ExpressionReturningAListWithAnUnconvertibleValue_Fails()
    {
        var resolution = _resolver.Resolve(ExpressionRule("Split(\"1;two\", \";\")", EmployeeNumberMv), [Text(EmployeeNumber, "E100")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
    }

    [Test]
    public void Resolve_ExpressionFailingWithANullReference_Fails()
    {
        // Every failure an expression can cause is a failed resolution, never an unhandled error (#2179).
        var resolution = _resolver.Resolve(ExpressionRule("cs[\"missing\"].ToString()", EmployeeId), [Text(EmployeeNumber, "E100")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
    }

    [Test]
    public void Resolve_ExpressionReadsInputsByNameIgnoringCase()
    {
        var resolution = _resolver.Resolve(ExpressionRule("cs[\"EMPLOYEENUMBER\"]", EmployeeId), [Text(EmployeeNumber, "E100")]);

        Assert.That(resolution.Values, Is.EqualTo(new object[] { "E100" }));
    }

    [Test]
    public void Resolve_ExpressionReadsANumberInputAsANumber()
    {
        var resolution = _resolver.Resolve(ExpressionRule("cs[\"staffNumber\"]", EmployeeNumberMv),
            [new ConnectedSystemObjectAttributeValue { AttributeId = StaffNumber.Id, Attribute = StaffNumber, IntValue = 42 }]);

        Assert.That(resolution.Values, Is.EqualTo(new object[] { 42 }));
    }

    [Test]
    public void Resolve_RuleWithSeveralSources_Fails()
    {
        var rule = AttributeRule(EmployeeNumber, EmployeeId);
        rule.Sources.Add(new ObjectMatchingRuleSource { Order = 1, ConnectedSystemAttribute = ProxyAddresses, ConnectedSystemAttributeId = ProxyAddresses.Id });

        var resolution = _resolver.Resolve(rule, [Text(EmployeeNumber, "E100")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
        Assert.That(resolution.FailureMessage, Does.Contain("one source"));
    }

    [Test]
    public void Resolve_RuleOnAMetaverseAttributeMatchingCannotCompare_Fails()
    {
        var resolution = _resolver.Resolve(ExpressionRule("true", Active), [Text(EmployeeNumber, "E100")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
        Assert.That(resolution.FailureMessage, Does.Contain("Active"));
    }

    [Test]
    public void Resolve_RuleWithoutAMetaverseAttribute_Fails()
    {
        var rule = new ObjectMatchingRule { Id = 1, Sources = [new ObjectMatchingRuleSource { Expression = "cs[\"employeeNumber\"]" }] };

        var resolution = _resolver.Resolve(rule, [Text(EmployeeNumber, "E100")]);

        Assert.That(resolution.Kind, Is.EqualTo(MatchValueResolutionKind.Failed));
    }

    private static ObjectMatchingRule AttributeRule(ConnectedSystemObjectTypeAttribute source, MetaverseAttribute target) => new()
    {
        Id = 1,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id }]
    };

    private static ObjectMatchingRule ExpressionRule(string expression, MetaverseAttribute target) => new()
    {
        Id = 1,
        TargetMetaverseAttribute = target,
        TargetMetaverseAttributeId = target.Id,
        Sources = [new ObjectMatchingRuleSource { Order = 0, Expression = expression }]
    };

    private static ConnectedSystemObjectAttributeValue Text(ConnectedSystemObjectTypeAttribute attribute, string value) =>
        new() { AttributeId = attribute.Id, Attribute = attribute, StringValue = value };
}
