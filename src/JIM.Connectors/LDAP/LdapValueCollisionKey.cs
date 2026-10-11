// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.LDAP;

/// <summary>
/// What a value is compared by when deciding whether a delete and an add of one attribute name the same value: text
/// without regard to case, as Active Directory compares proxyAddresses and every directory compares a Distinguished
/// Name, and binary exactly. Matching too much is harmless, since a delete sent first in the same request as the add
/// reaches the same state either way; matching too little is what loses values.
/// </summary>
/// <remarks>
/// One definition serves both the chunking that splits an export into requests and the fallback that applies a refused
/// request in parts, so neither can separate a pair the other keeps together.
/// </remarks>
internal static class LdapValueCollisionKey
{
    /// <summary>
    /// The comparer every collection keyed by <see cref="Of"/> must use. Text compares without regard to case; the hex of
    /// a binary value is the same whatever its case, so binary values still compare exactly.
    /// </summary>
    internal static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// The key a value is compared by, or null for a value that is never treated as colliding with another.
    /// </summary>
    internal static string? Of(object? value) => value switch
    {
        string text => "s:" + text,
        byte[] bytes => "b:" + Convert.ToHexString(bytes),
        _ => null
    };

    /// <summary>
    /// Values grouped by their key, keys in the order each first appears and values in their original order. A value
    /// with no key is left out.
    /// </summary>
    internal static Dictionary<string, List<object?>> Group(IEnumerable<object?> values)
    {
        var groups = new Dictionary<string, List<object?>>(Comparer);
        foreach (var (key, value) in values
                     .Select(value => (Key: Of(value), Value: value))
                     .Where(keyed => keyed.Key != null))
        {
            if (!groups.TryGetValue(key!, out var group))
            {
                group = [];
                groups[key!] = group;
            }

            group.Add(value);
        }

        return groups;
    }
}
