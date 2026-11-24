// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;

namespace JIM.Web.Tests.Spacing;

/// <summary>
/// Finds hand-placed vertical margin utilities (<c>mt-</c>, <c>mb-</c>, <c>my-</c>, <c>ma-</c>, non-zero) in Razor
/// markup (#3095). Vertical spacing comes from the design system's <c>SectionStack</c> and <c>SectionCaption</c>, so a
/// page that places its own margin is choosing a gap the design system should have chosen.
/// </summary>
internal static partial class VerticalSpacingScanner
{
    /// <summary>A deliberate exception, written on the margin's line or one of the two above, with its reason.</summary>
    internal const string ExemptionMarker = "spacing: exempt";

    internal sealed record Finding(int Line, string Token);

    internal static IReadOnlyList<Finding> Scan(string razor)
    {
        var rawLines = razor.Split('\n');
        var code = RazorComment().Replace(razor, comment => new string('\n', comment.Value.Count(c => c == '\n')));
        var codeLines = code.Split('\n');

        // A line is exempt when it, or one of the two above it, carries the marker.
        bool IsExempt(int index) => Enumerable.Range(Math.Max(0, index - 2), Math.Min(index, 2) + 1)
            .Any(line => rawLines[line].Contains(ExemptionMarker, StringComparison.Ordinal));

        return codeLines
            .Select((line, index) => (Line: line, Index: index))
            .Where(entry => !IsExempt(entry.Index))
            .SelectMany(entry => VerticalMargin().Matches(entry.Line).Select(match => new Finding(entry.Index + 1, match.Value)))
            .ToList();
    }

    [GeneratedRegex(@"@\*.*?\*@|<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex RazorComment();

    [GeneratedRegex(@"(?<![\w-])(?:mt|mb|my|ma)-n?(?:1[0-6]|[1-9])(?![\w-])")]
    private static partial Regex VerticalMargin();
}
