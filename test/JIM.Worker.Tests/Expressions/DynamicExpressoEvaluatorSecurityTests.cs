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

    /// <summary>
    /// Asserts the attempt fails as the one expression error every consumer reports (#2153, N5), and that the guard
    /// underneath it is the one this review relies on: the evaluator wraps the failure, it does not replace it.
    /// </summary>
    private static void AssertExpressionError<TGuard>(Action attempt) where TGuard : Exception
    {
        var error = Assert.Throws<ExpressionEvaluationException>(attempt);

        Assert.That(error!.InnerException, Is.InstanceOf<TGuard>());
    }

    #region Chained Reflection Escape Attempts

    [Test]
    public void Evaluate_ChainedGetTypeGetMethods_ThrowsReflectionNotAllowedException()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "a", "some value" } },
            new Dictionary<string, object?>());

        AssertExpressionError<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("mv[\"a\"].GetType().GetMethods()", context));
    }

    [Test]
    public void Evaluate_ChainedGetTypeAssembly_ThrowsReflectionNotAllowedException()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "a", "some value" } },
            new Dictionary<string, object?>());

        AssertExpressionError<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("mv[\"a\"].GetType().Assembly", context));
    }

    [Test]
    public void Evaluate_TypeofDoubleGetMethods_ThrowsReflectionNotAllowedException()
    {
        // The exact example DynamicExpresso's own documentation cites as blocked.
        var context = new ExpressionContext();

        AssertExpressionError<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("typeof(double).GetMethods()", context));
    }

    [Test]
    public void Evaluate_TypeofDoubleAssembly_ThrowsReflectionNotAllowedException()
    {
        var context = new ExpressionContext();

        AssertExpressionError<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("typeof(double).Assembly", context));
    }

    [Test]
    public void Evaluate_ChainedGetTypeInvokeMember_ThrowsReflectionNotAllowedException()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "a", "some value" } },
            new Dictionary<string, object?>());

        AssertExpressionError<ReflectionNotAllowedException>(() =>
            _evaluator.Evaluate("mv[\"a\"].GetType().GetMethod(\"ToString\")", context));
    }

    /// <summary>
    /// A bare <c>GetType()</c> call is refused when the expression is parsed (#2053), closing the finding this review
    /// once left open: no expression can hold a <see cref="Type"/>, so nothing can be chained onto one however it is
    /// re-typed (see EXPRESSION_SECURITY.md, "Late binding").
    /// </summary>
    [Test]
    public void Evaluate_BareGetType_IsRefused()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "a", "some value" } },
            new Dictionary<string, object?>());

        AssertExpressionError<ReflectionNotAllowedException>(() => _evaluator.Evaluate("mv[\"a\"].GetType()", context));
    }

    #endregion

    #region Types Outside the Default Known-Type Set

    [Test]
    public void Evaluate_UnregisteredSystemNamespaceIdentifier_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        AssertExpressionError<UnknownIdentifierException>(() =>
            _evaluator.Evaluate("System.Diagnostics.Process.Start(\"cmd\")", context));
    }

    [Test]
    public void Evaluate_UnregisteredFileType_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        AssertExpressionError<UnknownIdentifierException>(() =>
            _evaluator.Evaluate("new System.IO.FileStream(\"x\", 0)", context));
    }

    [Test]
    public void Evaluate_UnregisteredEnvironmentType_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        AssertExpressionError<UnknownIdentifierException>(() =>
            _evaluator.Evaluate("Environment.GetEnvironmentVariable(\"PATH\")", context));
    }

    [Test]
    public void Evaluate_UnregisteredActivatorType_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        AssertExpressionError<UnknownIdentifierException>(() =>
            _evaluator.Evaluate("Activator.CreateInstance(typeof(object))", context));
    }

    [Test]
    public void Evaluate_UnregisteredAppDomainType_ThrowsUnknownIdentifierException()
    {
        var context = new ExpressionContext();

        AssertExpressionError<UnknownIdentifierException>(() =>
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

        AssertExpressionError<ParseException>(() =>
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

        AssertExpressionError<ParseException>(() => _evaluator.Evaluate("Upper(mv[\"a\"]", context));
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

        Assert.That(() => _evaluator.Evaluate(expression, context), Throws.Nothing);
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

    #region Late Binding (#2053)

    // Expressions are late-bound so a transform's untyped result still joins and compares as text. A late-bound value
    // can reach its runtime type's public members, so these prove the reflection guard still holds when the chain to
    // System.Type runs through a late-bound result first, and through the several-values type every multi-valued
    // attribute now reads as. See engineering/EXPRESSION_SECURITY.md, "Late binding".

    [TestCase("cs[\"a\"].Substring(0).GetType().Assembly")]
    [TestCase("cs[\"a\"].Substring(0).GetType().GetMethods()")]
    [TestCase("cs[\"a\"].Substring(0).GetType().GetMethod(\"Clone\").Invoke(cs[\"a\"], null)")]
    [TestCase("Lower(cs[\"a\"]).GetType().Assembly")]
    [TestCase("cs[\"many\"].GetType().Assembly")]
    [TestCase("cs[\"many\"][0].GetType().GetMethods()")]
    [TestCase("Split(cs[\"a\"], \",\").GetType().Assembly")]
    [TestCase("cs[\"bytes\"].GetValue(0).GetType().Assembly")]
    public void Evaluate_ReflectionThroughALateBoundValue_ThrowsReflectionNotAllowedException(string expression)
    {
        var context = new ExpressionContext(connectedSystemAttributes: new Dictionary<string, object?>
        {
            { "a", "some value" },
            { "many", new List<object?> { "x", "y" } },
            { "bytes", new byte[] { 1 } }
        });

        AssertExpressionError<ReflectionNotAllowedException>(() => _evaluator.Evaluate(expression, context));
    }

    // A Type re-typed as object reaches the runtime binder rather than the parse-time reflection guard: every route by
    // which an expression can hand on a value it was given (IIF, Coalesce, a cast, the several-values functions) must
    // still be refused, without ever returning a Type, Assembly, MethodInfo or delegate.
    private static readonly string[] ReflectionThroughAnUntypedPassThrough =
    [
        "IIF(true, cs[\"a\"].GetType(), null).Assembly",
        "Coalesce(cs[\"a\"].GetType(), null).Assembly",
        "((object)cs[\"a\"].GetType()).Assembly",
        "((object)typeof(string)).Assembly",
        "IIF(true, (object)typeof(string), null).GetMethods()",
        "IIF(true, cs[\"a\"].GetType(), null).Assembly.GetType(\"System.IO.File\")",
        "IIF(true, cs[\"a\"].GetType(), null).Assembly.GetType(\"System.IO.File\").GetMethod(\"Exists\").Invoke(null, null)",
        "Min(cs[\"a\"].GetType()).Assembly",
        "Max(cs[\"a\"].GetType()).Assembly",
        "Where(cs[\"a\"].GetType(), \"equals\", \"x\").Assembly",
        "Coalesce(null, cs[\"a\"].GetType())",
        "IIF(true, cs[\"a\"].GetType(), null)",
        "cs[\"a\"].GetType()",
        "typeof(string)",
        "Split(\"a\", \",\").GetType()",
        "Coalesce(cs[\"a\"].GetType().Name, null)",
    ];

    [TestCaseSource(nameof(ReflectionThroughAnUntypedPassThrough))]
    public void Evaluate_ReflectionThroughAnUntypedPassThrough_IsAnExpressionErrorReturningNoReflectionObject(string expression)
    {
        var context = new ExpressionContext(connectedSystemAttributes: new Dictionary<string, object?> { { "a", "some value" } });

        object? result = null;
        Assert.Throws<ExpressionEvaluationException>(() => result = _evaluator.Evaluate(expression, context));
        Assert.That(result, Is.Not.InstanceOf<Type>().And.Not.InstanceOf<System.Reflection.MemberInfo>()
            .And.Not.InstanceOf<System.Reflection.Assembly>().And.Not.InstanceOf<Delegate>());
    }

    [TestCaseSource(nameof(ReflectionThroughAnUntypedPassThrough))]
    public void Validate_ReflectionThroughAnUntypedPassThrough_IsRejected(string expression)
    {
        Assert.That(_evaluator.Validate(expression).IsValid, Is.False);
    }

    [TestCase("cs[\"a\"].Substring(0).GetType().Assembly")]
    [TestCase("Lower(cs[\"a\"]).GetType().GetMethods()")]
    public void Validate_ReflectionThroughALateBoundValue_IsRejected(string expression)
    {
        Assert.That(_evaluator.Validate(expression).IsValid, Is.False);
    }

    [Test]
    public void Evaluate_AMemberTheValueDoesNotHave_IsAnExpressionError()
    {
        // Late binding resolves members while the expression runs, so a misspelt one fails then rather than at parse
        // time; it is still the one expression error, never a JIM fault.
        var context = new ExpressionContext(connectedSystemAttributes: new Dictionary<string, object?> { { "a", "abc" } });

        Assert.Throws<ExpressionEvaluationException>(() => _evaluator.Evaluate("cs[\"a\"].Lenght", context));
    }

    [Test]
    public void Evaluate_ChangingTheBytesOfOneOfSeveralBinaryValues_LeavesTheStoredValueAlone()
    {
        var stored = new byte[] { 1 };
        var context = new ExpressionContext(connectedSystemAttributes: new Dictionary<string, object?>
        {
            { "certs", new List<object?> { stored, new byte[] { 2 } } }
        });

        try { _evaluator.Evaluate("cs[\"certs\"][0][0] = (byte)9", context); } catch (ExpressionEvaluationException) { /* refused is as good as copied */ }

        Assert.That(stored[0], Is.EqualTo(1));
    }

    #endregion
}
