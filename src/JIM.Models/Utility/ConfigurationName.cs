// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Utility;

/// <summary>
/// Resolves a configuration name, such as an attribute, Object Type or Predefined Search name someone typed, to the
/// name JIM stores. Configuration names ignore case (see the Case Sensitivity concept page), so <c>MAIL</c> finds
/// <c>mail</c>.
/// </summary>
/// <remarks>
/// A Connected System whose own names are case-exact can hold two that differ only in case, so an exact name always
/// wins, and a case-insensitive match is taken only when it is the only one. Anything else would be a guess, and is
/// answered as not found rather than with the wrong configuration item. Resolving first and then querying by the
/// resolved name also keeps the query on the column's index.
/// </remarks>
public static class ConfigurationName
{
    /// <summary>
    /// The stored name <paramref name="requested"/> refers to, or null when none does or the match is ambiguous.
    /// </summary>
    /// <param name="storedNames">The names to resolve against.</param>
    /// <param name="requested">The name as given.</param>
    public static string? Resolve(IEnumerable<string> storedNames, string requested)
    {
        var candidates = storedNames
            .Where(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidates.Contains(requested, StringComparer.Ordinal))
            return requested;

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
