// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How a scripted cn=accesslog decides which logged writes one of its searches matches, as OpenLDAP answers the same
/// filter: the filter is evaluated as written, its ANDs and ORs included, so a change read that gives each partition
/// its own reqStart bound (<c>(|(&amp;(reqDN:dnSubtreeMatch:=A)(reqStart&gt;=W_A))...)</c>) is answered clause by clause.
/// Two kinds of assertion vary between writes: a reqStart bound, which a write matches when it is at or after it, and
/// a partition named with dnSubtreeMatch, which a write matches when it was made to an entry within it. Every scripted
/// write is a successful write operation, so every other assertion matches them all.
/// </summary>
internal static class ScriptedAccesslogFilter
{
    internal static bool Matches(string filter, string reqStart, string reqDn)
    {
        var position = 0;
        var matches = Evaluate(filter, ref position, reqStart, reqDn);
        if (position != filter.Length)
            throw new FormatException($"The filter '{filter}' has text after its end at position {position}.");

        return matches;
    }

    /// <summary>Evaluates the parenthesised filter starting at <paramref name="position"/>, leaving it just past its end.</summary>
    private static bool Evaluate(string filter, ref int position, string reqStart, string reqDn)
    {
        Expect(filter, position, '(');
        position++;

        if (filter[position] is '&' or '|')
        {
            var isAnd = filter[position] == '&';
            position++;

            var operands = new List<bool>();
            while (position < filter.Length && filter[position] == '(')
                operands.Add(Evaluate(filter, ref position, reqStart, reqDn));

            Expect(filter, position, ')');
            position++;
            return isAnd ? operands.All(operand => operand) : operands.Any(operand => operand);
        }

        // An assertion runs to the next closing parenthesis: RFC 4515 escapes one in a value, so none appears raw.
        var end = filter.IndexOf(')', position);
        if (end < 0)
            throw new FormatException($"The filter '{filter}' has an assertion with no end at position {position}.");

        var assertion = filter[position..end];
        position = end + 1;
        return AssertionMatches(assertion, reqStart, reqDn);
    }

    private static bool AssertionMatches(string assertion, string reqStart, string reqDn)
    {
        var bound = Regex.Match(assertion, @"^reqStart>=(.*)$");
        if (bound.Success)
            return string.CompareOrdinal(reqStart, bound.Groups[1].Value) >= 0;

        var partition = Regex.Match(assertion, @"^reqDN:dnSubtreeMatch:=(.*)$");
        return !partition.Success || IsWithin(reqDn, Unescape(partition.Groups[1].Value));
    }

    private static void Expect(string filter, int position, char expected)
    {
        if (position >= filter.Length || filter[position] != expected)
            throw new FormatException($"The filter '{filter}' was expected to have '{expected}' at position {position}.");
    }

    /// <summary>dnSubtreeMatch: the entry is the partition's own, or one beneath it.</summary>
    private static bool IsWithin(string dn, string partition) =>
        string.Equals(dn, partition, StringComparison.OrdinalIgnoreCase)
        || dn.EndsWith("," + partition, StringComparison.OrdinalIgnoreCase);

    /// <summary>A filter value as written, with RFC 4515's escapes (a backslash and two hex digits) undone.</summary>
    private static string Unescape(string value) =>
        Regex.Replace(value, @"\\([0-9a-fA-F]{2})", match => ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
}
