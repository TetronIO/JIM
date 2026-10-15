// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Expressions;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// The run-time half of refusing reflection in expressions (#2053): every function that hands a value on, and the
/// evaluator's result, refuse a reflection object or a delegate, so one that got past the parse-time guards still
/// goes nowhere.
/// </summary>
[TestFixture]
public class ExpressionValueSetsTests
{
    private static readonly object[] ReflectionObjects =
    [
        typeof(string),
        typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!,
        typeof(string).Assembly,
        typeof(string).Module,
        (Func<int>)(() => 1)
    ];

    [TestCaseSource(nameof(ReflectionObjects))]
    public void RefuseReflection_AReflectionObjectOrDelegate_IsAnExpressionError(object value)
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => ExpressionValueSets.RefuseReflection(value));

        Assert.That(ex!.Message, Does.Contain("Reflection"));
    }

    [TestCaseSource(nameof(ReflectionObjects))]
    public void Map_AReflectionObject_IsRefusedBeforeTheTransformSeesIt(object value)
    {
        var transformed = false;

        Assert.Throws<ExpressionEvaluationException>(() => ExpressionValueSets.Map(value, v => { transformed = true; return v; }));
        Assert.That(transformed, Is.False);
    }

    [TestCase("text")]
    [TestCase(42)]
    [TestCase(null)]
    public void RefuseReflection_AnOrdinaryValue_IsReturnedAsItIs(object? value)
    {
        Assert.That(ExpressionValueSets.RefuseReflection(value), Is.EqualTo(value));
    }
}
