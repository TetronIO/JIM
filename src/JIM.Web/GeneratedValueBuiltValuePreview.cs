// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Web.Models;

namespace JIM.Web;

/// <summary>
/// Works out, for the generated flow's preview, the values built from a candidate that are checked with it (Generated
/// Value Forms, #2023, PRD requirement 11): each value's expression evaluated with the candidate in place of the
/// generated value, through the values it is built through, against "Test this Expression"'s sample values. A preview of
/// what the run checks, not the run's own evaluation: inputs no sample gives read as their own names, as the base
/// expression's do with no samples entered, since the tester offers only the base expression's inputs.
/// </summary>
public static class GeneratedValueBuiltValuePreview
{
    /// <summary>
    /// The values <paramref name="values"/> holds that are checked before issuing, worked out for
    /// <paramref name="candidate"/>, nearest the generated value first.
    /// </summary>
    /// <param name="candidate">The candidate generated value.</param>
    /// <param name="values">The values built from the generated value, as described for its panel.</param>
    /// <param name="samples">The sample values from "Test this Expression", keyed by accessor.</param>
    /// <param name="evaluator">The expression evaluator.</param>
    public static List<GeneratedValueBuiltValuePreviewValue> WorkOut(string candidate, IReadOnlyList<GeneratedValueBuiltValueDescription> values,
        IReadOnlyDictionary<string, string?> samples, IExpressionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(evaluator);

        var checkedValues = values
            .Where(v => v.CheckedBeforeIssuing && !string.IsNullOrWhiteSpace(v.Expression))
            .OrderBy(v => v.Via.Count)
            .ThenBy(v => v.MetaverseAttributeName == null ? 1 : 0)
            .ThenBy(NameOf, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (checkedValues.Count == 0)
            return [];

        // The Metaverse as the candidate would leave it: the generated value, then each built value as it is worked out.
        var metaverse = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [checkedValues[0].GeneratedAttributeName] = candidate };
        var worked = new List<GeneratedValueBuiltValuePreviewValue>(checkedValues.Count);
        foreach (var value in checkedValues)
        {
            var expression = value.Expression!;
            var metaverseInputs = new Dictionary<string, object?>(metaverse, StringComparer.OrdinalIgnoreCase);
            var connectedSystemInputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in ExpressionInputResolver.Resolve(expression))
            {
                var target = input.Source == ExpressionInputSource.Metaverse ? metaverseInputs : connectedSystemInputs;
                target.TryAdd(input.AttributeName, SampleFor(input, samples));
            }

            var result = evaluator.Test(expression, new ExpressionContext(metaverseInputs, connectedSystemInputs));
            // In JIM's text form (#2153, N1), as the engine writes the value; a value with no text form gives none.
            var text = result.IsValid && ExpressionText.TryFormat(result.Result, out var formatted) ? formatted : null;
            worked.Add(new GeneratedValueBuiltValuePreviewValue(NameOf(value), text, result.IsValid ? null : result.ErrorMessage ?? "The expression could not be evaluated."));

            if (value.MetaverseAttributeName != null)
                metaverse[value.MetaverseAttributeName] = text;
        }

        return worked;
    }

    private static string NameOf(GeneratedValueBuiltValueDescription value) =>
        value.MetaverseAttributeName ?? value.ConnectedSystemAttributeName ?? string.Empty;

    /// <summary>
    /// An input's sample: the one entered, no value where it was entered blank, otherwise its own name.
    /// </summary>
    private static object? SampleFor(ExpressionInput input, IReadOnlyDictionary<string, string?> samples) =>
        samples.TryGetValue(input.Accessor, out var sample)
            ? string.IsNullOrEmpty(sample) ? null : sample
            : input.AttributeName;
}
