// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Guards the page-description convention documented in <c>src/JIM.Web/CLAUDE.md</c> > "Page descriptions": what a
/// page is for is said once, from a <c>&lt;PageInfo /&gt;</c> info button at the end of the page's title, never from
/// a grey intro paragraph sitting under it.
/// <para>
/// This is a source-shape test rather than a bUnit render test, for the same reason as
/// <see cref="SearchFieldConventionTests"/>: the defect it prevents (a new page growing its own
/// <c>Typo.subtitle1</c> intro paragraph instead of using the shared info button) is invisible to a per-component
/// test, because every individual page renders correctly in isolation. Only a sweep over every page file can catch
/// the *absence* of the convention on a page that never adopted it.
/// </para>
/// </summary>
[TestFixture]
public class PageDescriptionConventionTests
{
    /// <summary>
    /// A page that deliberately keeps an intro paragraph (a landing page orienting a first-time visitor, or a
    /// design specimen) opts out with this marker on a line directly above the paragraph, stating why. Deliberately
    /// a visible comment rather than an allowlist in this file, so the justification travels with the markup.
    /// </summary>
    private const string ExemptionMarker = "page-description: exempt";

    /// <summary>
    /// Marks the start of a page's title. The scan begins here so that a page-level heading is found before any
    /// heading nested deeper in the page (e.g. a tab's own <c>Typo.h5</c> sub-heading).
    /// </summary>
    private const string TitleMarker = "Typo.h3";

    /// <summary>
    /// Opening tags of the components that mark the end of the "title area": once one of these appears, whatever
    /// follows is page content, not part of describing what the page is for. A <c>Typo.subtitle1</c> found before
    /// the first of these is a candidate intro paragraph; one found after is a section description inside the
    /// page's own content, which is outside this convention.
    /// </summary>
    private static readonly string[] ContentMarkers =
    [
        "<MudPaper",
        "<NavigableMudTabs",
        "<VirtualisedDataGrid",
        "<MudTable",
        "<MudGrid"
    ];

    /// <summary>How many lines above the offending line are searched for <see cref="ExemptionMarker"/>.</summary>
    private const int ExemptionMarkerLookBehindLines = 3;

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    [Test]
    public void RoutablePages_HaveNoIntroParagraphBetweenTitleAndFirstContentBlock()
    {
        var pagesRoot = Path.Join(RepositoryRoot.Value, "src", "JIM.Web", "Pages");
        Assert.That(Directory.Exists(pagesRoot), Is.True, $"Expected to find JIM.Web pages at '{pagesRoot}'.");

        var offenders = Directory
            .EnumerateFiles(pagesRoot, "*.razor", SearchOption.AllDirectories)
            .Where(HasPageDirective)
            .SelectMany(FindIntroParagraphs)
            .OrderBy(o => o.RelativePath, StringComparer.Ordinal)
            .ThenBy(o => o.LineNumber)
            .ToList();

        if (offenders.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine($"{offenders.Count} page intro paragraph(s) found. Use <PageInfo /> in the title instead:")
            .AppendLine();

        foreach (var offender in offenders)
            message.AppendLine($"  {offender.RelativePath}:{offender.LineNumber}");

        message
            .AppendLine()
            .AppendLine("What a page is for is said once, from a <PageInfo /> info button at the end of the page's")
            .AppendLine("title; see src/JIM.Web/CLAUDE.md > \"Page descriptions\". A grey Typo.subtitle1 paragraph")
            .AppendLine("under the title is the same anti-pattern as the dismissible alert it replaced.")
            .AppendLine()
            .AppendLine($"If the paragraph is deliberate (a landing page orienting a first-time visitor, or a")
            .AppendLine($"design specimen), add a Razor comment containing \"{ExemptionMarker}\" directly above it,")
            .AppendLine("saying why.");

        Assert.Fail(message.ToString());
    }

    private static bool HasPageDirective(string path) =>
        File.ReadLines(path).Any(line => line.TrimStart().StartsWith("@page", StringComparison.Ordinal));

    /// <summary>
    /// Finds every <c>Typo.subtitle1</c> line that sits between a page's title and the first content block, and
    /// has not been exempted.
    /// </summary>
    private static IEnumerable<Offender> FindIntroParagraphs(string path)
    {
        var lines = File.ReadAllLines(path);
        var relativePath = Path.GetRelativePath(RepositoryRoot.Value, path).Replace('\\', '/');

        var titleLine = Array.FindIndex(lines, l => l.Contains(TitleMarker, StringComparison.Ordinal));
        if (titleLine < 0)
            yield break;

        for (var i = titleLine + 1; i < lines.Length; i++)
        {
            if (ContentMarkers.Any(marker => lines[i].Contains(marker, StringComparison.Ordinal)))
                yield break;

            if (!lines[i].Contains("Typo.subtitle1", StringComparison.Ordinal))
                continue;

            if (HasExemptionMarker(lines, i))
                continue;

            yield return new Offender(relativePath, i + 1);
        }
    }

    private static bool HasExemptionMarker(IReadOnlyList<string> lines, int lineIndex)
    {
        var firstLine = Math.Max(0, lineIndex - ExemptionMarkerLookBehindLines);

        for (var i = firstLine; i < lineIndex; i++)
        {
            if (lines[i].Contains(ExemptionMarker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>JIM.sln</c>. The test reads source
    /// files, so it needs the repository rather than the output directory.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }

    private sealed record Offender(string RelativePath, int LineNumber);
}
