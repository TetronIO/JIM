// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using DynamicExpresso.Exceptions;
using JIM.Application.Expressions;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// Proof tests for the OWASP #500 DynamicExpresso input path review (see
/// <c>engineering/EXPRESSION_SECURITY.md</c>). These tests do not change evaluator behaviour; they verify,
/// against the real evaluator, that reflection and type-escape attempts fail to parse or evaluate. Every
/// assertion here is cross-referenced from the review document, so a test rename or removal must be mirrored
/// there.
/// </summary>
[TestFixture]
public class DynamicExpressoEvaluatorSecurityTests
{
    private IExpressionEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new DynamicExpressoEvaluator();
    }

    #region Chained Reflection Escape Attempts

    [Test]
    public void Evaluate_ChainedGetTypeGetMethods_ThrowsReflectionNotAllowedException()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "a", "some value" } },
            new Dictionary<string, object?>());

        Assert.Throws<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("mv[\"a\"].GetType().GetMethods()", context));
    }

    [Test]
    public void Evaluate_ChainedGetTypeAssembly_ThrowsReflectionNotAllowedException()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "a", "some value" } },
            new Dictionary<string, object?>());

        Assert.Throws<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("mv[\"a\"].GetType().Assembly", context));
    }

    [Test]
    public void Evaluate_TypeofDoubleGetMethods_ThrowsReflectionNotAllowedException()
    {
        // The exact example DynamicExpresso's own documentation cites as blocked.
        var context = new ExpressionContext();

        Assert.Throws<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("typeof(double).GetMethods()", context));
    }

    [Test]
    public void Evaluate_TypeofDoubleAssembly_ThrowsReflectionNotAllowedException()
    {
        var context = new ExpressionContext();

        Assert.Throws<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("typeof(double).Assembly", context));
    }

    [Test]
    public void Evaluate_ChainedGetTypeInvokeMember_ThrowsReflectionNotAllowedException()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "a", "some value" } },
            new Dictionary<string, object?>());

        Assert.Throws<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("mv[\"a\"].GetType().GetMethod(\"ToString\")", context));
    }

    /// <summary>
    /// A bare <c>GetType()</c> call is NOT blocked: the visitor only rejects a method call or member access
    /// whose static receiver type is already <see cref="Type"/> or <see cref="System.Reflection.MemberInfo"/>
    /// (i.e. chained reflection). <c>mv["a"].GetType()</c> receives on <c>object</c>, so it parses and
    /// evaluates, returning a <see cref="Type"/> instance. This is documented, not hidden: see
    /// EXPRESSION_SECURITY.md. It does not itself grant further reflection; any attempt to chain a further
    /// call or member access onto the result within the same expression is blocked (see the tests above).
    /// </summary>
    [Test]
    public void Evaluate_BareGetType_ParsesAndReturnsTypeInstance()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "a", "some value" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("mv[\"a\"].GetType()", context);

        Assert.That(result, Is.EqualTo(typeof(string)));
    }

    #endregion

    #region Types Outside the Default Known-Type Set

    [Test]
    public void Evaluate_UnregisteredSystemNamespaceIdentifier_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        Assert.Throws<UnknownIdentifierException>(() =>
            _evaluator.Evaluate("System.Diagnostics.Process.Start(\"cmd\")", context));
    }

    [Test]
    public void Evaluate_UnregisteredFileType_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        Assert.Throws<UnknownIdentifierException>(() =>
            _evaluator.Evaluate("new System.IO.FileStream(\"x\", 0)", context));
    }

    [Test]
    public void Evaluate_UnregisteredEnvironmentType_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        Assert.Throws<UnknownIdentifierException>(() =>
            _evaluator.Evaluate("Environment.GetEnvironmentVariable(\"PATH\")", context));
    }

    [Test]
    public void Evaluate_UnregisteredActivatorType_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        Assert.Throws<UnknownIdentifierException>(() =>
            _evaluator.Evaluate("Activator.CreateInstance(typeof(object))", context));
    }

    [Test]
    public void Evaluate_UnregisteredAppDomainType_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        Assert.Throws<UnknownIdentifierException>(() =>
            _evaluator.Evaluate("AppDomain.CurrentDomain", context));
    }

    #endregion

    #region No Settable Surface for Assignment Operators

    /// <summary>
    /// DynamicExpresso's default <c>Interpreter()</c> leaves assignment operators enabled
    /// (<c>AssignmentOperators.All</c>); JIM does not disable them (see EXPRESSION_SECURITY.md for the
    /// rationale). This is not a functional gap because <see cref="AttributeAccessor"/>'s indexer is
    /// get-only: there is no settable member for an assignment expression to target.
    /// </summary>
    [Test]
    public void Evaluate_AssignToMvIndexer_ThrowsParseException()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "a", "original" } },
            new Dictionary<string, object?>());

        Assert.Throws<ParseException>(() =>
            _evaluator.Evaluate("mv[\"a\"] = \"changed\"", context));
    }

    #endregion

    #region Validate() Rejects Every Escape Attempt Above

    [TestCase("mv[\"a\"].GetType().GetMethods()")]
    [TestCase("mv[\"a\"].GetType().Assembly")]
    [TestCase("typeof(double).GetMethods()")]
    [TestCase("typeof(double).Assembly")]
    [TestCase("System.Diagnostics.Process.Start(\"cmd\")")]
    [TestCase("new System.IO.FileStream(\"x\", 0)")]
    [TestCase("Environment.GetEnvironmentVariable(\"PATH\")")]
    [TestCase("Activator.CreateInstance(typeof(object))")]
    [TestCase("AppDomain.CurrentDomain")]
    [TestCase("mv[\"a\"] = \"changed\"")]
    public void Validate_RejectsEveryEscapeAttempt(string expression)
    {
        var result = _evaluator.Validate(expression);

        Assert.That(result.IsValid, Is.False, $"Expected '{expression}' to fail validation.");
    }

    #endregion

    #region Representative Hostile-Looking-But-Illegitimate Inputs

    [Test]
    public void Evaluate_UnbalancedParentheses_ThrowsParseException()
    {
        var context = new ExpressionContext();

        Assert.Throws<ParseException>(() => _evaluator.Evaluate("Upper(mv[\"a\"]", context));
    }

    [Test]
    public void Evaluate_SqlInjectionLookingString_TreatedAsInertLiteral()
    {
        // Expressions have no SQL/shell surface at all; a SQL-injection-shaped string is just a string
        // literal here. This proves the point rather than assuming it.
        var context = new ExpressionContext();

        var result = _evaluator.Evaluate("\"'; DROP TABLE Users; --\"", context);

        Assert.That(result, Is.EqualTo("'; DROP TABLE Users; --"));
    }

    [Test]
    public void Evaluate_PathTraversalLookingString_TreatedAsInertLiteral()
    {
        var context = new ExpressionContext();

        var result = _evaluator.Evaluate("\"../../../../etc/passwd\"", context);

        Assert.That(result, Is.EqualTo("../../../../etc/passwd"));
    }

    [Test]
    public void Evaluate_ScriptTagLookingString_TreatedAsInertLiteral()
    {
        var context = new ExpressionContext();

        var result = _evaluator.Evaluate("\"<script>alert(1)</script>\"", context);

        Assert.That(result, Is.EqualTo("<script>alert(1)</script>"));
    }

    [Test]
    public void Evaluate_DeeplyNestedIIF_EvaluatesWithoutError()
    {
        // A legitimate-shaped but deeply nested conditional. Not a resource-exhaustion vector: this is
        // ordinary nested function calls, bounded by the MaxExpressionLength ceiling like any other
        // expression, not unbounded recursion or a loop construct (DynamicExpresso has no loop syntax).
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "n", "50" } },
            new Dictionary<string, object?>());

        var expression = string.Concat(Enumerable.Repeat("IIF(true, ", 50)) + "mv[\"n\"]" + string.Concat(Enumerable.Repeat(", \"x\")", 50));

        Assert.DoesNotThrow(() => _evaluator.Evaluate(expression, context));
    }

    [Test]
    public void Evaluate_EnumerableRangeCallsAreReachable_ButBounded()
    {
        // System.Linq.Enumerable is one of DynamicExpresso's default CommonTypes (see
        // EXPRESSION_SECURITY.md). It grants no filesystem/process/network access, only in-memory
        // sequence generation. Confirm it is reachable (so the doc's claim is accurate) and that a
        // moderately large range still evaluates promptly rather than hanging.
        var context = new ExpressionContext();

        var result = _evaluator.Evaluate("Enumerable.Range(0, 1000).Count()", context);

        Assert.That(result, Is.EqualTo(1000));
    }

    #endregion
}
