// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using System.Text.RegularExpressions;
using JIM.PostgresData;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// <see cref="LikePattern"/> escapes the <c>LIKE</c> metacharacters in administrator-typed text (#2053), and every
/// <c>LIKE</c>/<c>ILIKE</c> pattern in JIM.PostgresData is built by it.
/// </summary>
[TestFixture]
public class LikePatternTests
{
    [TestCase("svc_", @"svc\_")]
    [TestCase("100%", @"100\%")]
    [TestCase(@"DOMAIN\user", @"DOMAIN\\user")]
    [TestCase(@"a\_%b", @"a\\\_\%b")]
    [TestCase("plain", "plain")]
    public void Escape_EscapesEveryMetacharacter(string text, string expected)
    {
        Assert.That(LikePattern.Escape(text), Is.EqualTo(expected));
    }

    [Test]
    public void ContainsStartsWithEndsWith_WrapTheEscapedText()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LikePattern.Contains("a_b"), Is.EqualTo(@"%a\_b%"));
            Assert.That(LikePattern.StartsWith("a_b"), Is.EqualTo(@"a\_b%"));
            Assert.That(LikePattern.EndsWith("a_b"), Is.EqualTo(@"%a\_b"));
        }
    }

    [TestCase("Fin*", "Fin%")]
    [TestCase("*_ops*", @"%\_ops%")]
    [TestCase("100%", @"100\%")]
    [TestCase("Disabled", "Disabled")]
    public void Wildcard_TurnsAsterisksIntoPercentAndEscapesTheRest(string text, string expected)
    {
        Assert.That(LikePattern.Wildcard(text), Is.EqualTo(expected));
    }

    /// <summary>
    /// A pattern built by interpolating text between <c>%</c> signs (<c>$"%{search}%"</c>) is a pattern whose
    /// <c>%</c> and <c>_</c> are wildcards; there were about twenty in JIM.PostgresData before this sweep.
    /// </summary>
    [Test]
    public void NoRepository_BuildsALikePatternByInterpolation()
    {
        var sourceRoot = Path.Join(FindRepositoryRoot(), "src", "JIM.PostgresData");
        var interpolatedWildcard = new Regex(@"\$""%?\{[^}]+\}%|\$""%\{", RegexOptions.Compiled);

        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "Migrations"))
            .Where(path => Path.GetFileName(path) != $"{nameof(LikePattern)}.cs")
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(l => interpolatedWildcard.IsMatch(l.line))
                .Select(l => $"{Path.GetRelativePath(sourceRoot, path)}:{l.index + 1}"))
            .ToList();

        Assert.That(offenders, Is.Empty, "build LIKE/ILIKE patterns with LikePattern.Contains/StartsWith/EndsWith: " + string.Join(", ", offenders));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory != null && !File.Exists(Path.Join(directory.FullName, "JIM.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not locate the repository root from the test assembly's location.");
        return directory!.FullName;
    }
}
