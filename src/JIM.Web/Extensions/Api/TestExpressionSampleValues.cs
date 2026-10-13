// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections;
using System.Text.Json;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Web.Models.Api;

namespace JIM.Web.Extensions.Api;

/// <summary>
/// Reads the REST API's sample attribute values (<see cref="TestExpressionRequest"/>, <see cref="TestScopeRequest"/>) as
/// configuration tester inputs (#2055): null is no value, an array is several values, and anything else is one value.
/// </summary>
public static class TestExpressionSampleValues
{
    /// <summary>
    /// The samples in <paramref name="attributes"/> as tester inputs on <paramref name="source"/>'s side.
    /// </summary>
    public static IEnumerable<AttributeFlowTestInput> ToTestInputs(Dictionary<string, object?>? attributes, ExpressionInputSource source) =>
        attributes == null
            ? []
            : attributes.Select(attribute => new AttributeFlowTestInput(source, attribute.Key, Values(attribute.Value)));

    /// <summary>
    /// The samples in <paramref name="attributes"/> as an expression's attribute dictionary (#2053): null is no value,
    /// an array is several values (read in their fixed order by the accessor), and anything else is one value.
    /// </summary>
    public static Dictionary<string, object?> ToExpressionAttributes(Dictionary<string, object?>? attributes)
    {
        var expressionAttributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (attributes == null)
            return expressionAttributes;

        foreach (var (name, sample) in attributes)
        {
            var values = Values(sample);
            expressionAttributes[name] = sample is JsonElement { ValueKind: JsonValueKind.Array } or (IEnumerable and not string and not byte[])
                ? values
                : values.FirstOrDefault();
        }

        return expressionAttributes;
    }

    /// <summary>
    /// The samples in <paramref name="attributes"/> as configuration tester inputs, one per attribute.
    /// </summary>
    public static IEnumerable<AttributeSample> ToAttributeSamples(Dictionary<string, object?>? attributes) =>
        attributes == null
            ? []
            : attributes.Select(attribute => new AttributeSample(attribute.Key, Values(attribute.Value)));

    private static List<object> Values(object? sample) => sample switch
    {
        null => [],
        JsonElement json => JsonValues(json),
        string or byte[] => [sample],
        IEnumerable several => several.Cast<object?>().SelectMany(Values).ToList(),
        _ => [sample]
    };

    private static List<object> JsonValues(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => [],
        JsonValueKind.Array => json.EnumerateArray().SelectMany(JsonValues).ToList(),
        JsonValueKind.String => [json.GetString()!],
        JsonValueKind.True => [true],
        JsonValueKind.False => [false],
        JsonValueKind.Number => [json.TryGetInt64(out var whole) ? whole : json.GetDecimal()],
        // An object has no attribute value form; its JSON text is passed on for the type conversion to refuse.
        _ => [json.GetRawText()]
    };
}
