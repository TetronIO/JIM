// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Utilities;

namespace JIM.Application.Expressions;

/// <summary>
/// The one handler for what an Expression's result gives an attribute (#2179). Inbound Attribute Flow, export
/// staging and the provenance inspector resolve a result here, and Object Matching reads its elements the same way,
/// so a result means the same thing wherever it is evaluated.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Any collection (an array or list of any type) is several values; text and Binary are each one value.</item>
/// <item>Each value is converted to the attribute's type by <see cref="ExpressionResultConverter"/>; one value that
/// cannot be converted refuses the whole result, so nothing is stored rather than part of it.</item>
/// <item>Values are deduplicated by their typed canonical value: text by ordinal, dates by instant to the
/// microsecond, numbers by value, Binary by content.</item>
/// <item>Blank text is no value for an attribute that is not Text; for a Text attribute the caller's text
/// processing decides.</item>
/// </list>
/// </remarks>
public static class ExpressionResultValues
{
    /// <summary>
    /// Whether <paramref name="result"/> holds several values: any collection other than text or Binary.
    /// </summary>
    public static bool IsCollection(object? result) => result is IEnumerable and not string and not byte[];

    /// <summary>
    /// The values <paramref name="result"/> holds: none for null, each element of a collection, or the result itself.
    /// </summary>
    public static IReadOnlyList<object?> Elements(object? result) => result switch
    {
        null => [],
        IEnumerable collection when IsCollection(result) => collection.Cast<object?>().ToList(),
        _ => [result]
    };

    /// <summary>
    /// Resolves <paramref name="result"/> against an attribute of <paramref name="targetType"/> and
    /// <paramref name="targetPlurality"/>.
    /// </summary>
    /// <param name="processText">For a Text attribute, the mapping's own text processing, applied to each value; a
    /// value it collapses to null is dropped. Null applies none.</param>
    public static ExpressionResultResolution Resolve(
        object? result,
        AttributeDataType targetType,
        AttributePlurality targetPlurality,
        Func<string, string?>? processText = null)
    {
        var values = new List<object>();
        var keys = new HashSet<object>();

        foreach (var element in Elements(result).Where(element => element != null && !IsBlankForType(element, targetType)))
        {
            if (!ExpressionResultConverter.TryConvert(element, targetType, out var converted) || converted == null)
                return ExpressionResultResolution.NotConvertible(ExpressionText.Describe(element!));

            if (converted is string text && processText != null)
            {
                var processed = processText(text);
                if (processed == null)
                    continue;
                converted = processed;
            }

            if (keys.Add(Key(converted)))
                values.Add(converted);
        }

        if (values.Count == 0)
            return ExpressionResultResolution.NoValue();

        return targetPlurality == AttributePlurality.SingleValued && values.Count > 1
            ? ExpressionResultResolution.SeveralValues(values.Count, (result as ExpressionValues)?.SourceName)
            : ExpressionResultResolution.WithValues(values);
    }

    /// <summary>
    /// The key two converted values are compared by: equal keys are the same value. Text compares by ordinal, a
    /// date by its canonical ticks, Binary by its content, and every other type by value.
    /// </summary>
    public static object Key(object convertedValue) => convertedValue switch
    {
        DateTime date => DateTimeAttributeValue.Key(date),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => convertedValue
    };

    private static bool IsBlankForType(object? element, AttributeDataType targetType) =>
        targetType != AttributeDataType.Text && element is string text && string.IsNullOrWhiteSpace(text);
}
