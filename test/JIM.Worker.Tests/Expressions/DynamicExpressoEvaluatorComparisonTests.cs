// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Expressions;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// The expression comparison functions compare text exactly, character by character (#2051): a condition means what
/// it says, so characters that a culture-aware comparison ignores, such as a soft hyphen, or a different encoding of
/// an accented letter, are not treated as absent or equal.
/// </summary>
[TestFixture]
public class DynamicExpressoEvaluatorComparisonTests
{
    private const string SoftHyphen = "­";

    private DynamicExpressoEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp() => _evaluator = new DynamicExpressoEvaluator();

    private object? Evaluate(string expression, string value) =>
        _evaluator.Evaluate(expression, new ExpressionContext(new Dictionary<string, object?> { { "Value", value } }));

    [Test]
    public void StartsWith_ValueBeginsWithIgnorableCharacter_ReturnsFalse()
    {
        Assert.That(Evaluate("StartsWith(mv[\"Value\"], \"admin\")", SoftHyphen + "admin@company.com"), Is.False);
    }

    [Test]
    public void EndsWith_ValueEndsWithIgnorableCharacter_ReturnsFalse()
    {
        Assert.That(Evaluate("EndsWith(mv[\"Value\"], \"company.com\")", "admin@company.com" + SoftHyphen), Is.False);
    }

    [Test]
    public void StartsWith_PrefixIsDecomposedFormOfComposedValue_ReturnsFalse()
    {
        // "Å" as one code point (U+00C5) against "A" followed by a combining ring above (U+030A).
        Assert.That(Evaluate("StartsWith(mv[\"Value\"], \"Å\")", "Åsa"), Is.False);
    }

    [Test]
    public void StartsWith_PrefixDiffersOnlyInCase_ReturnsFalse()
    {
        Assert.That(Evaluate("StartsWith(mv[\"Value\"], \"ADMIN\")", "admin@company.com"), Is.False);
    }

    [Test]
    public void EndsWith_SuffixDiffersOnlyInCase_ReturnsFalse()
    {
        Assert.That(Evaluate("EndsWith(mv[\"Value\"], \"@COMPANY.COM\")", "admin@company.com"), Is.False);
    }
}
