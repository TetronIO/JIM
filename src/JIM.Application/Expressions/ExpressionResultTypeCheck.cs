// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;

namespace JIM.Application.Expressions;

/// <summary>
/// Whether an expression's result can suit its target, where that can be known before it runs (#2055 Phase 3). The
/// evaluator binds late, so an expression's result type is known only when its outermost call is a catalogue function,
/// whose <see cref="ExpressionFunction.ResultType"/> says what each value it returns is. Only a type that
/// <see cref="ExpressionResultConverter"/> never accepts for the target is refused; anything unknown passes, so this
/// can never refuse an expression a synchronisation would have run.
/// </summary>
public static class ExpressionResultTypeCheck
{
    /// <summary>
    /// The type of each value <paramref name="expression"/> returns: its outermost call's declared type when the whole
    /// expression is one call to a catalogue function, otherwise Unknown.
    /// </summary>
    public static ExpressionResultType ResultTypeOf(string? expression) =>
        OutermostFunction(expression)?.ResultType ?? ExpressionResultType.Unknown;

    /// <summary>
    /// True when no value of <paramref name="resultType"/> converts to <paramref name="targetType"/>, by the rules of
    /// <see cref="ExpressionResultConverter.TryConvert"/>. Text can be read as most types, so it is refused only for a
    /// type no expression produces.
    /// </summary>
    public static bool CanNeverHold(ExpressionResultType resultType, AttributeDataType targetType)
    {
        if (resultType == ExpressionResultType.Unknown)
            return false;

        return targetType switch
        {
            AttributeDataType.Text => false,
            AttributeDataType.Number or AttributeDataType.LongNumber or AttributeDataType.Decimal =>
                resultType is ExpressionResultType.Boolean or ExpressionResultType.DateTime,
            AttributeDataType.DateTime => resultType is not (ExpressionResultType.Text or ExpressionResultType.DateTime),
            AttributeDataType.Boolean => resultType is not (ExpressionResultType.Text or ExpressionResultType.Boolean),
            AttributeDataType.Guid => resultType is not ExpressionResultType.Text,

            // Binary takes only a Binary value and Reference none at all; no catalogue function returns either.
            _ => true
        };
    }

    /// <summary>
    /// Why <paramref name="expression"/>'s result can never suit <paramref name="targetType"/>, naming the function and
    /// <paramref name="targetName"/>; or null when it can, or when that is not known until it runs.
    /// </summary>
    public static string? DescribeProblem(string? expression, AttributeDataType targetType, string targetName)
    {
        var function = OutermostFunction(expression);
        if (function == null || !CanNeverHold(function.ResultType, targetType))
            return null;

        return $"{function.Name} returns {function.Returns}, which '{targetName}' can never hold as {Describe(targetType)} attribute.";
    }

    /// <summary>
    /// The catalogue function <paramref name="expression"/> is one call to, or null: the expression must be a function
    /// name, its opening bracket, and the bracket that closes it as the last character. Brackets inside string literals
    /// are text, not structure.
    /// </summary>
    private static ExpressionFunction? OutermostFunction(string? expression)
    {
        var text = expression?.Trim();
        if (string.IsNullOrEmpty(text) || !(char.IsLetter(text[0]) || text[0] == '_'))
            return null;

        var nameEnd = 1;
        while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] == '_'))
            nameEnd++;

        var open = nameEnd;
        while (open < text.Length && char.IsWhiteSpace(text[open]))
            open++;
        if (open >= text.Length || text[open] != '(')
            return null;

        return ClosingBracket(text, open) == text.Length - 1 ? ExpressionFunctionCatalogue.Find(text[..nameEnd]) : null;
    }

    /// <summary>The index of the bracket that closes the one at <paramref name="open"/>, or -1 when none does.</summary>
    private static int ClosingBracket(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"' or '\'':
                    i = EndOfLiteral(text, i);
                    if (i < 0)
                        return -1;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                        return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>The index of the quote that ends the literal starting at <paramref name="start"/>, or -1.</summary>
    private static int EndOfLiteral(string text, int start)
    {
        var quote = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
                i++;
            else if (text[i] == quote)
                return i;
        }

        return -1;
    }

    private static string Describe(AttributeDataType type) => type switch
    {
        AttributeDataType.Text => "a Text",
        AttributeDataType.Number => "a Number",
        AttributeDataType.LongNumber => "a Long Number",
        AttributeDataType.Decimal => "a Decimal",
        AttributeDataType.DateTime => "a Date Time",
        AttributeDataType.Boolean => "a Boolean",
        AttributeDataType.Guid => "a Guid",
        AttributeDataType.Binary => "a Binary",
        AttributeDataType.Reference => "a Reference",
        _ => "this"
    };
}
