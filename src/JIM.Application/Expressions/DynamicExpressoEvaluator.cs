// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DynamicExpresso;
using DynamicExpresso.Exceptions;
using Microsoft.CSharp.RuntimeBinder;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Expressions;

/// <summary>
/// Expression evaluator implementation using DynamicExpresso.
/// Provides C#-like expression syntax with built-in functions for identity management.
/// </summary>
public class DynamicExpressoEvaluator : IExpressionEvaluator
{
    private readonly Interpreter _interpreter;

    /// <summary>
    /// Maximum permitted expression source length, in characters. This is a sanity bound against accidental
    /// paste bombs (an administrator pasting the wrong clipboard contents into an expression field); it sits
    /// far above the length of any legitimate Attribute Flow, Object Matching Rule or Example Data Template
    /// expression. See <c>engineering/EXPRESSION_SECURITY.md</c> for the full rationale.
    /// </summary>
    public const int MaxExpressionLength = 10_000;

    /// <summary>
    /// Maximum number of entries the static compiled-expression cache is allowed to hold before it is cleared.
    /// Ad-hoc expressions evaluated via <see cref="Test"/> (the admin UI's expression tester) would otherwise
    /// accumulate in the cache forever. Recompilation is cheap, so clearing and starting again is preferred
    /// over an eviction policy. See <c>engineering/EXPRESSION_SECURITY.md</c> for the full rationale.
    /// </summary>
    public const int MaxCompiledExpressionCacheSize = 1_000;

    // Static cache shared across all instances - expressions are deterministic so cache can be shared
    // This ensures expression compilation is only done once per expression string across all sync operations
    private static readonly ConcurrentDictionary<string, Lambda> _compiledExpressions = new();

    /// <summary>
    /// The evaluating context's <see cref="ExpressionContext.NowUtc"/>, for <c>Now()</c> and <c>Today()</c>. Compiled
    /// Expressions are shared and their functions cannot see the context, so <see cref="Evaluate"/> holds it here for
    /// the duration of the call, on the thread running it, and restores what was there before.
    /// </summary>
    [ThreadStatic]
    private static DateTime? _contextNowUtc;

    // Static cache metrics for diagnostics (shared across instances)
    private static long _cacheHits;
    private static long _cacheMisses;

    /// <summary>
    /// Test-only observability hook exposing the current compiled-expression cache size, so the cache bound
    /// guardrail can be verified without depending on log capture or timing. Internal via the
    /// InternalsVisibleTo(JIM.Worker.Tests) grant on this project.
    /// </summary>
    internal static int CompiledExpressionCacheSize => _compiledExpressions.Count;

    // Threshold for logging slow expressions (in milliseconds)
    private const int SlowExpressionThresholdMs = 10;

    // Word list for passphrase generation (common, easy-to-type words)
    private static readonly string[] PassphraseWords =
    [
        "apple", "banana", "cherry", "delta", "eagle", "forest", "garden", "harbor",
        "island", "jungle", "kitten", "lemon", "marble", "nectar", "orange", "planet",
        "quartz", "rabbit", "silver", "temple", "umbrella", "violet", "winter", "yellow",
        "anchor", "bridge", "castle", "dragon", "ember", "falcon", "glacier", "hammer",
        "ivory", "jasper", "knight", "lantern", "meadow", "noble", "ocean", "phoenix",
        "quest", "raven", "shadow", "thunder", "velvet", "willow", "zenith", "aurora",
        "beacon", "crystal", "dancer", "eclipse", "flame", "granite", "horizon", "indigo"
    ];

    public DynamicExpressoEvaluator()
    {
        _interpreter = CreateInterpreter();
        RegisterBuiltInFunctions();
    }

