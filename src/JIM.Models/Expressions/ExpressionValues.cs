// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;
using System.Globalization;

namespace JIM.Models.Expressions;

/// <summary>
/// Several values in an expression (#2053, PRD Phase 2 requirement 8): what <c>cs["x"]</c> and <c>mv["x"]</c> read
/// for a multi-valued attribute holding more than one value, and what a transform applied to those values returns.
/// </summary>
/// <remarks>
/// <para>Functions understand it: transforms apply to each value, comparisons are true when any value satisfies them,
/// and <c>Count</c>, <c>Min</c>, <c>Max</c>, <c>Join</c> and <c>Where</c> work on the set. Everywhere one value is
/// needed (joining text with <c>+</c>, the comparison and arithmetic operators, <c>.ToString()</c>), several values
/// are an error on the object naming the attribute and the count, and one value is used as it is.
/// <c>== null</c> and <c>!= null</c> remain presence tests.</para>
/// <para>Expressions are late-bound (see <c>engineering/EXPRESSION_SECURITY.md</c>), so every public member here is
/// reachable from an expression. Keep the surface to what an expression should use: the values, their count and the
/// operators.</para>
/// </remarks>
public sealed class ExpressionValues : IReadOnlyList<object?>
{
    private readonly object?[] _values;

    /// <summary>
    /// Several values, in the order given.
    /// </summary>
    /// <param name="values">The values, kept in this order.</param>
    /// <param name="sourceName">The attribute they were read from, named in the error when several are used as one;
    /// null where they did not come from one attribute.</param>
    public ExpressionValues(IEnumerable<object?> values, string? sourceName)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values.ToArray();
        SourceName = sourceName;
    }

    /// <summary>
    /// The attribute these values were read from, or null.
    /// </summary>
    public string? SourceName { get; }

    /// <inheritdoc/>
    public int Count => _values.Length;

    /// <summary>
    /// The number of values: the same as <see cref="Count"/>, for expressions written when <c>Split</c> returned an
    /// array.
    /// </summary>
    public int Length => _values.Length;

    /// <inheritdoc/>
    public object? this[int index] => _values[index];

    /// <summary>
    /// What an expression reads for an attribute holding <paramref name="values"/>: null for no value, the value
    /// itself for one, and every distinct value in a fixed order for several (text by ordinal, numbers ascending, dates
    /// oldest first, Guids by their text, Binary by its content). Null entries are not values.
    /// </summary>
    /// <remarks>
    /// Directories hold multi-valued attributes as unordered sets, so the order here is what keeps an expression's
    /// answer the same after another import. A Binary value is copied, so an expression can never change the
    /// attribute value its bytes came from.
    /// </remarks>
    public static object? FromAttribute(string? attributeName, IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var distinct = new List<object>();
        var keys = new HashSet<object>();
        foreach (var value in values)
        {
            if (value is null)
                continue;
            if (keys.Add(Key(value)))
                distinct.Add(value);
        }

        return distinct.Count switch
        {
            0 => null,
            1 => CopyIfBinary(distinct[0]),
            _ => new ExpressionValues(distinct.Order(ValueOrder.Instance).Select(CopyIfBinary), attributeName)
        };
    }

    /// <summary>
    /// The error for several values where one is needed. It names the attribute and the count, never a value: the
    /// message reaches the worker's logs.
    /// </summary>
    public static ExpressionEvaluationException SeveralValuesError(int count, string? sourceName)
    {
        var subject = sourceName == null ? "The expression has" : $"'{sourceName}' has";
        return new ExpressionEvaluationException(
            $"{subject} {count} values where one is needed. Use Where, Join, Count, Min or Max to say which.");
    }

    /// <summary>
    /// The one value these hold: null for none, the value for one; several are <see cref="SeveralValuesError"/>.
    /// </summary>
    public object? Single() => _values.Length switch
    {
        0 => null,
        1 => _values[0],
        _ => throw SeveralValuesError(_values.Length, SourceName)
    };

    /// <inheritdoc/>
    public IEnumerator<object?> GetEnumerator() => ((IEnumerable<object?>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// The text of the one value held, or empty text for none. Several values have no one text, so this is
    /// <see cref="SeveralValuesError"/>: it is how the language joins a value to text with <c>+</c>.
    /// </summary>
    public override string ToString() => Convert.ToString(Single(), CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>
    /// Reference equality: two sets of values are only "equal" as objects when they are the same instance. Value
    /// comparison in an expression goes through the operators or <c>Eq</c>.
    /// </summary>
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    /// <inheritdoc/>
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    // The operators an expression can apply. Each is a one-value position: compared with null, == and != test presence;
    // otherwise the one value is used as the language would use it, and several values are the error.

    public static bool operator ==(ExpressionValues? values, object? other) =>
        other is null ? values is null : values is not null && Equal(values.Single(), other);

    public static bool operator !=(ExpressionValues? values, object? other) => !(values == other);

    // The value-first forms take IComparable (every value an attribute holds is one) rather than object, so comparing
    // with a null literal binds to exactly one operator instead of being ambiguous.

    public static bool operator ==(IComparable? other, ExpressionValues? values) => values == (object?)other;

    public static bool operator !=(IComparable? other, ExpressionValues? values) => !(values == (object?)other);

    public static object? operator +(ExpressionValues values, object? other) => (dynamic?)values.Single() + (dynamic?)other;

    public static object? operator +(object? other, ExpressionValues values) => (dynamic?)other + (dynamic?)values.Single();

    public static object? operator -(ExpressionValues values, object? other) => (dynamic?)values.Single() - (dynamic?)other;

    public static object? operator -(object? other, ExpressionValues values) => (dynamic?)other - (dynamic?)values.Single();

    public static object? operator *(ExpressionValues values, object? other) => (dynamic?)values.Single() * (dynamic?)other;

    public static object? operator *(object? other, ExpressionValues values) => (dynamic?)other * (dynamic?)values.Single();

    public static object? operator /(ExpressionValues values, object? other) => (dynamic?)values.Single() / (dynamic?)other;

    public static object? operator /(object? other, ExpressionValues values) => (dynamic?)other / (dynamic?)values.Single();

    public static object? operator %(ExpressionValues values, object? other) => (dynamic?)values.Single() % (dynamic?)other;

    public static object? operator %(object? other, ExpressionValues values) => (dynamic?)other % (dynamic?)values.Single();

    public static bool operator <(ExpressionValues values, object? other) => (dynamic?)values.Single() < (dynamic?)other;

    public static bool operator <(object? other, ExpressionValues values) => (dynamic?)other < (dynamic?)values.Single();

    public static bool operator >(ExpressionValues values, object? other) => (dynamic?)values.Single() > (dynamic?)other;

    public static bool operator >(object? other, ExpressionValues values) => (dynamic?)other > (dynamic?)values.Single();

    public static bool operator <=(ExpressionValues values, object? other) => (dynamic?)values.Single() <= (dynamic?)other;

    public static bool operator <=(object? other, ExpressionValues values) => (dynamic?)other <= (dynamic?)values.Single();

    public static bool operator >=(ExpressionValues values, object? other) => (dynamic?)values.Single() >= (dynamic?)other;

    public static bool operator >=(object? other, ExpressionValues values) => (dynamic?)other >= (dynamic?)values.Single();

    /// <summary>
    /// The language's own <c>==</c> on the one value, so a single value compares exactly as it would have read alone.
    /// </summary>
    private static bool Equal(object? value, object other) => value is not null && (bool)((dynamic)value == (dynamic)other);

    private static object CopyIfBinary(object value) => value is byte[] bytes ? bytes.Clone() : value;

    /// <summary>
    /// The key two values are the same value by: text by ordinal, a date by its instant, Binary by its content, and
    /// everything else by its own equality.
    /// </summary>
    private static object Key(object value) => value switch
    {
        DateTime date => AsUtcTicks(date),
        DateTimeOffset dateWithOffset => dateWithOffset.UtcTicks,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => value
    };

    private static long AsUtcTicks(DateTime date) =>
        date.Kind == DateTimeKind.Local ? date.ToUniversalTime().Ticks : date.Ticks;

    /// <summary>
    /// The fixed order several values read in. Values of one attribute share one type; mixed types (which no attribute
    /// holds) fall back to ordering by type name, then text, so the order is still stable.
    /// </summary>
    private sealed class ValueOrder : IComparer<object>
    {
        public static readonly ValueOrder Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null || y is null)
                return x is null ? (y is null ? 0 : -1) : 1;

            return (x, y) switch
            {
                (string a, string b) => string.CompareOrdinal(a, b),
                (DateTime or DateTimeOffset, DateTime or DateTimeOffset) => ((long)Key(x)).CompareTo((long)Key(y)),
                (Guid a, Guid b) => string.CompareOrdinal(a.ToString(), b.ToString()),
                (byte[] a, byte[] b) => string.CompareOrdinal((string)Key(a), (string)Key(b)),
                (bool a, bool b) => a.CompareTo(b),
                _ when IsNumber(x) && IsNumber(y) => Convert.ToDecimal(x, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(y, CultureInfo.InvariantCulture)),
                _ => CompareByTypeThenText(x, y)
            };
        }

        private static bool IsNumber(object value) => value is int or long or decimal or short or byte;

        private static int CompareByTypeThenText(object x, object y)
        {
            var byType = string.CompareOrdinal(x.GetType().FullName, y.GetType().FullName);
            return byType != 0
                ? byType
                : string.CompareOrdinal(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture));
        }
    }
}
