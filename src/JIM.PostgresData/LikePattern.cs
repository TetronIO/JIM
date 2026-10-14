// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData;

/// <summary>
/// Builds <c>LIKE</c>/<c>ILIKE</c> patterns from text an administrator typed, so that <c>%</c>, <c>_</c> and <c>\</c>
/// match themselves rather than acting as wildcards (#2053). Every <c>LIKE</c> and <c>ILIKE</c> in JIM takes its pattern
/// from here: a search for "svc_" that also returned "svcX" looks like an answer, which is worse than no answer.
/// </summary>
/// <remarks>
/// The escape character is PostgreSQL's default for <c>LIKE</c> (a backslash), so a pattern from here needs no
/// <c>ESCAPE</c> clause; <c>EF.Functions.ILike(column, pattern)</c> and raw <c>col ILIKE @p</c> both honour it.
/// </remarks>
public static class LikePattern
{
    /// <summary>The text with every <c>LIKE</c> metacharacter escaped, so it matches only itself.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    /// <summary>A pattern matching any value that contains the text.</summary>
    public static string Contains(string text) => $"%{Escape(text)}%";

    /// <summary>A pattern matching any value that starts with the text.</summary>
    public static string StartsWith(string text) => $"{Escape(text)}%";

    /// <summary>A pattern matching any value that ends with the text.</summary>
    public static string EndsWith(string text) => $"%{Escape(text)}";
}
