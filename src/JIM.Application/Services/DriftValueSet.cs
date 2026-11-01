// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Utilities;

namespace JIM.Application.Services;

/// <summary>
/// One side of a multi-valued attribute's Drift Detection comparison, gathered once so that each value of the other side
/// is asked after by lookup rather than by comparing it with every value here (#2658). Compared value by value, a large
/// group cost the square of its membership: finding the corrections for a drifted 50,000-member group compared every
/// expected member with every held one and every held member with every expected one, and a target holding 50,000 Guids
/// as their text paid the same to be found not drifted at all.
/// </summary>
/// <remarks>
/// <para>
/// The answer is exactly that of comparing the asked-after value with every value here by
/// <see cref="DriftDetectionService.SingleValueEquals"/>, and the lookups are keyed the way that comparison decides, so
/// the two must change together; a differential test holds them to one answer (<c>DriftValueSetDifferentialTests</c>).
/// The comparison is not symmetric (an expected Guid matches a held Guid's text, but expected text never matches a held
/// Guid), so the question is asked from the side it is asked about: <see cref="HoldsMatchForExpected"/> when the values
/// here are what the target holds, <see cref="HoldsMatchForActual"/> when they are what it should hold.
/// </para>
/// <para>
/// The comparison decides by the runtime types of its two values, which are those Drift Detection reads values as:
/// binary by content, text by the attribute's comparison, a Guid by its value or by text that parses as it, and every
/// other value (numbers, dates, booleans) by its own equality, which matches only a value of the same type and agrees
/// with its hash. A value of any other type is never produced; should one appear on either side, the question is answered
/// by the scan, which is never wrong. Each lookup is built on its first question, from one pass over the values.
/// </para>
/// </remarks>
internal sealed class DriftValueSet
{
    private readonly HashSet<object> _values;
    private readonly StringComparer _textComparer;

    // Whether every value here is of a type the lookups are keyed for; if not, every question is answered by the scan.
    private readonly bool _keyedTypesOnly;

    private HashSet<string>? _text;
    private HashSet<byte[]>? _binary;
    private HashSet<Guid>? _guidsAsText;

    /// <param name="values">The values of one side, as Drift Detection reads them.</param>
    /// <param name="textComparer">How the attribute compares text (<see cref="JIM.Utilities.AttributeValueComparison"/>).</param>
    public DriftValueSet(HashSet<object> values, StringComparer textComparer)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(textComparer);

        _values = values;
        _textComparer = textComparer;
        _keyedTypesOnly = values.All(value => value is null || IsKeyedType(value));
    }

    /// <summary>
    /// Whether any value here, as a value the target holds, matches <paramref name="expected"/>: exactly what comparing
    /// it with each by <see cref="DriftDetectionService.SingleValueEquals"/>(<paramref name="expected"/>, value) says.
    /// </summary>
    public bool HoldsMatchForExpected(object? expected)
    {
        if (expected == null)
            return _values.Contains(null!);

        if (!_keyedTypesOnly || !IsKeyedType(expected))
            return _values.Any(actual => DriftDetectionService.SingleValueEquals(expected, actual, _textComparer));

        return expected switch
        {
            byte[] bytes => Binary.Contains(bytes),
            string text => Text.Contains(text),
            // A Guid matches the same Guid, or text that parses as it.
            Guid guid => _values.Contains(guid) || GuidsAsText.Contains(guid),
            // A number, date or boolean matches only a value of its own type equal to it, which its hash agrees with.
            _ => _values.Contains(expected)
        };
    }

    /// <summary>
    /// Whether any value here, as a value the target should hold, matches <paramref name="actual"/>: exactly what comparing
    /// each with it by <see cref="DriftDetectionService.SingleValueEquals"/>(value, <paramref name="actual"/>) says.
    /// </summary>
    public bool HoldsMatchForActual(object? actual)
    {
        if (actual == null)
            return _values.Contains(null!);

        if (!_keyedTypesOnly || !IsKeyedType(actual))
            return _values.Any(expected => DriftDetectionService.SingleValueEquals(expected, actual, _textComparer));

        return actual switch
        {
            byte[] bytes => Binary.Contains(bytes),
            // Text matches the same text, or an expected Guid it parses as.
            string text => Text.Contains(text) || (Guid.TryParse(text, out var guid) && _values.Contains(guid)),
            // A Guid matches only the same Guid; expected text never matches one. A number, date or boolean matches only a
            // value of its own type equal to it.
            _ => _values.Contains(actual)
        };
    }

    private HashSet<string> Text => _text ??= _values.OfType<string>().ToHashSet(_textComparer);

    // Binary values compared by content, as DriftDetectionService.SingleValueEquals compares them.
    private HashSet<byte[]> Binary => _binary ??= _values.OfType<byte[]>().ToHashSet(ByteArrayContentComparer.Instance);

    private HashSet<Guid> GuidsAsText => _guidsAsText ??= [.. _values.OfType<string>().SelectMany(GuidParsedFrom)];

    private static IEnumerable<Guid> GuidParsedFrom(string text) => Guid.TryParse(text, out var guid) ? [guid] : [];

    private static bool IsKeyedType(object value) =>
        value is string or byte[] or Guid or int or long or decimal or DateTime or bool;
}
