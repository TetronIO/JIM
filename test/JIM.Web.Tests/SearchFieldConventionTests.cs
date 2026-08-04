// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Guards the search-as-you-type convention documented in <c>src/JIM.Web/CLAUDE.md</c>: a box that filters a list as
/// the user types is a <c>&lt;SearchField&gt;</c>, never a hand-rolled <c>MudTextField</c>.
/// <para>
/// This is a source-shape test rather than a bUnit render test on purpose. The defect it prevents (a
/// <c>MudTextField</c> without <c>Immediate="true"</c> commits only on blur, so the list does not filter until focus
/// leaves the box) is invisible to a per-component test: every individual box renders correctly in isolation, and the
/// fault is that a *new* page hand-rolls another one. Only a sweep over the whole of <c>src/JIM.Web</c> can catch that,
/// and the convention has now regressed twice while documented as prose (issue #864).
/// </para>
/// </summary>
[TestFixture]
public class SearchFieldConventionTests
{
    /// <summary>
    /// A field that is one criterion among several in a form submitted by an explicit button is not a
    /// search-as-you-type box and is outside the convention. Such a field opts out with this marker on the line (or
    /// the two lines) immediately above it, stating why. The marker is deliberately a visible comment rather than an
    /// allowlist in this file: the justification then travels with the markup and shows up in the diff that adds it.
    /// </summary>
    private const string ExemptionMarker = "search-convention: exempt";

    /// <summary>
    /// The component that owns the convention. It is the one place a search-shaped <c>MudTextField</c> is expected.
    /// </summary>
    private const string ComponentFileName = "SearchField.razor";

    /// <summary>How many lines above the tag are searched for <see cref="ExemptionMarker"/>.</summary>
    private const int ExemptionMarkerLookBehindLines = 2;

    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    [Test]
    public void SearchBoxes_AcrossJimWeb_UseTheSharedSearchFieldComponent()
    {
        var webRoot = Path.Join(RepositoryRoot.Value, "src", "JIM.Web");
        Assert.That(Directory.Exists(webRoot), Is.True, $"Expected to find JIM.Web sources at '{webRoot}'.");

        var offenders = Directory
            .EnumerateFiles(webRoot, "*.razor", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ComponentFileName, StringComparison.Ordinal))
            .SelectMany(FindUnmigratedSearchBoxes)
            .OrderBy(o => o.RelativePath, StringComparer.Ordinal)
            .ThenBy(o => o.LineNumber)
            .ToList();

        if (offenders.Count == 0)
            return;

        var message = new StringBuilder()
            .AppendLine($"{offenders.Count} hand-rolled search box(es) found. Use <SearchField /> instead:")
            .AppendLine();

        foreach (var offender in offenders)
            message.AppendLine($"  {offender.RelativePath}:{offender.LineNumber}  {offender.Description}");

        message
            .AppendLine()
            .AppendLine("A MudTextField commits its value on blur, so a hand-rolled search box does not filter until")
            .AppendLine("focus leaves it. <SearchField /> bakes in Immediate=\"true\" and a debounce; see")
            .AppendLine("src/JIM.Web/CLAUDE.md > \"Search and filter boxes\".")
            .AppendLine()
            .AppendLine($"If the field is a criterion in a form submitted by a button, it is outside the convention:")
            .AppendLine($"add a Razor comment containing \"{ExemptionMarker}\" directly above it, saying why.");

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// A search box must not be able to opt out of search-as-you-type. <see cref="ExemptionMarker"/> exists for fields
    /// that are not search boxes at all; it is not a way to keep one that commits on blur.
    /// </summary>
    [Test]
    public void SearchField_DoesNotLetCallSitesTurnOffImmediateCommit()
    {
        var componentPath = Path.Join(RepositoryRoot.Value, "src", "JIM.Web", "Shared", ComponentFileName);
        Assert.That(File.Exists(componentPath), Is.True, $"Expected the shared component at '{componentPath}'.");

        var source = File.ReadAllText(componentPath);

        Assert.Multiple(() =>
        {
            Assert.That(
                Regex.IsMatch(source, @"Immediate\s*=\s*""true"""),
                Is.True,
                "SearchField must hard-code Immediate=\"true\" on its MudTextField.");

            Assert.That(
                Regex.IsMatch(source, @"public\s+bool\s+Immediate\s*\{"),
                Is.False,
                "SearchField must not expose an Immediate parameter; a call site could then reinstate blur-commit.");

            Assert.That(
                Regex.IsMatch(source, @"CaptureUnmatchedValues\s*=\s*true"),
                Is.False,
                "SearchField must not splat unmatched attributes onto its MudTextField; Immediate=\"false\" would " +
                "pass straight through and silently revert that instance to blur-commit. Add an explicit parameter " +
                "for anything a call site legitimately needs to set.");
        });
    }

    /// <summary>
    /// Finds every search-shaped <c>MudTextField</c> in a Razor file that has neither been migrated nor exempted.
    /// </summary>
    private static IEnumerable<Offender> FindUnmigratedSearchBoxes(string path)
    {
        var lines = File.ReadAllLines(path);
        var relativePath = Path.GetRelativePath(RepositoryRoot.Value, path).Replace('\\', '/');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("<MudTextField", StringComparison.Ordinal))
                continue;

            var tag = ReadTag(lines, i);
            if (!IsSearchShaped(tag, out var description))
                continue;

            if (HasExemptionMarker(lines, i))
                continue;

            yield return new Offender(relativePath, i + 1, description);
        }
    }

    /// <summary>
    /// Reads a Razor tag that may span lines, stopping at its closing angle bracket. A line ending in "=&gt;" is a
    /// lambda mid-attribute (e.g. <c>ValueChanged="@(s =&gt; OnSearch(s))"</c>), not the end of the tag.
    /// </summary>
    private static string ReadTag(IReadOnlyList<string> lines, int startIndex)
    {
        var builder = new StringBuilder();

        for (var i = startIndex; i < lines.Count; i++)
        {
            var line = lines[i];
            builder.AppendLine(line);

            var trimmed = line.TrimEnd();
            if (trimmed.EndsWith("=>", StringComparison.Ordinal))
                continue;

            if (trimmed.EndsWith(">", StringComparison.Ordinal))
                break;
        }

        return builder.ToString();
    }

    /// <summary>
    /// A field is a search box when it wears the magnifier adornment, or when its label or placeholder opens with
    /// "Search" or "Filter". Both are the user-visible signals that typing in it narrows a list.
    /// </summary>
    private static bool IsSearchShaped(string tag, out string description)
    {
        if (tag.Contains("Icons.Material.Filled.Search", StringComparison.Ordinal))
        {
            description = "carries the search adornment";
            return true;
        }

        var caption = Regex.Match(tag, @"(?:Label|Placeholder)\s*=\s*""\s*(Search|Filter)[^""]*""");
        if (caption.Success)
        {
            description = $"has a search caption ({caption.Value.Trim()})";
            return true;
        }

        description = string.Empty;
        return false;
    }

    private static bool HasExemptionMarker(IReadOnlyList<string> lines, int tagStartIndex)
    {
        var firstLine = Math.Max(0, tagStartIndex - ExemptionMarkerLookBehindLines);

        for (var i = firstLine; i < tagStartIndex; i++)
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

    private sealed record Offender(string RelativePath, int LineNumber, string Description);
}