    /// <summary>
    /// The interpreter every expression is parsed by: DynamicExpresso's defaults, late-bound (#2053). An accessor reads
    /// one value or several, so a transform's result is untyped; late binding is what lets <c>Upper(a) + Upper(b)</c>
    /// parse and <c>Lower(x) == "abc"</c> compare text rather than references. Reflection is refused in depth (see
    /// <c>engineering/EXPRESSION_SECURITY.md</c>, "Late binding"): at parse time by DynamicExpresso's own guard and by
    /// <see cref="ReflectionRefusingVisitor"/>, which refuses any reflection-typed part of an expression, <c>GetType()</c>
    /// and <c>typeof</c> included; and at run time by DynamicExpresso's late binder, by every function that hands a
    /// value on (<see cref="ExpressionValueSets.RefuseReflection"/>) and by <see cref="Evaluate"/> refusing a reflection
    /// result.
    /// </summary>
    private static Interpreter CreateInterpreter()
    {
        var interpreter = new Interpreter(InterpreterOptions.Default | InterpreterOptions.LateBindObject);
        interpreter.Visitors.Add(new ReflectionRefusingVisitor());
        return interpreter;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every evaluation runs under JIM's expression culture (<see cref="ExpressionText.Culture"/>), set here and
    /// restored afterwards, so what the language writes as text (a date or a number joined to text with <c>+</c>)
    /// is the same on every server whatever its <c>LANG</c> (#2153, N1). Every failure the Expression can cause is
    /// thrown as one <see cref="ExpressionEvaluationException"/> (N5).
    /// </remarks>
    public object? Evaluate(string expression, ExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);

        if (expression.Length > MaxExpressionLength)
        {
            throw new ExpressionEvaluationException(
                $"Expression exceeds the maximum permitted length of {MaxExpressionLength:N0} characters (actual length: {expression.Length:N0}).");
        }

        var hostCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = ExpressionText.Culture;
        var hostClock = _contextNowUtc;
        _contextNowUtc = context.NowUtc;
        var sw = Stopwatch.StartNew();
        try
        {
            var lambda = GetOrCompileExpression(expression);

            return ExpressionValueSets.RefuseReflection(lambda.Invoke(
                new Parameter("mv", context.Mv),
                new Parameter("cs", context.Cs)));
        }
        // The failures an Expression can cause, from parsing it to running it, each made the one expression error.
        // Anything else (an out-of-memory condition, a JIM fault) propagates unchanged, as does an
        // ExpressionEvaluationException a function raised itself.
        catch (DynamicExpressoException ex) { throw ToExpressionError(ex); }
        catch (ArgumentException ex) { throw ToExpressionError(ex); }
        catch (FormatException ex) { throw ToExpressionError(ex); }
        catch (ArithmeticException ex) { throw ToExpressionError(ex); }
        catch (InvalidOperationException ex) { throw ToExpressionError(ex); }
        catch (InvalidCastException ex) { throw ToExpressionError(ex); }
        catch (KeyNotFoundException ex) { throw ToExpressionError(ex); }
        catch (NullReferenceException ex) { throw ToExpressionError(ex); }
        catch (IndexOutOfRangeException ex) { throw ToExpressionError(ex); }
        // Late binding resolves a member or operator on a value while the expression runs: one the value does not have
        // (a misspelt member, an operator its type lacks) is the expression's fault, reported like a parse error.
        catch (RuntimeBinderException ex) { throw ToExpressionError(ex); }
        finally
        {
            CultureInfo.CurrentCulture = hostCulture;
            _contextNowUtc = hostClock;
            sw.Stop();
            if (sw.ElapsedMilliseconds > SlowExpressionThresholdMs)
            {
                Log.Warning("Slow expression evaluation: '{Expression}' took {ElapsedMs}ms",
                    LogSanitiser.Sanitise(expression.Length > 50 ? expression[..50] + "..." : expression),
                    sw.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>
    /// Wraps a failure the Expression caused as the one expression error, keeping its message so the administrator
    /// reads what went wrong exactly as before.
    /// </summary>
    private static ExpressionEvaluationException ToExpressionError(Exception exception) =>
        new(exception.Message, exception);

    /// <summary>
    /// Gets the current expression cache metrics.
    /// </summary>
    /// <returns>A tuple containing (CacheHits, CacheMisses).</returns>
    public (long CacheHits, long CacheMisses) GetCacheMetrics() => (_cacheHits, _cacheMisses);

    /// <inheritdoc/>
    public ExpressionValidationResult Validate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return ExpressionValidationResult.Failure("Expression cannot be empty.");
        }

        if (expression.Length > MaxExpressionLength)
        {
            return ExpressionValidationResult.Failure(
                $"Expression exceeds the maximum permitted length of {MaxExpressionLength:N0} characters (actual length: {expression.Length:N0}).");
        }

        try
        {
            // Create a test interpreter with mock parameters
            var testInterpreter = CreateInterpreter();
            RegisterBuiltInFunctions(testInterpreter);

            testInterpreter.Parse(expression,
                new Parameter("mv", typeof(AttributeAccessor)),
                new Parameter("cs", typeof(AttributeAccessor)));

            return ExpressionValidationResult.Success();
        }
        catch (ParseException ex)
        {
            return ExpressionValidationResult.Failure(ex.Message, ex.Position);
        }
        catch (Exception ex)
        {
            return ExpressionValidationResult.Failure(ex.Message);
        }
    }

    /// <inheritdoc/>
    public ExpressionTestResult Test(string expression, ExpressionContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return ExpressionTestResult.Failure("Expression cannot be empty.");
        }

        if (expression.Length > MaxExpressionLength)
        {
            return ExpressionTestResult.Failure(
                $"Expression exceeds the maximum permitted length of {MaxExpressionLength:N0} characters (actual length: {expression.Length:N0}).");
        }

        try
        {
            var result = Evaluate(expression, context);
            return ExpressionTestResult.Success(result);
        }
        catch (ExpressionEvaluationException ex)
        {
            return ExpressionTestResult.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Evaluate lets a failure outside the expression errors propagate (an expression can still raise one, such
            // as "x".PadLeft(2000000000), which exceeds the maximum string length). Test has a result to carry any
            // failure in, and the portal tester, the REST endpoint, Example Data and the generated-value previews rely
            // on it never throwing.
            return ExpressionTestResult.Failure(ex.Message);
        }
    }

    /// <summary>The instant <c>Now()</c> reads: the evaluating context's, or the real time.</summary>
    private static DateTime CurrentUtc() => _contextNowUtc ?? DateTime.UtcNow;

    private Lambda GetOrCompileExpression(string expression)
    {
        // Check for cache hit first
        if (_compiledExpressions.TryGetValue(expression, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        // Cache miss - compile and add
        Interlocked.Increment(ref _cacheMisses);
        Log.Debug("Expression cache miss - compiling: '{Expression}'",
            expression.Length > 50 ? expression[..50] + "..." : expression);

        // Bound the cache: ad-hoc expressions evaluated via Test (the admin UI's expression tester) would
        // otherwise accumulate forever. Recompilation is cheap, so on reaching the bound we clear and start
        // again rather than implementing an eviction policy. See engineering/EXPRESSION_SECURITY.md.
        if (_compiledExpressions.Count >= MaxCompiledExpressionCacheSize)
        {
            _compiledExpressions.Clear();
            Log.Warning(
                "Compiled expression cache reached its {MaxCacheSize}-entry bound and was cleared; expressions will be recompiled on next use.",
                MaxCompiledExpressionCacheSize);
        }

        return _compiledExpressions.GetOrAdd(expression, expr =>
        {
            return _interpreter.Parse(expr,
                new Parameter("mv", typeof(AttributeAccessor)),
                new Parameter("cs", typeof(AttributeAccessor)));
        });
    }

    private void RegisterBuiltInFunctions(Interpreter? interpreter = null)
    {
        var target = interpreter ?? _interpreter;

        // Every function accepts object? (an accessor reads null, one value, or several values) and follows the
        // several-values rules in ExpressionValueSets (#2053): a transform applies to each value, a comparison is true
        // when any value satisfies it, and a one-value position refuses several values with an error naming the
        // attribute and the count.

        // Text transforms: each value in, each result out, in the same order.
        target.SetFunction("Trim", (Func<object?, object?>)(o => ExpressionValueSets.Map(o, v => AsString(v)?.Trim())));
        target.SetFunction("Upper", (Func<object?, object?>)(o => ExpressionValueSets.Map(o, v => AsString(v)?.ToUpperInvariant())));
        target.SetFunction("Lower", (Func<object?, object?>)(o => ExpressionValueSets.Map(o, v => AsString(v)?.ToLowerInvariant())));
        target.SetFunction("Capitalise", (Func<object?, object?>)(o => ExpressionValueSets.Map(o, v => Capitalise(AsString(v)))));
        target.SetFunction("Left", (Func<object?, int, object?>)((o, count) => ExpressionValueSets.Map(o, v => Left(AsString(v), count))));
        target.SetFunction("Right", (Func<object?, int, object?>)((o, count) => ExpressionValueSets.Map(o, v => Right(AsString(v), count))));
        target.SetFunction("Substring", (Func<object?, int, int, object?>)((o, start, length) => ExpressionValueSets.Map(o, v => SafeSubstring(AsString(v), start, length))));
        target.SetFunction("Replace", (Func<object?, object?, object?, object?>)((o, oldVal, newVal) =>
        {
            var oldText = AsString(oldVal) ?? "";
            var newText = AsString(newVal) ?? "";
            return ExpressionValueSets.Map(o, v => AsString(v)?.Replace(oldText, newText));
        }));

        // Comparisons: true when any value satisfies them, so ! means "no value does".
        target.SetFunction("StartsWith", (Func<object?, object?, bool>)((o, prefix) =>
        {
            var prefixText = AsString(prefix) ?? "";
            return ExpressionValueSets.Any(o, v => AsString(v)?.StartsWith(prefixText, StringComparison.Ordinal) ?? false);
        }));
        target.SetFunction("EndsWith", (Func<object?, object?, bool>)((o, suffix) =>
        {
            var suffixText = AsString(suffix) ?? "";
            return ExpressionValueSets.Any(o, v => AsString(v)?.EndsWith(suffixText, StringComparison.Ordinal) ?? false);
        }));
        target.SetFunction("Contains", (Func<object?, object?, bool>)((o, value) =>
        {
            var valueText = AsString(value) ?? "";
            return ExpressionValueSets.Any(o, v => AsString(v)?.Contains(valueText) ?? false);
        }));

        // One-value functions.
        target.SetFunction("Length", (Func<object?, int>)(o => AsString(ExpressionValueSets.One(o))?.Length ?? 0));

        // Presence: JIM's one definition of no value (null, empty text, or a list with no values). A Binary value
        // (which has no text form) is a value unless it holds no bytes, rather than an error.
        target.SetFunction("IsNullOrEmpty", (Func<object?, bool>)ExpressionValueSets.HasNoValue);
        target.SetFunction("IsNullOrWhitespace", (Func<object?, bool>)(o =>
            ExpressionValueSets.HasNoValue(o) || ExpressionValueSets.Values(o).All(v => v is not byte[] && string.IsNullOrWhiteSpace(AsString(v)))));

        // Whole-set functions. Split's result is several values in the order it found them, so Join gives back what
        // Split was given.
        target.SetFunction("Split", (Func<object?, object?, ExpressionValues>)((o, delimiter) => new ExpressionValues(Split(AsString(ExpressionValueSets.One(o)), AsString(delimiter)), null)));
        target.SetFunction("Join", (Func<object?, object?, string?>)((collection, delimiter) => Join(collection, AsString(delimiter))));
        target.SetFunction("Count", (Func<object?, int>)(o => ExpressionValueSets.Values(o).Count));
        target.SetFunction("Min", (Func<object?, object?>)(o => ExpressionValueSets.Extreme(o, largest: false, "Min")));
        target.SetFunction("Max", (Func<object?, object?>)(o => ExpressionValueSets.Extreme(o, largest: true, "Max")));
        target.SetFunction("Where", (Func<object?, object?, object?, object?>)((values, op, value) => ExpressionWhere.Filter(values, op, value, ignoreCase: false)));
        target.SetFunction("Where", (Func<object?, object?, object?, bool, object?>)ExpressionWhere.Filter);

        // Conditional functions
        target.SetFunction("Coalesce", (Func<object?, object?, object?>)((a, b) =>
            ExpressionValueSets.RefuseReflection(ExpressionValueSets.HasNoValue(a) ? b : a)));
        target.SetFunction("IIF", (Func<bool, object?, object?, object?>)((condition, trueVal, falseVal) =>
            ExpressionValueSets.RefuseReflection(condition ? trueVal : falseVal)));

        // Value equality, true when any value of one side equals any value of the other. Expressions are late-bound, so
        // == compares values too; Eq remains the one that reads text against a typed value as that type.
        target.SetFunction("Eq", (Func<object?, object?, bool>)((a, b) =>
            ExpressionResultValues.IsCollection(a) || ExpressionResultValues.IsCollection(b)
                ? ExpressionValueSets.Any(a, x => ExpressionValueSets.Any(b, y => ValueEquals(x, y)))
                : ValueEquals(a, b)));

        // Date functions. Now and Today (like RandomPassword and RandomPassphrase below) return a different value on
        // each evaluation; NonRepeatableFunctionDetector lists them, so keep it in step when adding another.
        target.SetFunction("Now", (Func<DateTime>)(() => CurrentUtc()));
        target.SetFunction("Today", (Func<DateTime>)(() => CurrentUtc().Date));
        // FormatDate and ParseDate are counterparts: one writes a date as text in a given format, the other reads text
        // in a given format as a date. Every other reading of text as a date uses the ISO 8601 rule (ExpressionDates).
        target.SetFunction("FormatDate", (Func<object?, string, object?>)((o, format) => ExpressionValueSets.Map(o, v => FormatDate(v, format))));
        target.SetFunction("ParseDate", (Func<object?, string, object?>)((o, format) => ExpressionValueSets.Map(o, v => ParseDate(v, format))));
        target.SetFunction("ToFileTime", (Func<object?, long?>)(o => ToFileTime(ExpressionValueSets.One(o))));
        target.SetFunction("FromFileTime", (Func<object?, object?>)(o => ExpressionValueSets.Map(o, v => FromFileTime(v))));

        // Conversion functions
        target.SetFunction("ToString", (Func<object?, object?>)(o => ExpressionValueSets.Map(o, AsString)));
        target.SetFunction("ToInt", (Func<object?, int>)(o => int.TryParse(AsString(ExpressionValueSets.One(o)), out var i) ? i : 0));
        // ToDecimal: exact for decimal/int/long inputs; strings parse with invariant culture and accept
        // exponent notation (e.g. "1.5E3" becomes 1500). Deliberately no double/float path: binary
        // floating point cannot represent most decimal fractions exactly.
        target.SetFunction("ToDecimal", (Func<object?, decimal>)(input =>
        {
            var o = ExpressionValueSets.One(input);
            return o is decimal d ? d :
                o is int i ? i :
                o is long l ? l :
                DecimalAttributeValue.TryParse(AsString(o), out var parsed) ? parsed : 0m;
        }));

        // DN Helper functions
        target.SetFunction("EscapeDN", (Func<object?, object?>)(o => ExpressionValueSets.Map(o, v => EscapeDN(AsString(v)))));

        // Password generation functions
        target.SetFunction("RandomPassword", (Func<int, bool, string>)RandomPassword);
        target.SetFunction("RandomPassphrase", (Func<int, string, string>)RandomPassphrase);

        // Bitwise functions for AD userAccountControl manipulation
        target.SetFunction("EnableUser", (Func<object?, int>)(o => ClearBit(AsInt(o), 2)));  // Clear ACCOUNTDISABLE (0x0002)
        target.SetFunction("DisableUser", (Func<object?, int>)(o => SetBit(AsInt(o), 2)));   // Set ACCOUNTDISABLE (0x0002)
        target.SetFunction("SetBit", (Func<object?, int, int>)((o, bit) => SetBit(AsInt(o), bit)));
        target.SetFunction("ClearBit", (Func<object?, int, int>)((o, bit) => ClearBit(AsInt(o), bit)));
        target.SetFunction("HasBit", (Func<object?, int, bool>)((o, bit) => HasBit(AsInt(o), bit)));
    }

    /// <summary>
    /// A function's input as text, in JIM's one text form (#2153, N1): a date as <c>2026-03-15 10:30:00Z</c>, a
    /// number with <c>.</c> as the decimal point. A Binary value has no text form, so using one as text is an
    /// expression error rather than the text <c>System.Byte[]</c>. Text is a one-value position (#2053): several values
    /// are the error naming the attribute and the count.
    /// </summary>
    private static string? AsString(object? value)
    {
        value = ExpressionValueSets.One(value);

        if (ExpressionText.TryFormat(value, out var text))
            return text;

        if (value is byte[])
            throw new ExpressionEvaluationException("A Binary value has no text form, so it cannot be used as text.");

        return value!.ToString();
    }

    /// <summary>
    /// Converts an object to int, handling null and various types. A one-value position (#2053).
    /// </summary>
    private static int AsInt(object? value)
    {
        value = ExpressionValueSets.One(value);
        if (value == null) return 0;
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is short s) return s;
        if (value is byte b) return b;
        if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        return 0;
    }

    #region String Functions

    private static string? Capitalise(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return s;

        var result = new StringBuilder(s.Length);
        var capitaliseNext = true;

        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == '\'')
            {
                result.Append(c);
                capitaliseNext = true;
            }
            else if (capitaliseNext)
            {
                result.Append(char.ToUpperInvariant(c));
                capitaliseNext = false;
            }
            else
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }

        return result.ToString();
    }

    private static string? Left(string? s, int count)
    {
        if (s == null) return null;
        if (count <= 0) return string.Empty;
        return s.Length <= count ? s : s[..count];
    }

    private static string? Right(string? s, int count)
    {
        if (s == null) return null;
        if (count <= 0) return string.Empty;
        return s.Length <= count ? s : s[^count..];
    }

    private static string? SafeSubstring(string? s, int start, int length)
    {
        if (s == null) return null;
        if (start < 0) start = 0;
        if (start >= s.Length) return string.Empty;
        if (length <= 0) return string.Empty;

        var actualLength = Math.Min(length, s.Length - start);
        return s.Substring(start, actualLength);
    }

    #endregion

    #region Array/Collection Functions

    /// <summary>
    /// Splits a string by a delimiter into an array of strings.
    /// This is useful for converting delimited single-valued attributes (e.g., "A|B|C")
    /// into multiple values for multi-valued metaverse attributes.
    /// Empty entries are removed automatically.
    /// </summary>
    /// <param name="value">The string to split.</param>
    /// <param name="delimiter">The delimiter to split on (e.g., "|", ",", ";").</param>
    /// <returns>An array of non-empty strings, or an empty array if value is null/empty.</returns>
    private static string[] Split(string? value, string? delimiter)
    {
        if (string.IsNullOrEmpty(value))
            return Array.Empty<string>();

        if (string.IsNullOrEmpty(delimiter))
            return new[] { value };

        return value.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Joins values into one delimited text value, in the order they are read (#2053): an attribute's values in their
    /// fixed order, or <c>Split</c>'s in the order it found them, so <c>Split</c> followed by <c>Join</c> gives back what it
    /// was given. Values with no text (empty text) are left out; a Binary value is an error, as it has no text form.
    /// </summary>
    /// <returns>The joined text, or null when there are no values.</returns>
    private static string? Join(object? collection, string? delimiter)
    {
        delimiter ??= ",";
        var items = ExpressionValueSets.Values(collection)
            .Select(AsString)
            .Where(text => !string.IsNullOrEmpty(text))
            .ToArray();
        return items.Length > 0 ? string.Join(delimiter, items) : null;
    }

    #endregion

    #region DN Helper Functions

    /// <summary>
    /// Escapes special characters in a Distinguished Name component.
    /// Special characters: , + " \ &lt; &gt; ; = and leading/trailing spaces, # at start.
    /// </summary>
    private static string? EscapeDN(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = new StringBuilder(value.Length * 2);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            // Escape special DN characters
            if (c == ',' || c == '+' || c == '"' || c == '\\' || c == '<' || c == '>' || c == ';' || c == '=')
            {
                result.Append('\\');
                result.Append(c);
            }
            // Escape leading space or #
            else if (i == 0 && (c == ' ' || c == '#'))
            {
                result.Append('\\');
                result.Append(c);
            }
            // Escape trailing space
            else if (i == value.Length - 1 && c == ' ')
            {
                result.Append('\\');
                result.Append(c);
            }
            // Escape newlines and carriage returns
            else if (c == '\r')
            {
                result.Append("\\0D");
            }
            else if (c == '\n')
            {
                result.Append("\\0A");
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    #endregion

    #region Password Generation Functions

    /// <summary>
    /// Generates a random password with the specified length.
    /// </summary>
    /// <param name="length">The desired password length (minimum 8).</param>
    /// <param name="extendedChars">If true, includes special characters (!@#$%^&amp;*). If false, alphanumeric only.</param>
    private static string RandomPassword(int length, bool extendedChars)
    {
        length = Math.Max(8, Math.Min(128, length)); // Clamp between 8 and 128

        const string lowercase = "abcdefghijklmnopqrstuvwxyz";
        const string uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string digits = "0123456789";
        const string special = "!@#$%^&*";

        var chars = lowercase + uppercase + digits;
        if (extendedChars)
        {
            chars += special;
        }

        var result = new StringBuilder(length);
        var bytes = new byte[length];

        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        // Ensure at least one of each required character type
        result.Append(lowercase[bytes[0] % lowercase.Length]);
        result.Append(uppercase[bytes[1] % uppercase.Length]);
        result.Append(digits[bytes[2] % digits.Length]);

        var startIndex = 3;
        if (extendedChars && length >= 4)
        {
            result.Append(special[bytes[3] % special.Length]);
            startIndex = 4;
        }

        // Fill the rest randomly
        for (var i = startIndex; i < length; i++)
        {
            result.Append(chars[bytes[i] % chars.Length]);
        }

        // Shuffle the result to avoid predictable positions
        return ShuffleString(result.ToString());
    }

    /// <summary>
    /// Generates a random passphrase with the specified number of words.
    /// </summary>
    /// <param name="wordCount">The number of words (minimum 3, maximum 10).</param>
    /// <param name="separator">The separator between words (default "-").</param>
    private static string RandomPassphrase(int wordCount, string separator)
    {
        wordCount = Math.Max(3, Math.Min(10, wordCount)); // Clamp between 3 and 10

        if (string.IsNullOrEmpty(separator))
        {
            separator = "-";
        }

        var bytes = new byte[wordCount];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        var words = new string[wordCount];
        for (var i = 0; i < wordCount; i++)
        {
            words[i] = PassphraseWords[bytes[i] % PassphraseWords.Length];
        }

        return string.Join(separator, words);
    }

    private static string ShuffleString(string input)
    {
        var array = input.ToCharArray();
        var bytes = new byte[array.Length];

        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        for (var i = array.Length - 1; i > 0; i--)
        {
            var j = bytes[i] % (i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }

        return new string(array);
    }

    #endregion

    #region DateTime Conversion Functions

    // These functions support Active Directory Large Integer (FILETIME) attributes:
    // - accountExpires: When the account expires (0 or Int64.MaxValue = never)
    // - pwdLastSet: When the password was last set (0 = must change at next logon)
    // - lastLogon: Last interactive logon time (not replicated between DCs)
    // - lastLogonTimestamp: Last logon time (replicated, but with 9-14 day lag)
    // - lockoutTime: When the account was locked out (0 = not locked)
    // - badPasswordTime: Last failed password attempt
    //
    // FILETIME format: 100-nanosecond intervals since January 1, 1601 UTC
    // Range: 0 to 9223372036854775807 (Int64.MaxValue)

    /// <summary>
    /// Converts a DateTime to Windows FILETIME (100-nanosecond intervals since January 1, 1601 UTC).
    /// This is the format used by AD attributes like accountExpires, pwdLastSet, lastLogon, etc.
    /// A value of 0 or 9223372036854775807 (Int64.MaxValue) means "never expires" in AD.
    /// </summary>
    private static long? ToFileTime(object? value)
    {
        if (value == null)
            return null;

        // A date (DateTime, or DateTimeOffset from PostgreSQL) or text, read by the one date rule (#2153, N2): text
        // only in ISO 8601, an Unspecified date taken as UTC. Unreadable text is no date, so no FILETIME.
        if (!ExpressionDates.IsDate(value) && value is not string)
        {
            throw new ArgumentException(
                $"ToFileTime cannot convert value of type '{value.GetType().Name}'. " +
                "Expected a date, or text in ISO 8601 such as 2026-03-15T10:30:00Z.");
        }

        if (!ExpressionDates.TryRead(value, out var utcDateTime))
            return null;

        // Handle PostgreSQL's -infinity (DateTime.MinValue) and +infinity (DateTime.MaxValue)
        // These represent "no date" or "never" and should return null
        if (utcDateTime == DateTime.MinValue || utcDateTime == DateTime.MaxValue)
            return null;

        // FILETIME can only represent dates from 1601-01-01 onwards
        // Return null for dates before this (shouldn't happen with valid data, but be safe)
        if (utcDateTime.Year < 1601)
            return null;

        return utcDateTime.ToFileTimeUtc();
    }

    /// <summary>
    /// Writes a date as text in <paramref name="format"/> (a .NET date format, read with the invariant culture). The
    /// value is a date, or text read by the one date rule (#2153, N3): ISO 8601 only, no offset meaning UTC. Null,
    /// empty or unreadable text gives null; a value of any other type is an expression error.
    /// </summary>
    private static string? FormatDate(object? value, string format)
    {
        if (value == null)
            return null;

        DateTime date;
        switch (value)
        {
            // A date is formatted as it is, exactly as before FormatDate accepted text.
            case DateTime dateTime:
                date = dateTime;
                break;
            case DateTimeOffset dateWithOffset:
                date = dateWithOffset.UtcDateTime;
                break;
            case string text:
                if (!ExpressionDates.TryReadText(text, out date))
                    return null;
                break;
            default:
                throw new ArgumentException(
                    $"FormatDate cannot format a value of type '{value.GetType().Name}'. " +
                    "Expected a date, or text in ISO 8601 such as 2026-03-15T10:30:00Z.");
        }

        return date.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads text in <paramref name="format"/> (a .NET custom date format, with the invariant culture) as a date: the
    /// counterpart of <see cref="FormatDate"/>, and the way to read a date in any form other than ISO 8601. The text
    /// is taken as UTC unless the format carries an offset (<c>zzz</c> or <c>K</c>); the result is the canonical
    /// UTC date, to the microsecond. A date passed in is returned as UTC.
    /// </summary>
    /// <returns>Null for no value or empty text.</returns>
    /// <exception cref="FormatException">Non-empty text that does not match the format: an expression error, so the
    /// attribute keeps its value rather than being cleared by a null.</exception>
    private static DateTime? ParseDate(object? value, string format)
    {
        switch (value)
        {
            case null:
            case "":
                return null;
            case DateTime dateTime:
                return DateTimeAttributeValue.ToCanonical(dateTime);
            case DateTimeOffset dateWithOffset:
                return DateTimeAttributeValue.ToCanonical(dateWithOffset.UtcDateTime);
            case string text:
                if (string.IsNullOrEmpty(format))
                    throw new ArgumentException("ParseDate needs the format the text is in, such as \"dd/MM/yyyy\".");

                // The message names the format, never the text: it reaches the worker's logs, which must not carry
                // source data such as a date of birth; the Run Profile Execution Item identifies the object.
                if (!DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    throw new FormatException($"ParseDate could not read the text as a date in the format '{format}'.");

                return DateTimeAttributeValue.ToCanonical(parsed.UtcDateTime);
            default:
                throw new ArgumentException(
                    $"ParseDate cannot read a value of type '{value.GetType().Name}'. Expected text, or a date.");
        }
    }

    /// <summary>
    /// Converts a Windows FILETIME (100-nanosecond intervals since January 1, 1601 UTC) to DateTime.
    /// This is the format used by AD attributes like accountExpires, pwdLastSet, lastLogon, etc.
    /// </summary>
    private static DateTime? FromFileTime(object? value)
    {
        if (value == null)
            return null;

        long? fileTime = value switch
        {
            long l => l,
            int i => i,
            string s => ParseLongOrNull(s),  // Handle string values (may return null for invalid numbers)
            _ => throw new ArgumentException(
                $"FromFileTime cannot convert value of type '{value.GetType().Name}'. " +
                "Expected long, int, or string.")
        };

        if (fileTime == null || fileTime.Value <= 0)
            return null;

        // Int64.MaxValue means "never expires" in AD - return null to indicate no expiry
        if (fileTime.Value == long.MaxValue)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(fileTime.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Invalid FILETIME value
            return null;
        }
    }

    /// <summary>
    /// Attempts to parse a string as a long, returning null if parsing fails.
    /// This allows graceful handling of empty strings or unparseable numbers.
    /// </summary>
    private static long? ParseLongOrNull(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        return long.TryParse(s, out var parsed) ? parsed : null;
    }

    #endregion

    #region Bitwise Functions

    /// <summary>
    /// Sets a bit in the value (value | bit).
    /// </summary>
    private static int SetBit(int value, int bit) => value | bit;

    /// <summary>
    /// Clears a bit in the value (value &amp; ~bit).
    /// </summary>
    private static int ClearBit(int value, int bit) => value & ~bit;

    /// <summary>
    /// Checks if a bit is set in the value ((value &amp; bit) == bit).
    /// </summary>
    private static bool HasBit(int value, int bit) => (value & bit) == bit;

    #endregion

    #region Equality Functions

    /// <summary>
    /// Performs value-based equality comparison between two objects.
    /// This is necessary because the == operator in expressions uses reference equality
    /// when comparing object? to string (since the indexer returns object?).
    /// </summary>
    /// <remarks>
    /// IMPORTANT: In C#, when you write `mv["attr"] == "value"`:
    /// - mv["attr"] returns object? (from the AttributeAccessor indexer)
    /// - "value" is a string literal
    /// - The == operator uses static type analysis, so it compares object? to string
    /// - For object comparisons, == uses reference equality, NOT value equality
    /// - This means two strings with the same content but different instances return false!
    ///
    /// Use Eq() for value-based comparisons: `Eq(mv["attr"], "value")`
    /// </remarks>
    private static bool ValueEquals(object? a, object? b)
    {
        // Handle null cases
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        // Dates compare by instant (#2153, N2), and text compared with a date is read by the one date rule: ISO 8601
        // only, so the host's own date text ("15/03/2026 10:30:00" on en-GB) is not equal on one server and unequal
        // on the next. Text that cannot be read as a date is not equal to one.
        if (ExpressionDates.IsDate(a) || ExpressionDates.IsDate(b))
        {
            return ExpressionDates.TryRead(a, out var dateA) && ExpressionDates.TryRead(b, out var dateB) &&
                   dateA.Ticks == dateB.Ticks;
        }

        // String comparison (most common case)
        if (a is string strA && b is string strB)
            return string.Equals(strA, strB, StringComparison.Ordinal);

        // If one is string and other is object, convert and compare
        if (a is string && b is not string)
            return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
        if (b is string && a is not string)
            return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);

        // Numeric comparisons - handle cross-type numeric equality
        if (IsNumeric(a) && IsNumeric(b))
        {
            try
            {
                var decA = Convert.ToDecimal(a);
                var decB = Convert.ToDecimal(b);
                return decA == decB;
            }
            catch
            {
                // Fall through to default Equals
            }
        }

        // Default to Equals method
        return a.Equals(b);
    }

    private static bool IsNumeric(object? obj)
    {
        return obj is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    #endregion
}
