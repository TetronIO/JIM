// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Guards the vocabulary rule documented in <c>engineering/DEVELOPER_GUIDE.md</c> > Vocabulary (#1667): "CSO"
/// and "MVO" (and their plurals) may only appear in a tight table column header or a stat chip that would
/// otherwise wrap, with the full name available as a tooltip. Everywhere else in the portal's markup the full
/// name ("Connected System Object" / "Metaverse Object") is written out.
/// <para>
/// This is a source-shape test rather than a bUnit render test, for the same reason as
/// <see cref="SearchFieldConventionTests"/>: every individual page renders correctly in isolation, and the
/// defect this catches is a *new* page reintroducing the abbreviation in prose. Only a sweep over the whole of
/// <c>src/JIM.Web</c> can see that.
/// </para>
/// <para>
/// The scanner is deliberately simple (a per-line heuristic, not a Razor parser), matching the brief in issue
/// #1668: it looks at each line carrying the abbreviation and exempts it when the line itself carries one of
/// the four allowed shapes below, or falls inside a Razor/HTML comment or an <c>@code</c> block. A line that
/// does not fits none of these is a genuine offender. This will not catch an abbreviation split unnaturally
/// across two lines; none exists today, and doing better would mean parsing Razor rather than scanning it.
/// </para>
/// </summary>
[TestFixture]
public class VocabularyAbbreviationScanTests
{
    private static readonly Regex AbbreviationPattern = new(@"\bCSOs?\b|\bMVOs?\b", RegexOptions.Compiled);

    /// <summary>
    /// A line is exempt when it is (or is part of) one of the allowed shapes: the native HTML <c>title</c>
    /// attribute, a <c>MudTooltip</c>'s <c>Text</c> attribute, a literal <c>&lt;th&gt;</c>, or JIM's own
    /// column-header component/template (<c>VirtualisedSortHeader</c>, <c>HeaderTemplate</c>).
    /// </summary>
    private static readonly Regex ExemptShapePattern = new(
        "title=\"|<MudTooltip|Text=\"|<th[ >]|VirtualisedSortHeader|HeaderTemplate",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Test]
    public void RazorMarkup_AcrossPagesAndShared_SpellsOutConnectedSystemObjectAndMetaverseObjectInFull()
    {
        var repositoryRoot = FindRepositoryRoot();
        var directories = new[]
        {
            Path.Join(repositoryRoot, "src", "JIM.Web", "Pages"),
            Path.Join(repositoryRoot, "src", "JIM.Web", "Shared")
        };

        foreach (var directory in directories)
            Assert.That(Directory.Exists(directory), Is.True, $"Expected to find JIM.Web sources at '{directory}'.");

        var offenders = directories
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.razor", SearchOption.AllDirectories))
            .SelectMany(path => FindOffendingLines(path, repositoryRoot))
            .OrderBy(o => o.RelativePath, StringComparer.Ordinal)
            .ThenBy(o => o.LineNumber)
            .ToList();

        if (offenders.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine($"{offenders.Count} occurrence(s) of \"CSO\"/\"MVO\" found in portal prose. Write the full")
            .AppendLine("name (\"Connected System Object\" / \"Metaverse Object\") instead; the abbreviation is only")
            .AppendLine("allowed in a table column header or a stat chip that would otherwise wrap, with the full")
            .AppendLine("name available as a tooltip. See engineering/DEVELOPER_GUIDE.md > Vocabulary.")
            .AppendLine();

        foreach (var offender in offenders)
            message.AppendLine($"  {offender.RelativePath}:{offender.LineNumber}  {offender.Line.Trim()}");

        Assert.Fail(message.ToString());
    }

    private static IEnumerable<Offender> FindOffendingLines(string path, string repositoryRoot)
    {
        var lines = File.ReadAllLines(path);
        var relativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
        var inCodeBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // The @code block is the last top-level block in a JIM component, so once it starts, everything
            // to the end of the file is C# rather than markup and is out of scope for this scanner.
            if (!inCodeBlock && trimmed.StartsWith("@code", StringComparison.Ordinal))
            {
                inCodeBlock = true;
                continue;
            }

            if (inCodeBlock)
                continue;

            if (!AbbreviationPattern.IsMatch(line))
                continue;

            if (IsCommentLine(trimmed))
                continue;

            if (ExemptShapePattern.IsMatch(line))
                continue;

            yield return new Offender(relativePath, i + 1, line);
        }
    }

    /// <summary>
    /// JIM's Razor comments are single-line (box-drawing section headers and inline explanations; see
    /// "Razor comments" in <c>src/JIM.Web/CLAUDE.md</c>), and its HTML comments in this codebase are likewise
    /// written on one line, so a start-of-line check is enough without tracking multi-line comment state.
    /// </summary>
    private static bool IsCommentLine(string trimmedLine) =>
        trimmedLine.StartsWith("@*", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("<!--", StringComparison.Ordinal);

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>JIM.sln</c>, the same way
    /// <c>ThemeContrastTests</c> locates <c>site.css</c>: the test reads source files, so it needs the
    /// repository rather than the output directory.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }

    private sealed record Offender(string RelativePath, int LineNumber, string Line);
}
