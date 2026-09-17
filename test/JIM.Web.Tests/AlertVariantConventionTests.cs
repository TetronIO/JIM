// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Holds the design system's alert rule: a <c>MudAlert</c> takes MudBlazor's default variant (Text), so no
/// call site names a <c>Variant</c> at all. The rule used to be the opposite (every alert Outlined), spelt
/// out by hand on 158 alerts; a default that needs no attribute cannot drift the way that did. The theme
/// specimen page is exempt, because showing every variant side by side is its job.
/// </summary>
[TestFixture]
public class AlertVariantConventionTests
{
    private static readonly string[] ExemptFiles = ["Pages/Admin/ThemePreview.razor"];

    // A MudAlert opening tag, tolerant of '>' inside quoted attribute values such as lambdas.
    private static readonly Regex AlertTag = new(@"<MudAlert\b(?:[^>""']|""[^""]*""|'[^']*')*>", RegexOptions.Compiled);

    [Test]
    public void EveryMudAlert_TakesTheDefaultVariant()
    {
        var webRoot = Path.Join(FindRepositoryRoot(), "src", "JIM.Web");
        Assert.That(Directory.Exists(webRoot), Is.True, $"Expected to find JIM.Web sources at '{webRoot}'.");

        var offenders = Directory
            .EnumerateFiles(webRoot, "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Path: path, Relative: Path.GetRelativePath(webRoot, path).Replace('\\', '/')))
            .Where(file => !ExemptFiles.Contains(file.Relative))
            .SelectMany(file =>
            {
                var source = File.ReadAllText(file.Path);
                return AlertTag.Matches(source)
                    .Where(match => match.Value.Contains("Variant="))
                    .Select(match => $"{file.Relative}:{source[..match.Index].Count(c => c == '\n') + 1}");
            })
            .ToList();

        Assert.That(offenders, Is.Empty,
            "A MudAlert names a Variant. Alerts take the default variant; remove the attribute " +
            "(see src/JIM.Web/CLAUDE.md > Alerts):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
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
