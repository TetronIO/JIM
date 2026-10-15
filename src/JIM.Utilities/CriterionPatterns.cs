// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace JIM.Utilities;

/// <summary>
/// The compiled regular expressions behind the "matches pattern" operators (#2053). Every pattern is built the same
/// way, with the same time limit, whether it is being validated on save or evaluated during synchronisation, so a
/// pattern that saves is a pattern that runs.
/// </summary>
/// <remarks>
/// A scoping criterion is evaluated once per object, so constructing its expression per evaluation would parse the
/// pattern hundreds of thousands of times in one run. Expressions are cached by pattern and case setting. The cache is
/// bounded: a deployment holds a handful of patterns, so reaching the bound means something is generating them, and
/// starting again costs one recompile each rather than unbounded memory.
/// </remarks>
public static class CriterionPatterns
{
    /// <summary>
    /// How long one match may run before it is abandoned with a <see cref="RegexMatchTimeoutException"/>. A pattern
    /// that backtracks catastrophically on some value must fail that object loudly, not stall synchronisation.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private const int MaximumCachedPatterns = 512;

    private static readonly ConcurrentDictionary<(string Pattern, bool CaseSensitive), Regex> Cache = new();

    /// <summary>
    /// The expression for a pattern, ignoring case unless <paramref name="caseSensitive"/>. Culture-invariant, so
    /// case-insensitive matching does not depend on the server's locale. Throws <see cref="ArgumentException"/> for a
    /// pattern that is not a valid regular expression.
    /// </summary>
    public static Regex Get(string pattern, bool caseSensitive)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var key = (pattern, caseSensitive);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        var regex = new Regex(pattern, options, MatchTimeout);

        if (Cache.Count >= MaximumCachedPatterns)
            Cache.Clear();

        return Cache.GetOrAdd(key, regex);
    }
}
