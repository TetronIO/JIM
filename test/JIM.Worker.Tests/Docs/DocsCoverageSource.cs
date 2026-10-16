// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Docs;

/// <summary>
/// What the documentation coverage tests read from the repository: the docs pages themselves, and the allow-list of
/// deliberate exceptions in <c>docs/.conformance/coverage.psd1</c>. That allow-list is shared with
/// <c>scripts/Test-DocsCoverage.ps1</c>, which checks the rules that need no compiled code, so every coverage rule
/// keeps its exceptions in one file (engineering/DOCUMENTATION_STANDARD.md, "Coverage"; #1454).
/// </summary>
internal static class DocsCoverageSource
{
    private static readonly Lazy<string> Root = new(FindRepositoryRoot);

    /// <summary>The repository root, found by walking up from the test assembly to <c>JIM.sln</c>.</summary>
    public static string RepositoryRoot => Root.Value;

    /// <summary>A path relative to the repository root, in the form the docs and failure messages use.</summary>
    public static string FullPath(string relativePath) => Path.Join(RepositoryRoot, relativePath);

    /// <summary>
    /// The first cell of every row of every table on a page whose header's first cell is one of
    /// <paramref name="headers"/>, with its line number. Code formatting and bold are removed from the cell, so
    /// <c>`Instance.Name`</c> and <c>**Host**</c> read as <c>Instance.Name</c> and <c>Host</c>. Tables inside fenced
    /// code blocks are not tables, and are skipped.
    /// </summary>
    public static List<(string Name, int Line)> ReadTableFirstColumn(string relativePath, params string[] headers)
    {
        var path = FullPath(relativePath);
        Assert.That(File.Exists(path), Is.True, $"Expected the page '{relativePath}'.");

        var lines = File.ReadAllLines(path);
        var entries = new List<(string, int)>();
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || !IsTableRow(lines[i]) || i + 1 >= lines.Length || !IsSeparatorRow(lines[i + 1]))
                continue;

            var header = FirstCell(lines[i]);
            var rowIndex = i + 2;
            for (; rowIndex < lines.Length && IsTableRow(lines[rowIndex]); rowIndex++)
            {
                if (headers.Contains(header, StringComparer.Ordinal))
                    entries.Add((FirstCell(lines[rowIndex]), rowIndex + 1));
            }

            i = rowIndex - 1;
        }

        return entries;
    }

    /// <summary>
    /// The entries of one allow-list in <c>docs/.conformance/coverage.psd1</c> (a hashtable under <c>Allow</c>),
    /// each with the reason given for it. Reads only the form that file uses, one <c>'name' = 'reason'</c> pair
    /// to a line, and fails on anything else rather than silently missing an entry.
    /// </summary>
    public static Dictionary<string, string> ReadAllowList(string listName)
    {
        const string allowListPath = "docs/.conformance/coverage.psd1";
        var lines = File.ReadAllLines(FullPath(allowListPath));
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, $@"^\s*{Regex.Escape(listName)}\s*=\s*@\{{"));
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"Expected an allow-list named '{listName}' under Allow in {allowListPath}.");

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Regex.IsMatch(lines[start], @"@\{\s*\}\s*$"))
            return entries;

        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "}")
                return entries;
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var entry = Regex.Match(line, @"^'((?:[^']|'')*)'\s*=\s*'((?:[^']|'')*)'$");
            Assert.That(entry.Success, Is.True,
                $"{allowListPath}:{i + 1} is not a 'name' = 'reason' entry, so the coverage tests cannot read it: {line}");
            entries[entry.Groups[1].Value.Replace("''", "'")] = entry.Groups[2].Value.Replace("''", "'");
        }

        Assert.Fail($"The allow-list '{listName}' in {allowListPath} is never closed.");
        return entries;
    }

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith('|');

    private static bool IsSeparatorRow(string line) => Regex.IsMatch(line, @"^\s*\|(\s*:?-+:?\s*\|)+\s*$");

    private static string FirstCell(string row)
    {
        var cell = Regex.Split(row.Trim().TrimStart('|'), @"(?<!\\)\|")[0];
        return cell.Replace("`", "").Replace("**", "").Trim();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate JIM.sln by walking up from the test output directory.");
        return directory!.FullName;
    }
}
