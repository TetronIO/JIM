// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using JIM.Application.Expressions;
using JIM.Web.Models;

namespace JIM.Web;

/// <summary>
/// What the expression editor offers as an expression is typed (#2053, PRD Phase 2 requirement 9): suggestions at the
/// caret, functions from the function catalogue and attribute names inside <c>mv["..."]</c> or <c>cs["..."]</c>, and
/// the signature of the function call the caret is inside. Reads the text alone, as the highlighter does; nothing is
/// parsed or evaluated.
/// </summary>
public static partial class ExpressionAssist
{
    /// <summary>The most suggestions offered at once.</summary>
    public const int MaxSuggestions = 10;

    /// <summary>
    /// The suggestions at <paramref name="caret"/> in <paramref name="text"/>: inside an accessor's quotes, the
    /// attributes of that side whose names start with, then contain, what is typed; at the end of a word outside any
    /// text, the functions whose names do. Nothing elsewhere.
    /// </summary>
    public static ExpressionCompletion Complete(string? text, int caret,
        IReadOnlyCollection<string> metaverseAttributes, IReadOnlyCollection<string> connectedSystemAttributes)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);

        var literalStart = OpenLiteralStart(text, caret);
        return literalStart >= 0
            ? CompleteAttribute(text, caret, literalStart, metaverseAttributes, connectedSystemAttributes)
            : CompleteFunction(text, caret);
    }

    /// <summary>
    /// The function call <paramref name="caret"/> is inside, and which of its parameters, or null where it is in none
    /// the catalogue knows.
    /// </summary>
    public static ExpressionSignature? SignatureAt(string? text, int caret)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);

        // Each open parenthesis the caret is inside, with the commas met at its own level.
        var open = new Stack<(int Index, int Commas)>();
        var inLiteral = false;
        for (var i = 0; i < caret; i++)
        {
            var c = text[i];
            if (inLiteral)
            {
                if (c == '\\')
                    i++;
                else if (c == '"')
                    inLiteral = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inLiteral = true;
                    break;
                case '(':
                    open.Push((i, 0));
                    break;
                case ')' when open.Count > 0:
                    open.Pop();
                    break;
                case ',' when open.Count > 0:
                    var (index, commas) = open.Pop();
                    open.Push((index, commas + 1));
                    break;
            }
        }

        if (inLiteral || open.Count == 0)
            return null;

        var (parenthesis, commaCount) = open.Peek();
        var function = ExpressionFunctionCatalogue.Find(WordBefore(text, parenthesis));
        return function == null
            ? null
            : new ExpressionSignature(function, Math.Clamp(commaCount, 0, Math.Max(0, function.Parameters.Count - 1)));
    }

    /// <summary>
    /// A catalogue description as the editor shows it: plain text, a Markdown link read as its words.
    /// </summary>
    public static string PlainText(string description) =>
        MarkdownLink().Replace(description, "${text}").Replace("`", string.Empty, StringComparison.Ordinal);

    private static ExpressionCompletion CompleteAttribute(string text, int caret, int literalStart,
        IReadOnlyCollection<string> metaverseAttributes, IReadOnlyCollection<string> connectedSystemAttributes)
    {
        var accessor = AccessorBefore().Match(text[..literalStart]);
        if (!accessor.Success)
            return ExpressionCompletion.None(caret);

        var (names, kind) = accessor.Groups["side"].Value == "mv"
            ? (metaverseAttributes, ExpressionSuggestionKind.MetaverseAttribute)
            : (connectedSystemAttributes, ExpressionSuggestionKind.ConnectedSystemAttribute);

        // Typing into an accessor already closed (cs["ma|"]) replaces the name only; otherwise the quote and bracket
        // are written too.
        var closing = caret < text.Length && text[caret] == '"' ? string.Empty : "\"]";
        var typed = text[(literalStart + 1)..caret];
        var suggestions = Ranked(names, name => name, typed)
            .Select(name => new ExpressionSuggestion(kind, name, Escape(name) + closing, null, null))
            .ToList();

        return new ExpressionCompletion(literalStart + 1, caret, suggestions);
    }

    private static ExpressionCompletion CompleteFunction(string text, int caret)
    {
        // Mid-word, or a name already followed by its call, has nothing left to complete.
        if (caret < text.Length && (IsWordChar(text[caret]) || text[caret] == '('))
            return ExpressionCompletion.None(caret);

        var start = caret;
        while (start > 0 && IsWordChar(text[start - 1]))
            start--;

        var word = text[start..caret];
        if (word.Length == 0 || !char.IsLetter(word[0]) || start > 0 && text[start - 1] == '.')
            return ExpressionCompletion.None(caret);

        var suggestions = Ranked(ExpressionFunctionCatalogue.All, function => function.Name, word)
            .Select(function => new ExpressionSuggestion(ExpressionSuggestionKind.Function, function.Name, function.Name + "(",
                function.Signature, PlainText(function.Description)))
            .ToList();

        return new ExpressionCompletion(start, caret, suggestions);
    }

    /// <summary>
    /// The items whose name starts with <paramref name="typed"/>, then those that only contain it, each in their own
    /// order, ignoring case; every item when nothing is typed.
    /// </summary>
    private static IEnumerable<T> Ranked<T>(IEnumerable<T> items, Func<T, string> name, string typed)
    {
        var list = items.ToList();
        var starting = list.Where(item => name(item).StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        var containing = list.Where(item => !name(item).StartsWith(typed, StringComparison.OrdinalIgnoreCase) &&
                                            name(item).Contains(typed, StringComparison.OrdinalIgnoreCase));
        return starting.Concat(containing).Take(MaxSuggestions);
    }

    /// <summary>
    /// Where the text literal the caret is inside opens, or -1 when it is in none.
    /// </summary>
    private static int OpenLiteralStart(string text, int caret)
    {
        var start = -1;
        for (var i = 0; i < caret; i++)
        {
            if (start >= 0 && text[i] == '\\')
                i++;
            else if (text[i] == '"')
                start = start >= 0 ? -1 : i;
        }

        return start;
    }

    private static string WordBefore(string text, int index)
    {
        var end = index;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;

        var start = end;
        while (start > 0 && IsWordChar(text[start - 1]))
            start--;

        return text[start..end];
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Escape(string name) =>
        name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    [GeneratedRegex(@"\b(?<side>mv|cs)\s*\[\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AccessorBefore();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\([^)]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();
}
