// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// What the expression editor can offer at the caret (#2053): the suggestions, and the span of text a chosen one replaces.
/// </summary>
/// <param name="ReplaceStart">Where the text being typed starts.</param>
/// <param name="ReplaceEnd">Where it ends: the caret.</param>
/// <param name="Suggestions">The suggestions, best first; empty where there is nothing to suggest.</param>
public sealed record ExpressionCompletion(int ReplaceStart, int ReplaceEnd, IReadOnlyList<ExpressionSuggestion> Suggestions)
{
    /// <summary>Nothing to suggest.</summary>
    public static ExpressionCompletion None(int caret) => new(caret, caret, []);
}
