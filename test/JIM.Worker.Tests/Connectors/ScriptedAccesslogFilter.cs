// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How a scripted cn=accesslog decides which logged writes one of the watermark walk's searches matches, by the two
/// parts of its filter that vary: the reqStart bound, and the partitions it names with dnSubtreeMatch. A write
/// matches when it is at or after the bound (any write, when there is none) and was made to an entry within one of
/// the named partitions (any entry, when none is named), as OpenLDAP answers the same filter. Every scripted write
/// is a successful write operation, so the rest of the filter matches them all.
/// </summary>
internal static class ScriptedAccesslogFilter
{
    internal static bool Matches(string filter, string reqStart, string reqDn)
    {
        var bound = Regex.Match(filter, @"\(reqStart>=([^)]*)\)");
        if (bound.Success && string.CompareOrdinal(reqStart, bound.Groups[1].Value) < 0)
            return false;

        var partitions = Regex.Matches(filter, @"\(reqDN:dnSubtreeMatch:=([^)]*)\)")
            .Select(match => Unescape(match.Groups[1].Value))
            .ToList();
        return partitions.Count == 0 || partitions.Any(partition => IsWithin(reqDn, partition));
    }

    /// <summary>dnSubtreeMatch: the entry is the partition's own, or one beneath it.</summary>
    private static bool IsWithin(string dn, string partition) =>
        string.Equals(dn, partition, StringComparison.OrdinalIgnoreCase)
        || dn.EndsWith("," + partition, StringComparison.OrdinalIgnoreCase);

    /// <summary>A filter value as written, with RFC 4515's escapes (a backslash and two hex digits) undone.</summary>
    private static string Unescape(string value) =>
        Regex.Replace(value, @"\\([0-9a-fA-F]{2})", match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
}
